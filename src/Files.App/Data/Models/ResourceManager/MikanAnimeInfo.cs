// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

using System.Text.RegularExpressions;

public sealed record MikanAnimeCandidate(int Id, string Title, string PosterUrl);

public sealed class MikanAnimeInfo
{
    public MikanAnimeCandidate Candidate { get; init; } = new(0, string.Empty, string.Empty);
    public string AiringInfo { get; init; } = string.Empty;
    public string Synopsis { get; init; } = string.Empty;
    public int? BangumiSubjectId { get; init; }
    public IReadOnlyList<MikanSubtitleGroup> Groups { get; init; } = [];
    public IReadOnlyList<MikanEpisodeRelease> Releases => Groups.SelectMany(group => group.Releases).ToArray();
}

public sealed record MikanSubtitleGroup(int Id, string Name, IReadOnlyList<MikanEpisodeRelease> Releases);

public sealed record MikanEpisodeRelease(string Id, string Title, string Size, string PublishedAt, string MagnetUri, string PageUrl, string GroupName, int GroupId)
{
    public string VariantKey => MikanReleaseVariant.GetKey(Title);
}

public static class MikanReleaseVariant
{
    public static string GetKey(string title)
    {
        var details = new List<string>();
        AddMatch(details, title, @"(?<!\d)(?:\d{3,4}p|\d{3,4}x\d{3,4})(?!\d)", "分辨率");
        AddMatch(details, title, @"(?i)(?:AVC|HEVC|H\.?(?:264|265)|x26[45]|AV1|10[- ]?bit)", "编码");
        AddMatch(details, title, @"(?i)(?<![A-Z0-9])(?:MKV|MP4|WEBM)(?![A-Z0-9])", "容器");
        AddMatch(details, title, @"(?i)(?:简繁|简日|繁日|简体|繁体|GB_CN|内封|内嵌|外挂字幕)", "字幕");
        AddMatch(details, title, @"(?i)(?:\bV\d+\b|\b(?:WEB[- ]?DL|WEBRIP|BDRIP|BLURAY)\b)", "版本");
        return details.Count == 0 ? "默认版本" : string.Join(" · ", details);
    }

    private static void AddMatch(List<string> details, string title, string pattern, string label)
    {
        var match = Regex.Match(title, pattern, RegexOptions.CultureInvariant);
        if (match.Success)
            details.Add($"{label} {match.Value.ToUpperInvariant()}");
    }
}
