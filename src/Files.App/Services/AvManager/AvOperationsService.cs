// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json;
using Files.App.Data.Models.AvManager;
using Microsoft.Extensions.Logging;

namespace Files.App.Services.AvManager;

public sealed class AvOperationsService : IAvOperationsService
{
    private readonly IAvCodeParser _codeParser;
    private readonly IAvScanner _scanner;
    private readonly ILogger<AvOperationsService> _logger;

    public AvOperationsService(IAvCodeParser codeParser, IAvScanner scanner, ILogger<AvOperationsService> logger)
    {
        _codeParser = codeParser;
        _scanner = scanner;
        _logger = logger;
    }

    public async Task<List<AvFileOperation>> PreviewRenameVideosAsync(string root, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var ops = new List<AvFileOperation>();
            foreach (var file in EnumerateFiles(root, 2, 3))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsVideoFile(file)) continue;
                var parent = file.Directory;
                if (parent is null) continue;
                var code = _codeParser.ParseCode(parent.Name);
                if (code is null) { ops.Add(new AvFileOperation { Operation = "rename", Source = file.FullName, Status = "skip", Reason = "上级文件夹无法识别番号" }); continue; }
                var ext = file.Extension.TrimStart('.');
                var suffix = _codeParser.HasChineseSubtitle(file.Name) || _codeParser.HasChineseSubtitle(parent.Name) ? "-C" : "";
                var target = Path.Combine(parent.FullName, $"{code.Normalized}{suffix}.{ext}");
                var status = file.Name == Path.GetFileName(target) ? "ok" : File.Exists(target) ? "conflict" : "ready";
                ops.Add(new AvFileOperation { Operation = "rename", Source = file.FullName, Target = target, Code = code.Normalized, Status = status, Reason = status == "conflict" ? "目标文件已存在" : null });
            }
            return ops;
        }, ct);
    }

    public async Task<List<AvFileOperation>> PreviewClassifySubtitlesAsync(string root, AvSettings settings, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var scanResult = _scanner.AnalyzeLibraryAsync(root, settings, ct).GetAwaiter().GetResult();
            var ops = new List<AvFileOperation>();
            foreach (var folder in scanResult.Folders)
            {
                ct.ThrowIfCancellationRequested();
                if (folder.HasChineseSubtitle) continue;
                var source = new DirectoryInfo(folder.Path);
                var parent = source.Parent;
                if (parent is null) continue;
                var targetRoot = Path.Combine(parent.FullName, "无中文字幕");
                if (source.Parent?.FullName == targetRoot) continue;
                var target = Path.Combine(targetRoot, folder.Name);
                var status = Directory.Exists(target) ? "conflict" : "ready";
                ops.Add(new AvFileOperation { Operation = "classify_no_subtitle", Source = folder.Path, Target = target, Code = folder.Code, Status = status, Reason = status == "conflict" ? "无中文字幕目录中已存在同名文件夹" : null });
            }
            return ops;
        }, ct);
    }

    public List<AvFileOperation> ApplyConflictStrategy(List<AvFileOperation> ops, string strategy)
    {
        var resolved = new List<AvFileOperation>();
        foreach (var op in ops)
        {
            if (op.Status != "conflict" || string.IsNullOrEmpty(op.Target)) { resolved.Add(op); continue; }
            switch (strategy)
            {
                case "skip": resolved.Add(new AvFileOperation { Operation = op.Operation, Source = op.Source, Target = op.Target, Code = op.Code, Status = "skip" }); break;
                case "keep_both":
                    var dir = Path.GetDirectoryName(op.Target) ?? ".";
                    var stem = Path.GetFileNameWithoutExtension(op.Target);
                    var ext = new FileInfo(op.Target).Extension.TrimStart('.');
                    for (int i = 2; i < 100; i++) { var c = Path.Combine(dir, $"{stem}-{i:D2}.{ext}"); if (!File.Exists(c)) { resolved.Add(new AvFileOperation { Operation = op.Operation, Source = op.Source, Target = c, Code = op.Code, Status = "ready" }); break; } } break;
                case "overwrite": resolved.Add(new AvFileOperation { Operation = op.Operation, Source = op.Source, Target = op.Target, Code = op.Code, Status = "ready_overwrite" }); break;
                default: resolved.Add(op); break;
            }
        }
        return resolved;
    }

    public async Task<List<AvFileOperation>> ExecuteOperationsAsync(string root, List<AvFileOperation> ops, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<AvFileOperation>();
            foreach (var op in ops)
            {
                ct.ThrowIfCancellationRequested();
                if (op.Status != "ready" && op.Status != "ready_overwrite") { results.Add(op); continue; }
                if (string.IsNullOrEmpty(op.Target)) { results.Add(new AvFileOperation { Operation = op.Operation, Source = op.Source, Target = op.Target, Code = op.Code, Status = "conflict", Reason = "目标为空" }); continue; }
                try
                {
                    var overwrite = op.Status == "ready_overwrite";
                    if (File.Exists(op.Target) && !overwrite) { results.Add(new AvFileOperation { Operation = op.Operation, Source = op.Source, Target = op.Target, Code = op.Code, Status = "conflict", Reason = "目标文件已存在" }); continue; }
                    if (overwrite && File.Exists(op.Target)) File.Delete(op.Target);
                    var p = Path.GetDirectoryName(op.Target); if (p is not null) Directory.CreateDirectory(p);
                    File.Move(op.Source, op.Target);
                    results.Add(new AvFileOperation { Operation = op.Operation, Source = op.Source, Target = op.Target, Code = op.Code, Status = "done" });
                }
                catch (Exception ex) { results.Add(new AvFileOperation { Operation = op.Operation, Source = op.Source, Target = op.Target, Code = op.Code, Status = "failed", Reason = ex.Message }); }
            }
            var done = results.Where(r => r.Status == "done").ToList();
            if (done.Count > 0) AppendHistory(root, done);
            return results;
        }, ct);
    }

    public async Task<List<AvOperationBatch>> GetOperationHistoryAsync(string root, CancellationToken ct = default)
    {
        return await Task.Run(() => { var p = HistoryPath(root); if (!File.Exists(p)) return new List<AvOperationBatch>(); try { return JsonSerializer.Deserialize<List<AvOperationBatch>>(File.ReadAllText(p)) ?? []; } catch { return new List<AvOperationBatch>(); } }, ct);
    }

    public async Task<List<AvFileOperation>> UndoLastOperationAsync(string root, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var history = GetOperationHistoryAsync(root, ct).GetAwaiter().GetResult();
            if (history.Count == 0) return new List<AvFileOperation>();
            var batch = history[^1]; history.RemoveAt(history.Count - 1);
            var reversed = new List<AvFileOperation>();
            foreach (var op in batch.Operations.AsEnumerable().Reverse())
            {
                ct.ThrowIfCancellationRequested();
                var undo = new AvFileOperation { Operation = $"undo_{op.Operation}", Source = op.Target, Target = op.Source, Code = op.Code, Status = "ready" };
                if (!File.Exists(undo.Source) || File.Exists(undo.Target)) { undo.Status = "conflict"; undo.Reason = "撤销源不存在或原路径已被占用"; reversed.Add(undo); continue; }
                try { File.Move(undo.Source, undo.Target); undo.Status = "done"; } catch (Exception ex) { undo.Status = "failed"; undo.Reason = ex.Message; }
                reversed.Add(undo);
            }
            try { File.WriteAllText(HistoryPath(root), JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true })); } catch { }
            return reversed;
        }, ct);
    }

    private static string HistoryPath(string root) => Path.Combine(root, ".av-resource-manager-history.json");
    private void AppendHistory(string root, List<AvFileOperation> done) { try { var h = new List<AvOperationBatch>(); var p = HistoryPath(root); if (File.Exists(p)) h = JsonSerializer.Deserialize<List<AvOperationBatch>>(File.ReadAllText(p)) ?? []; var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); h.Add(new AvOperationBatch { Id = $"batch-{now}", CreatedAt = now, Summary = $"完成 {done.Count} 项文件操作", Operations = done }); File.WriteAllText(p, JsonSerializer.Serialize(h, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to append history"); } }
    private static bool IsVideoFile(FileInfo f) => f.Extension.TrimStart('.').ToLowerInvariant() is "mp4" or "mkv" or "avi" or "mov" or "wmv" or "flv" or "m4v" or "ts";
    private static IEnumerable<FileInfo> EnumerateFiles(string root, int min, int max) { var d = new DirectoryInfo(root); if (!d.Exists) yield break; foreach (var f in EnumerateFilesR(d, 0, min, max)) yield return f; }
    private static IEnumerable<FileInfo> EnumerateFilesR(DirectoryInfo d, int depth, int min, int max) { if (depth > max) yield break; FileInfo[] fs; try { fs = d.GetFiles(); } catch { yield break; } if (depth >= min) foreach (var f in fs) yield return f; DirectoryInfo[] ds; try { ds = d.GetDirectories(); } catch { yield break; } foreach (var s in ds) { if (s.Name.StartsWith('.')) continue; foreach (var f in EnumerateFilesR(s, depth + 1, min, max)) yield return f; } }
}
