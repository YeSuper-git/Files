// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

public sealed class ResourceSettings
{
    public List<string> VideoExtensions { get; set; } = ["mp4", "mkv", "avi", "mov", "wmv", "flv", "m4v", "ts", "webm", "mpg", "mpeg"];
    public List<string> ImageExtensions { get; set; } = ["jpg", "jpeg", "png", "webp"];
    public List<string> AnimeImageIncludedNames { get; set; } = [];
    public bool? ImageKeywordRecognitionEnabled { get; set; }
    public bool? ImageSizeLimitEnabled { get; set; }
    public bool? VideoLengthLimitEnabled { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveImageMinimumWidth => ImageSizeLimitEnabled == true ? AnimeImageMinimumWidth : 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveImageMinimumHeight => ImageSizeLimitEnabled == true ? AnimeImageMinimumHeight : 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveVideoMinimumSeconds => VideoLengthLimitEnabled == true ? AnimeVideoMinimumSeconds : 0;
    public int? BrowserSortOption { get; set; }
    public int? BrowserSortDirection { get; set; }
    public bool BrowserSortFilesFirst { get; set; }
    public bool BrowserSortDirectoriesAlongsideFiles { get; set; }
    public string AnimeImageWebsitePrefix { get; set; } = string.Empty;
    public List<string> MediaWebsitePrefixes { get; set; } = [];
    public int AnimeVideoMinimumSeconds { get; set; }
    public int AnimeImageMinimumWidth { get; set; }
    public int AnimeImageMinimumHeight { get; set; }
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
        ImageKeywordRecognitionEnabled = ImageKeywordRecognitionEnabled,
        ImageSizeLimitEnabled = ImageSizeLimitEnabled,
        VideoLengthLimitEnabled = VideoLengthLimitEnabled,
        BrowserSortOption = BrowserSortOption,
        BrowserSortDirection = BrowserSortDirection,
        BrowserSortFilesFirst = BrowserSortFilesFirst,
        BrowserSortDirectoriesAlongsideFiles = BrowserSortDirectoriesAlongsideFiles,
        AnimeImageWebsitePrefix = AnimeImageWebsitePrefix,
        MediaWebsitePrefixes = [.. (MediaWebsitePrefixes ?? [])],
        AnimeImageSource = AnimeImageSource,
        AnimeVideoMinimumSeconds = AnimeVideoMinimumSeconds,
        AnimeImageMinimumWidth = AnimeImageMinimumWidth,
        AnimeImageMinimumHeight = AnimeImageMinimumHeight,
        AnimeFlattenSeasons = AnimeFlattenSeasons,
        AnimePosterWidth = AnimePosterWidth,
        AnimePosterEpisodeLimit = AnimePosterEpisodeLimit,
        SubtitleKeywords = [.. (SubtitleKeywords ?? [])],
    };

    public void Normalize()
    {
        VideoExtensions = NormalizeExtensions(null, ["mp4", "mkv", "avi", "mov", "wmv", "flv", "m4v", "ts", "webm", "mpg", "mpeg"]);
        ImageExtensions = NormalizeExtensions(null, ["jpg", "jpeg", "png", "webp"]);
        var keywords = (SubtitleKeywords ?? [])
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SubtitleKeywords = keywords.Count > 0
            ? keywords
            : ["中文字幕", "中字", "中文", "chinese", "chs", "cht", "sub"];
        AnimeImageIncludedNames = (AnimeImageIncludedNames ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ImageKeywordRecognitionEnabled ??= AnimeImageIncludedNames.Count > 0;
        ImageSizeLimitEnabled ??= AnimeImageMinimumWidth > 0 || AnimeImageMinimumHeight > 0;
        VideoLengthLimitEnabled ??= AnimeVideoMinimumSeconds > 0;
        var savedPrefixes = MediaWebsitePrefixes ?? [];
        if (savedPrefixes.Count == 0 && !string.IsNullOrWhiteSpace(AnimeImageWebsitePrefix)) savedPrefixes = [AnimeImageWebsitePrefix];
        MediaWebsitePrefixes = savedPrefixes.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().TrimEnd('/') + "/").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        AnimeImageWebsitePrefix = MediaWebsitePrefixes.FirstOrDefault(value => string.Equals(value, (AnimeImageWebsitePrefix ?? string.Empty).Trim().TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
            ?? MediaWebsitePrefixes.FirstOrDefault() ?? string.Empty;
        AnimeVideoMinimumSeconds = Math.Clamp(AnimeVideoMinimumSeconds, 0, 86400);
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
