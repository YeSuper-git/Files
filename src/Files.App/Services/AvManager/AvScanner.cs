// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using Files.App.Data.Models.AvManager;
using Microsoft.Extensions.Logging;

namespace Files.App.Services.AvManager;

public sealed class AvScanner : IAvScanner
{
    private readonly IAvCodeParser _codeParser;
    private readonly ILogger<AvScanner> _logger;

    public AvScanner(IAvCodeParser codeParser, ILogger<AvScanner> logger)
    {
        _codeParser = codeParser;
        _logger = logger;
    }

    public async Task<AvScanResult> AnalyzeLibraryAsync(string root, AvSettings settings, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var effectiveSettings = settings.Clone();
            effectiveSettings.Normalize();
            var rootDir = new DirectoryInfo(root);
            var folders = new List<AvResourceFolder>();

            if (!rootDir.Exists)
            {
                return new AvScanResult { Root = root };
            }

            try
            {
                foreach (var entry in rootDir.GetDirectories())
                {
                    ct.ThrowIfCancellationRequested();
                    if (ShouldSkipDirectory(entry))
                        continue;
                    if (entry.Name.StartsWith('_'))
                        CollectWorkFolders(entry, folders, effectiveSettings, ct);
                    else if (_codeParser.ParseCode(entry.Name) is not null)
                        folders.Add(AnalyzeWorkFolder(entry, effectiveSettings, ct));
                    else
                        CollectWorkFolders(entry, folders, effectiveSettings, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Error analyzing library"); }

            MarkDuplicateCodes(folders);
            var looseVideoCount = CountLooseVideos(rootDir, effectiveSettings, ct);

            return new AvScanResult
            {
                Root = root,
                TotalFolders = folders.Count,
                NormalCount = folders.Count(f => f.IsNormal),
                MissingVideoCount = folders.Count(f => !f.HasVideo),
                MissingPosterCount = folders.Count(f => !f.HasPoster),
                ChineseSubCount = folders.Count(f => f.HasChineseSubtitle),
                NoChineseSubCount = folders.Count(f => !f.HasChineseSubtitle),
                LowQualityPosterCount = folders.Count(f => f.LowQualityPosterCount > 0),
                DuplicateCodeCount = folders.Count(f => f.Problems.Contains("重复番号")),
                LooseVideoCount = looseVideoCount,
                Folders = folders
            };
        }, ct);
    }

    private void CollectWorkFolders(DirectoryInfo parent, List<AvResourceFolder> out_, AvSettings settings, CancellationToken ct)
    {
        try
        {
            foreach (var dir in parent.GetDirectories())
            {
                ct.ThrowIfCancellationRequested();
                if (ShouldSkipDirectory(dir))
                    continue;
                out_.Add(AnalyzeWorkFolder(dir, settings, ct));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Error collecting folders from {Path}", parent.FullName); }
    }

    private AvResourceFolder AnalyzeWorkFolder(DirectoryInfo dir, AvSettings settings, CancellationToken ct)
    {
        var name = dir.Name;
        var code = _codeParser.ParseCode(name);
        int vc = 0, pc = 0, lq = 0;
        bool hasSub = _codeParser.HasChineseSubtitle(name, settings.SubtitleKeywords);
        ScanFolderFast(dir, 0, settings, ref vc, ref pc, ref lq, ref hasSub, ct);

        var problems = new List<string>();
        if (code is null) problems.Add("无法识别番号");
        if (vc == 0) problems.Add("缺视频");
        if (pc == 0) problems.Add("缺海报");
        if (vc > 1) problems.Add("多视频");
        if (pc > 1) problems.Add("多海报");
        if (lq > 0) problems.Add("低质量海报");

        return new AvResourceFolder
        {
            Name = name, Path = dir.FullName, Code = code?.Normalized,
            HasVideo = vc > 0, HasPoster = pc > 0, HasChineseSubtitle = hasSub,
            VideoCount = vc, PosterCount = pc, LowQualityPosterCount = lq, Problems = problems
        };
    }

    private void ScanFolderFast(DirectoryInfo dir, int depth, AvSettings s, ref int vc, ref int pc, ref int lq, ref bool hasSub, CancellationToken ct)
    {
        if (depth > 2) return;
        try
        {
            foreach (var entry in dir.GetFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo sub)
                {
                    if (ShouldSkipDirectory(sub)) continue;
                    ScanFolderFast(sub, depth + 1, s, ref vc, ref pc, ref lq, ref hasSub, ct);
                }
                else if (entry is FileInfo f)
                {
                    var ext = f.Extension.TrimStart('.').ToLowerInvariant();
                    if (s.VideoExtensions.Contains(ext)) { vc++; if (_codeParser.HasChineseSubtitle(f.Name, s.SubtitleKeywords)) hasSub = true; }
                    else if (s.ImageExtensions.Contains(ext)) { pc++; if (f.Length < s.PosterQualityKb * 1024) lq++; }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Error scanning {Path}", dir.FullName); }
    }

    private int CountLooseVideos(DirectoryInfo root, AvSettings s, CancellationToken ct)
    {
        try
        {
            var extensions = s.VideoExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var file in root.GetFiles())
            {
                ct.ThrowIfCancellationRequested();
                if (extensions.Contains(file.Extension.TrimStart('.')))
                    count++;
            }
            return count;
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }
    }

    private static bool ShouldSkipDirectory(DirectoryInfo directory)
    {
        if (directory.Name.StartsWith('.') || directory.Name.Equals("@eaDir", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            return directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private void MarkDuplicateCodes(List<AvResourceFolder> folders)
    {
        var counts = new Dictionary<string, int>();
        foreach (var f in folders) if (f.Code is not null) counts[f.Code] = counts.GetValueOrDefault(f.Code) + 1;
        foreach (var f in folders) if (f.Code is not null && counts.GetValueOrDefault(f.Code) > 1 && !f.Problems.Contains("重复番号")) f.Problems.Add("重复番号");
    }
}
