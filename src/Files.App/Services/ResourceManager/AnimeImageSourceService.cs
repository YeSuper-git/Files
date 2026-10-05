// Copyright (c) Files Community. Licensed under the MIT License.
using System.IO;
using Windows.Graphics.Imaging;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using System.Text.RegularExpressions;

namespace Files.App.Services.ResourceManager;

public sealed record AnimeSourceImage(string Kind, Uri OriginalUrl, string? Episode, string Label);
public sealed record AnimeSourceWork(string Title, IReadOnlyList<string> Aliases, IReadOnlyList<AnimeSourceImage> Images);

public sealed class AnimeImageSourceService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumImageBytes = 32 * 1024 * 1024;

    public static string ResolvePageAddress(string address, string prefix)
    {
        address = address.Trim();
        if (Uri.TryCreate(address, UriKind.Absolute, out _)) throw new InvalidDataException("Enter only the page suffix.");
        if (address.Contains("://", StringComparison.Ordinal) || address.Length == 0 || !Uri.TryCreate(prefix.Trim(), UriKind.Absolute, out var origin) || origin.Scheme != "https")
            throw new InvalidDataException("Enter an HTTPS URL or configure an HTTPS website prefix.");
        var combined = new Uri(origin.AbsoluteUri.TrimEnd('/') + "/" + address.TrimStart('/'));
        if (combined.Scheme != "https" || combined.Host != origin.Host) throw new InvalidDataException("Invalid page address.");
        return combined.AbsoluteUri;
    }

    public sealed record PageImage(Uri Url, string Label, string Evidence, string? SuggestedName = null);

    public static string GetImageFileName(PageImage image)
        => image.SuggestedName ?? Uri.UnescapeDataString(Path.GetFileName(image.Url.AbsolutePath));

    public static IReadOnlyList<PageImage> FilterImages(IEnumerable<PageImage> images, IEnumerable<string> includedNames, bool matchAll = false)
    {
        var keywords = includedNames.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray();
        return images.Where(image => keywords.Length == 0 || (matchAll ? keywords.All(keyword => GetImageFileName(image).Contains(keyword, StringComparison.OrdinalIgnoreCase)) : keywords.Any(keyword => GetImageFileName(image).Contains(keyword, StringComparison.OrdinalIgnoreCase)))).ToArray();
    }

    public async Task<IReadOnlyList<PageImage>> FilterDimensionsAsync(IEnumerable<PageImage> images, int minimumWidth, int minimumHeight, CancellationToken token)
    {
        if (minimumWidth == 0 && minimumHeight == 0) return images.ToArray();
        var result = new List<PageImage>();
        foreach (var image in images)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var bytes = await ReadRemoteAsync(image.Url, MaximumImageBytes, token);
                using var stream = new MemoryStream(bytes);
                using var randomAccess = stream.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(randomAccess).AsTask(token);
                if (decoder.PixelWidth >= minimumWidth && decoder.PixelHeight >= minimumHeight) result.Add(image);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Runtime.InteropServices.COMException or TaskCanceledException) { }
        }
        return result;
    }

    public static IReadOnlyList<IReadOnlyList<PageImage>> GroupImages(IEnumerable<PageImage> images, bool groupByPrefix)
        => images.GroupBy(image => groupByPrefix ? GetImageSetKey(image) : image.Url.AbsoluteUri, StringComparer.Ordinal)
            .Select(group => (IReadOnlyList<PageImage>)group.ToArray()).ToArray();

    public static string GetImageSetKey(PageImage image)
    {
        var name = Path.GetFileNameWithoutExtension(GetImageFileName(image));
        return name.Length >= 8 ? name[..8] : image.Url.AbsoluteUri;
    }

    public async Task<IReadOnlyList<PageImage>> InspectPageAsync(string address, CancellationToken token)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != "https")
            throw new InvalidDataException("Use an HTTPS page address.");
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var origin = response.RequestMessage?.RequestUri ?? url;
        if (origin.Scheme != "https") throw new InvalidDataException("HTTPS is required.");
        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return [new(origin, Path.GetFileName(origin.AbsolutePath), "direct")];
        var bytes = await ReadLimitedAsync(response, MaximumManifestBytes, token);
        var encoding = System.Text.Encoding.UTF8;
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = System.Text.Encoding.GetEncoding(charset); } catch (ArgumentException) { }
        }
        return ParsePage(encoding.GetString(bytes), origin);
    }

    public static IReadOnlyList<PageImage> ParsePage(string html, Uri origin)
    {
        var result = new Dictionary<string, PageImage>(StringComparer.Ordinal);
        var timeout = TimeSpan.FromSeconds(2);
        if (origin.Host.Equals("www.lune-soft.jp", StringComparison.OrdinalIgnoreCase) || origin.Host.Equals("lune-soft.jp", StringComparison.OrdinalIgnoreCase))
        {
            var gallery = Regex.Match(html, @"<section\b[^>]*\bid\s*=\s*[""']gallery[""'][^>]*>(.*?)</section\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, timeout);
            var product = Regex.Match(html, @"<th\b[^>]*>\s*品番\s*</th>\s*<td\b[^>]*>\s*([A-Z]+\d+)\s*</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline, timeout);
            if (gallery.Success)
            {
                var galleryImages = ParsePage(gallery.Groups[1].Value, origin);
                if (!product.Success) return galleryImages;
                var code = product.Groups[1].Value.ToUpperInvariant();
                return galleryImages.Select((image, index) => image with { SuggestedName = $"{code}_{index + 1:00}{Path.GetExtension(image.Url.AbsolutePath)}" }).ToArray();
            }
        }
        html = Regex.Replace(html, @"<!--.*?-->|<(script|style)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, timeout);
        Dictionary<string, string> Attributes(string tag)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(tag, "([\\w:-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))", RegexOptions.None, timeout))
                values[match.Groups[1].Value] = WebUtility.HtmlDecode(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value);
            return values;
        }
        var baseTag = Regex.Match(html, @"<base\b[^>]*>", RegexOptions.IgnoreCase, timeout);
        if (baseTag.Success && Attributes(baseTag.Value).TryGetValue("href", out var baseAddress) && Uri.TryCreate(origin, baseAddress, out var baseUri) && baseUri.Scheme == "https") origin = baseUri;
        void Add(string address, string label, string evidence)
        {
            if (result.Count >= 300 || string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(origin, address.Trim(), out var url) || url.Scheme != "https") return;
            var key = url.GetLeftPart(UriPartial.Path) + url.Query;
            if (!result.TryGetValue(key, out var previous) || previous.Evidence == "unknown") result[key] = new(url, label, evidence);
        }
        foreach (Match tag in Regex.Matches(html, @"<(?:img|source|a|meta)\b[^>]*>", RegexOptions.IgnoreCase, timeout))
        {
            var attributes = Attributes(tag.Value);
            var label = attributes.GetValueOrDefault("alt", attributes.GetValueOrDefault("title", string.Empty));
            if (tag.Value.StartsWith("<a", StringComparison.OrdinalIgnoreCase))
            {
                if (attributes.TryGetValue("href", out var href) && Uri.TryCreate(origin, href, out var link)
                    && new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(link.AbsolutePath), StringComparer.OrdinalIgnoreCase)) Add(href, label, "link");
            }
            else if (tag.Value.StartsWith("<meta", StringComparison.OrdinalIgnoreCase))
            {
                if (attributes.GetValueOrDefault("property", attributes.GetValueOrDefault("name", string.Empty)) is "og:image" or "twitter:image") Add(attributes.GetValueOrDefault("content", ""), label, "unknown");
            }
            else
            {
                foreach (var name in new[] { "src", "data-src", "data-lazy-src", "data-original", "data-full" })
                    if (attributes.TryGetValue(name, out var address)) Add(address, label, name is "data-original" or "data-full" ? "declared" : "unknown");
                foreach (var name in new[] { "srcset", "data-srcset" })
                {
                    if (!attributes.TryGetValue(name, out var set)) continue;
                    var entries = set.Split(',').Select(entry => entry.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        .Where(parts => parts.Length > 0).Select(parts => (Address: parts[0], Size: parts.Length > 1 && double.TryParse(parts[1].TrimEnd('w', 'x'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : 0));
                    var largest = entries.OrderByDescending(entry => entry.Size).FirstOrDefault();
                    if (largest.Address is not null) Add(largest.Address, label, "largest");
                }
            }
        }
        return result.Values.ToArray();
    }

    public async Task<IReadOnlyList<AnimeSourceWork>> SearchAsync(string source, string query, CancellationToken token)
    {
        Uri? origin = null;
        byte[] bytes;
        if (Uri.TryCreate(source.Replace("{query}", Uri.EscapeDataString(query), StringComparison.Ordinal), UriKind.Absolute, out var uri) && uri.Scheme == "https")
        {
            origin = uri;
            bytes = await ReadRemoteAsync(uri, MaximumManifestBytes, token);
        }
        else if (Path.IsPathFullyQualified(source) && File.Exists(source))
        {
            if (new FileInfo(source).Length > MaximumManifestBytes) throw new InvalidDataException("Manifest exceeds 4 MB.");
            bytes = await File.ReadAllBytesAsync(source, token);
        }
        else throw new InvalidDataException("Use an HTTPS JSON URL or an existing local JSON file.");
        var works = Parse(bytes, origin);
        return source.Contains("{query}", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(query)
            ? works : works.Where(work => work.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || work.Aliases.Any(alias => alias.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public static IReadOnlyList<AnimeSourceWork> Parse(byte[] json, Uri? origin = null)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("works", out var works) || works.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Expected a works array.");
        var result = new List<AnimeSourceWork>();
        foreach (var work in works.EnumerateArray().Take(500))
        {
            var title = Text(work, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var aliases = work.TryGetProperty("aliases", out var names) && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String).Select(name => name.GetString()!).ToArray() : [];
            var images = new List<AnimeSourceImage>();
            if (work.TryGetProperty("images", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray().Take(500))
                {
                    var kind = Text(entry, "kind");
                    if (kind is not ("poster" or "illustration")) continue;
                    var address = Text(entry, "originalUrl");
                    Uri? url = null;
                    if (!Uri.TryCreate(address, UriKind.Absolute, out url) && origin is not null) Uri.TryCreate(origin, address, out url);
                    if (url is null || url.Scheme != "https") continue;
                    var episode = entry.TryGetProperty("episode", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetRawText() : Text(entry, "episode");
                    images.Add(new(kind, url, string.IsNullOrWhiteSpace(episode) ? null : episode, Text(entry, "label")));
                }
            }
            result.Add(new(title, aliases, images));
        }
        return result;
    }

    private static string Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static async Task<byte[]> ReadRemoteAsync(Uri url, int limit, CancellationToken token)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("HTTPS is required.");
        return await ReadLimitedAsync(response, limit, token);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response exceeds the size limit.");
        using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Response exceeds the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return output.ToArray();
    }

    public async Task<string?> ImportAsync(AnimeSourceImage image, string libraryRoot, string folder, string video, string episodeNumber, CancellationToken token, string? sourceNumber = null)
    {
        folder = Path.GetFullPath(folder);
        var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(folder)
            || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(video)), folder, StringComparison.OrdinalIgnoreCase) || !File.Exists(video))
            throw new InvalidDataException("The target must be an existing video in the selected library folder.");
        for (var directory = new DirectoryInfo(folder); directory is not null && directory.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase); directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Linked target folders are not supported.");
        if (!int.TryParse(episodeNumber, out var episode) || episode < 1) throw new InvalidDataException("Invalid episode number.");
        if (sourceNumber is not null && !Regex.IsMatch(sourceNumber, @"^[0-9]{5}$")) throw new InvalidDataException("Invalid image source number.");
        var stem = $"{episode:00}插图" + (sourceNumber is null ? string.Empty : "_" + sourceNumber);
        var bytes = await ReadRemoteAsync(image.OriginalUrl, MaximumImageBytes, token);
        var extension = DetectExtension(bytes);
        var hash = SHA256.HashData(bytes);
        foreach (var existing in Directory.EnumerateFiles(folder).Where(path => Path.GetFileNameWithoutExtension(path).StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
        {
            if (new FileInfo(existing).Length != bytes.Length) continue;
            using var stream = File.OpenRead(existing);
            var existingHash = await SHA256.HashDataAsync(stream, token);
            if (hash.SequenceEqual(existingHash)) return null;
        }
        var existingIndices = Directory.EnumerateFiles(folder).Select(path =>
        {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(path), "^" + Regex.Escape(stem) + @" \((\d+)\)$", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
        });
        var index = checked(existingIndices.DefaultIfEmpty(0).Max() + 1);
        var target = Path.Combine(folder, $"{stem} ({index}){extension}");
        var temporary = Path.Combine(folder, $".files-image-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, false);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string DetectExtension(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff) return ".jpg";
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (data.Length >= 12 && System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WEBP") return ".webp";
        throw new InvalidDataException("Expected a JPEG, PNG or WebP image.");
    }
}
