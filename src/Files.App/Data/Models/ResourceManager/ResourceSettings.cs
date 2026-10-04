// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

public sealed class ResourceSettings
{
    public List<string> VideoExtensions { get; set; } = ["mp4", "mkv", "avi", "mov", "wmv", "flv", "m4v", "ts"];
    public List<string> ImageExtensions { get; set; } = ["jpg", "jpeg", "png", "webp"];
    public List<string> AnimeImageIncludedNames { get; set; } = [];
    public string AnimeImageWebsitePrefix { get; set; } = string.Empty;
    public int AnimeImageMinimumWidth { get; set; }
    public int AnimeImageMinimumHeight { get; set; }
    public bool AnimeImageGroupByPrefix { get; set; }
    public string AnimeImageSource { get; set; } = string.Empty;
    public bool AnimeFlattenSeasons { get; set; } = true;
    public int AnimePosterWidth { get; set; } = 160;
    public int AnimePosterEpisodeLimit { get; set; } = 8;
    public int PosterQualityKb { get; set; } = 30;
    public List<string> SubtitleKeywords { get; set; } = ["中文字幕", "中字", "中文", "chinese", "chs", "cht", "sub"];

    public ResourceSettings Clone() => new()
    {
        VideoExtensions = [.. (VideoExtensions ?? [])],
        ImageExtensions = [.. (ImageExtensions ?? [])],
        PosterQualityKb = PosterQualityKb,
        AnimeImageIncludedNames = [.. (AnimeImageIncludedNames ?? [])],
        AnimeImageWebsitePrefix = AnimeImageWebsitePrefix,
        AnimeImageSource = AnimeImageSource,
        AnimeImageMinimumWidth = AnimeImageMinimumWidth,
        AnimeImageMinimumHeight = AnimeImageMinimumHeight,
        AnimeImageGroupByPrefix = AnimeImageGroupByPrefix,
        AnimeFlattenSeasons = AnimeFlattenSeasons,
        AnimePosterWidth = AnimePosterWidth,
        AnimePosterEpisodeLimit = AnimePosterEpisodeLimit,
        SubtitleKeywords = [.. (SubtitleKeywords ?? [])],
    };

    public void Normalize()
    {
        VideoExtensions = NormalizeExtensions(VideoExtensions, ["mp4", "mkv", "avi", "mov", "wmv", "flv", "m4v", "ts"]);
        ImageExtensions = NormalizeExtensions(ImageExtensions, ["jpg", "jpeg", "png", "webp"]);
        var keywords = (SubtitleKeywords ?? [])
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SubtitleKeywords = keywords.Count > 0
            ? keywords
            : ["中文字幕", "中字", "中文", "chinese", "chs", "cht", "sub"];
        AnimeImageIncludedNames = (AnimeImageIncludedNames ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AnimeImageMinimumWidth = Math.Clamp(AnimeImageMinimumWidth, 0, 65535);
        AnimeImageMinimumHeight = Math.Clamp(AnimeImageMinimumHeight, 0, 65535);
        AnimePosterWidth = Math.Clamp(AnimePosterWidth, 100, 240);
        AnimePosterEpisodeLimit = Math.Clamp(AnimePosterEpisodeLimit, 0, 30);
        PosterQualityKb = Math.Clamp(PosterQualityKb, 1, 1024 * 1024);
    }

    private static List<string> NormalizeExtensions(IEnumerable<string>? values, List<string> fallback)
    {
        var normalized = (values ?? [])
            .Select(x => (x ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant())
            .Where(x => x.Length > 0 && x.All(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count > 0 ? normalized : fallback;
    }
}
