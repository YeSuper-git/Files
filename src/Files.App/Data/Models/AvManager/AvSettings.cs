// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.AvManager;

public sealed class AvSettings
{
    public List<string> VideoExtensions { get; set; } = ["mp4", "mkv", "avi", "mov", "wmv", "flv", "m4v", "ts"];
    public List<string> ImageExtensions { get; set; } = ["jpg", "jpeg", "png", "webp"];
    public int PosterQualityKb { get; set; } = 30;
    public List<string> SubtitleKeywords { get; set; } = ["中文字幕", "中字", "中文", "chinese", "chs", "cht", "sub"];

    public AvSettings Clone() => new()
    {
        VideoExtensions = [.. (VideoExtensions ?? [])],
        ImageExtensions = [.. (ImageExtensions ?? [])],
        PosterQualityKb = PosterQualityKb,
        SubtitleKeywords = [.. (SubtitleKeywords ?? [])]
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
