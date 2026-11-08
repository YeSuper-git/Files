// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.RegularExpressions;

namespace Files.App.Services.ResourceManager;

public sealed class MikanProjectService
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("https://mikanani.kas.pub/"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    static MikanProjectService()
        => Client.DefaultRequestHeaders.UserAgent.ParseAdd("FilesMax/1.0 (+https://github.com/YeSuper-git/Files)");

    public async Task<IReadOnlyList<MikanAnimeCandidate>> GetSeasonAsync(int year, string season, CancellationToken cancellationToken = default)
    {
        var url = $"Home/BangumiCoverFlowByDayOfWeek?year={year}&seasonStr={Uri.EscapeDataString(season)}";
        var html = await Client.GetStringAsync(url, cancellationToken);
        var results = new List<MikanAnimeCandidate>();
        foreach (Match posterMatch in Regex.Matches(html, "<span\\b(?=[^>]*class=\\\"[^\\\"]*js-expand_bangumi[^\\\"]*\\\")(?=[^>]*data-bangumiid=\\\"(?<id>\\d+)\\\")(?=[^>]*data-src=\\\"(?<poster>[^\\\"]+)\\\")[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!int.TryParse(posterMatch.Groups["id"].Value, out var id))
                continue;

            var titleMatch = Regex.Match(html,
                $"<a\\b[^>]*href=\\\"/Home/Bangumi/{id}\\\"[^>]*>(?<title>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!titleMatch.Success)
                continue;

            var title = DecodeText(titleMatch.Groups["title"].Value);
            var posterUrl = ResolveUrl(WebUtility.HtmlDecode(posterMatch.Groups["poster"].Value));
            if (!results.Any(item => item.Id == id))
                results.Add(new MikanAnimeCandidate(id, title, posterUrl));
        }

        if (results.Count == 0)
        {
            foreach (Match titleMatch in Regex.Matches(html,
                "<a\\b[^>]*href=\\\"/Home/Bangumi/(?<id>\\d+)\\\"[^>]*>(?<title>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
            {
                if (!int.TryParse(titleMatch.Groups["id"].Value, out var id))
                    continue;
                var title = DecodeText(titleMatch.Groups["title"].Value);
                if (!results.Any(item => item.Id == id))
                    results.Add(new MikanAnimeCandidate(id, title, string.Empty));
            }
        }
        return results;
    }

    public async Task<MikanAnimeInfo> GetAnimeAsync(int bangumiId, CancellationToken cancellationToken = default)
    {
        if (bangumiId <= 0)
            throw new ArgumentOutOfRangeException(nameof(bangumiId));

        var html = await Client.GetStringAsync($"Home/Bangumi/{bangumiId}", cancellationToken);
        var titleMatch = Regex.Match(html, "<p\\b[^>]*class=\\\"bangumi-title\\\"[^>]*>(?<title>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var title = titleMatch.Success
            ? DecodeText(Regex.Replace(titleMatch.Groups["title"].Value, "<.*?>", string.Empty))
            : bangumiId.ToString();

        var posterMatch = Regex.Match(html, "class=\\\"bangumi-poster\\\"[^>]*style=\\\"[^\\\"]*url\\(['\\\"]?(?<url>[^)'\\\"]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var posterUrl = posterMatch.Success ? ResolveUrl(WebUtility.HtmlDecode(posterMatch.Groups["url"].Value)) : string.Empty;
        var candidate = new MikanAnimeCandidate(bangumiId, title, posterUrl);

        var synopsisMatch = Regex.Match(html, "class=\\\"header2-desc\\\"[^>]*>(?<text>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var synopsis = synopsisMatch.Success ? DecodeText(Regex.Replace(synopsisMatch.Groups["text"].Value, "<.*?>", " ")) : string.Empty;
        var airingInfo = string.Join("  ·  ", Regex.Matches(html, "<p\\b[^>]*class=\\\"bangumi-info\\\"[^>]*>(?<text>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>()
            .Select(match => DecodeText(Regex.Replace(match.Groups["text"].Value, "<.*?>", " ")))
            .Where(text => text.StartsWith("放送日期", StringComparison.Ordinal) || text.StartsWith("放送开始", StringComparison.Ordinal)));

        var groups = new Dictionary<int, string>();
        foreach (Match groupMatch in Regex.Matches(html,
            "<a\\b[^>]*class=\\\"subgroup-name\\s+subgroup-(?<id>\\d+)\\\"[^>]*>(?<name>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (int.TryParse(groupMatch.Groups["id"].Value, out var id))
                groups[id] = DecodeText(groupMatch.Groups["name"].Value);
        }

        var parsedGroups = new List<MikanSubtitleGroup>();
        foreach (Match section in Regex.Matches(html,
            "<div\\b[^>]*class=\\\"subgroup-text\\\"[^>]*id=\\\"(?<id>\\d+)\\\"[^>]*>(?<body>.*?)(?=<div\\b[^>]*class=\\\"subgroup-scroll-top-|</body>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (!int.TryParse(section.Groups["id"].Value, out var groupId))
                continue;
            var groupName = groups.GetValueOrDefault(groupId, string.Empty);
            var releases = new List<MikanEpisodeRelease>();
            foreach (Match rowMatch in Regex.Matches(section.Groups["body"].Value, "<tr\\b[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var row = rowMatch.Groups["row"].Value;
                var episodeMatch = Regex.Match(row, "href=\\\"/Home/Episode/(?<id>[a-f0-9]+)\\\"[^>]*>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var magnetMatch = Regex.Match(row, "data-clipboard-text=\\\"(?<magnet>magnet:[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!episodeMatch.Success || !magnetMatch.Success)
                    continue;

                var cells = Regex.Matches(row, "<td\\b[^>]*>(?<text>.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                    .Select(match => DecodeText(Regex.Replace(match.Groups["text"].Value, "<.*?>", " ")))
                    .ToArray();
                var magnet = WebUtility.HtmlDecode(magnetMatch.Groups["magnet"].Value).Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
                releases.Add(new MikanEpisodeRelease(
                    episodeMatch.Groups["id"].Value,
                    DecodeText(episodeMatch.Groups["title"].Value),
                    cells.Length > 2 ? cells[2] : string.Empty,
                    cells.Length > 3 ? cells[3] : string.Empty,
                    magnet,
                    ResolveUrl($"/Home/Episode/{episodeMatch.Groups["id"].Value}"),
                    groupName,
                    groupId));
            }
            parsedGroups.Add(new MikanSubtitleGroup(groupId, groupName, releases));
        }

        var bgmMatch = Regex.Match(html, "href=\\\"https?://bgm\\.tv/subject/(?<id>\\d+)\\\"", RegexOptions.IgnoreCase);
        int? subjectId = int.TryParse(bgmMatch.Groups["id"].Value, out var parsedSubjectId) ? parsedSubjectId : null;
        return new MikanAnimeInfo
        {
            Candidate = candidate,
            AiringInfo = airingInfo,
            Synopsis = synopsis,
            BangumiSubjectId = subjectId,
            Groups = parsedGroups,
        };
    }

    public Task<byte[]> DownloadImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidDataException("蜜柑计划返回的海报地址无效。");
        return Client.GetByteArrayAsync(uri, cancellationToken);
    }

    private static string ResolveUrl(string path)
        => Uri.TryCreate(path, UriKind.Absolute, out var absolute) ? absolute.ToString() : new Uri(Client.BaseAddress!, path).ToString();

    private static string DecodeText(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        return Regex.Replace(decoded, "\\s+", " ").Trim();
    }
}
