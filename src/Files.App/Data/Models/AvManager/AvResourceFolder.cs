// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.AvManager;

public sealed class AvResourceFolder
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? Code { get; init; }
    public bool HasVideo { get; init; }
    public bool HasPoster { get; init; }
    public bool HasChineseSubtitle { get; init; }
    public int VideoCount { get; init; }
    public int PosterCount { get; init; }
    public int LowQualityPosterCount { get; init; }
    public List<string> Problems { get; init; } = [];
    public bool IsNormal => Problems.Count == 0;
}
