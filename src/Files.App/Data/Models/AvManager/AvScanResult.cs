// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.AvManager;

public sealed class AvScanResult
{
    public string Root { get; init; } = string.Empty;
    public int TotalFolders { get; init; }
    public int NormalCount { get; init; }
    public int MissingVideoCount { get; init; }
    public int MissingPosterCount { get; init; }
    public int ChineseSubCount { get; init; }
    public int NoChineseSubCount { get; init; }
    public int LowQualityPosterCount { get; init; }
    public int DuplicateCodeCount { get; init; }
    public int LooseVideoCount { get; init; }
    public List<AvResourceFolder> Folders { get; init; } = [];
    public int ProblemCount => TotalFolders - NormalCount;
}
