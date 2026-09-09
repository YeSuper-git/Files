// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Files.App.Data.Models.AvManager;

namespace Files.App.Services.AvManager;

public sealed partial class AvCodeParser : IAvCodeParser
{
    [GeneratedRegex(@"(?<![a-zA-Z0-9])([a-zA-Z]{2,6})-?(\d{1,6})(?![a-zA-Z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex CodePattern();

    private static readonly string[] DefaultSubKeywords = ["中文字幕", "中字", "中文", "chinese", "chs", "cht", "sub", "字幕"];

    public AvCodeInfo? ParseCode(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var match = CodePattern().Match(name);
        if (!match.Success) return null;
        var prefix = match.Groups[1].Value.ToUpperInvariant();
        var numberStr = match.Groups[2].Value;
        if (!int.TryParse(numberStr, out var number)) return null;
        return new AvCodeInfo { Normalized = $"{prefix}-{numberStr}", NoZero = $"{prefix}{number}", Raw = match.Value };
    }

    public bool HasChineseSubtitle(string name) => HasChineseSubtitle(name, DefaultSubKeywords);

    public bool HasChineseSubtitle(string name, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var lower = name.ToLowerInvariant();
        return keywords.Any(k => lower.Contains(k.ToLowerInvariant()));
    }
}
