// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.AvManager;

public sealed class AvSettings
{
    public List<string> VideoExtensions { get; set; } = ["mp4", "mkv", "avi", "mov", "wmv", "flv", "m4v", "ts"];
    public List<string> ImageExtensions { get; set; } = ["jpg", "jpeg", "png", "webp"];
    public int PosterQualityKb { get; set; } = 30;
    public List<string> SubtitleKeywords { get; set; } = ["中文字幕", "中字", "中文", "chinese", "chs", "cht", "sub"];
}
