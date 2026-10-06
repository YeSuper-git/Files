// Copyright (c) Files Community. Licensed under the MIT License.
using Files.App.Services.VideoEditor;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Files.App.Services.ResourceManager;

public sealed class MediaVideoSourceService(VideoProbeService probe)
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly string[] Extensions = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".ts", ".webm", ".mpg", ".mpeg"];
    public sealed record PageVideo(Uri Url, string Name, double? DurationSeconds = null, string? DurationError = null);
    public sealed record ScanResult(IReadOnlyList<PageVideo> Videos, int TooShort, int UnknownDuration);

    public static IReadOnlyList<PageVideo> ParsePage(string html, Uri origin)
    {
        var result = new Dictionary<string, PageVideo>(StringComparer.Ordinal);
        var timeout = TimeSpan.FromSeconds(2);
        html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline, timeout);
        var baseTag = Regex.Match(html, @"<base\b[^>]*\bhref\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase, timeout);
        if (baseTag.Success && Uri.TryCreate(origin, WebUtility.HtmlDecode(baseTag.Groups[1].Value), out var baseUri) && baseUri.Scheme is "http" or "https") origin = baseUri;
        var decoded = WebUtility.HtmlDecode(html).Replace(@"\/", "/");
        foreach (Match match in Regex.Matches(decoded, "[\\\"']([^\\\"'<>\\r\\n]+)[\\\"']", RegexOptions.None, timeout))
        {
            if (!Uri.TryCreate(origin, match.Groups[1].Value.Trim(), out var url) || url.Scheme is not ("http" or "https")
                || !Extensions.Contains(Path.GetExtension(url.AbsolutePath), StringComparer.OrdinalIgnoreCase)) continue;
            result.TryAdd(url.GetLeftPart(UriPartial.Path) + url.Query, new(url, GetDownloadName(url)));
            if (result.Count >= 100) break;
        }
        foreach (Match tag in Regex.Matches(html, @"<(?:video|source)\b[^>]*>", RegexOptions.IgnoreCase, timeout))
        {
            var src = Regex.Match(tag.Value, @"\bsrc\s*=\s*(?:[""']([^""']+)[""']|([^\s>]+))", RegexOptions.IgnoreCase, timeout);
            var address = WebUtility.HtmlDecode(src.Groups[1].Success ? src.Groups[1].Value : src.Groups[2].Value);
            if (!Uri.TryCreate(origin, address, out var url) || url.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(address)) continue;
            if (!Extensions.Contains(Path.GetExtension(url.AbsolutePath), StringComparer.OrdinalIgnoreCase) && !Regex.IsMatch(tag.Value, @"\btype\s*=\s*[""']video/", RegexOptions.IgnoreCase, timeout)) continue;
            result.TryAdd(url.GetLeftPart(UriPartial.Path) + url.Query, new(url, GetDownloadName(url)));
        }
        return result.Values.Take(100).OrderBy(video => video.Name, new EpisodeNameComparer()).ToArray();
    }

    public static string GetDownloadName(Uri url, string? mediaType = null)
    {
        var name = Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath));
        name = new string(name.Select(character => character < 32 || "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
        var stem = Path.GetFileNameWithoutExtension(name);
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) name = "_" + name;
        var extension = mediaType?.ToLowerInvariant() switch { "video/webm" => ".webm", "video/quicktime" => ".mov", "video/x-matroska" => ".mkv", "video/x-msvideo" => ".avi", "video/mpeg" => ".mpeg", _ => ".mp4" };
        return string.IsNullOrWhiteSpace(name) ? "video" + extension : Extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) ? name : name + extension;
    }

    public static bool MeetsMinimumDuration(double? duration, int minimumSeconds)
        => minimumSeconds == 0 || duration is double seconds && double.IsFinite(seconds) && seconds >= minimumSeconds;

    private static HttpRequestMessage Request(Uri url, Uri referer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = referer;
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 FilesMax/1.0");
        return request;
    }

    public async Task<ScanResult> InspectPageAsync(string address, int minimumSeconds, CancellationToken token)
    {
        var url = new Uri(address);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(url, url);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        var origin = response.RequestMessage?.RequestUri ?? url;
        IReadOnlyList<PageVideo> candidates;
        if (Extensions.Contains(Path.GetExtension(origin.AbsolutePath), StringComparer.OrdinalIgnoreCase) || response.Content.Headers.ContentType?.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            candidates = [new(origin, GetDownloadName(origin, response.Content.Headers.ContentType?.MediaType))];
        else
        {
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("Page exceeds size limit.");
            using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (output.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("Page exceeds size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            }
            var encoding = System.Text.Encoding.UTF8;
            try { if (response.Content.Headers.ContentType?.CharSet is { } charset) encoding = System.Text.Encoding.GetEncoding(charset.Trim('"')); } catch (ArgumentException) { }
            candidates = ParsePage(encoding.GetString(output.ToArray()), origin);
        }
        var videos = new List<PageVideo>();
        var tooShort = 0; var unknown = 0;
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            double? duration = null;
            string? durationError = null;
            using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            probeDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            try { duration = (await probe.ProbeAsync(candidate.Url.AbsoluteUri, probeDeadline.Token, origin)).DurationSeconds; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { durationError = Strings.AnimeVideosUnknownDuration.GetLocalizedResource(); }
            catch (Exception ex) when (!token.IsCancellationRequested) { durationError = ex.Message; }
            if (duration is null) unknown++;
            if (MeetsMinimumDuration(duration, minimumSeconds)) videos.Add(candidate with { DurationSeconds = duration, DurationError = durationError });
            else if (duration is not null) tooShort++;
        }
        return new(videos, tooShort, unknown);
    }

    public async Task<string> DownloadAsync(PageVideo video, string libraryRoot, string folder, Uri referer, int minimumSeconds, IProgress<long>? progress, CancellationToken token)
    {
        folder = Path.GetFullPath(folder);
        var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(folder)) throw new InvalidDataException("Invalid target folder.");
        for (var directory = new DirectoryInfo(folder); directory is not null && directory.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase); directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Linked target folders are not supported.");
        var temporary = Path.Combine(folder, $".video-{Guid.NewGuid():N}.tmp");
        try
        {
            using var request = Request(video.Url, referer);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            using (var input = await response.Content.ReadAsStreamAsync(token))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920]; long total = 0; int read; var reportTimer = Stopwatch.StartNew();
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token); total += read;
                    if (reportTimer.ElapsedMilliseconds >= 250) { progress?.Report(total); reportTimer.Restart(); }
                }
            }
            var metadata = await probe.ProbeAsync(temporary, token);
            if (!MeetsMinimumDuration(metadata.DurationSeconds, minimumSeconds)) throw new InvalidDataException(Strings.AnimeVideosTooShort.GetLocalizedResource());
            var name = GetDownloadName(video.Url, response.Content.Headers.ContentType?.MediaType);
            var destination = Path.Combine(folder, name); var index = 1;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { File.Move(temporary, destination); return destination; }
                catch (IOException) when (File.Exists(destination)) { destination = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({index++}){Path.GetExtension(name)}"); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
