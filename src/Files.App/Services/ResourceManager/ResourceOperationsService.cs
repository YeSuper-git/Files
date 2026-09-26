// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text.Json;
using Files.App.Data.Models.ResourceManager;
using Microsoft.Extensions.Logging;

namespace Files.App.Services.ResourceManager;

public sealed class ResourceOperationsService : IResourceOperationsService
{
    private const string BackupDirectoryName = ".files-resource-manager-backups";

    private readonly IResourceCodeParser _codeParser;
    private readonly IResourceScanner _scanner;
    private readonly IResourceWorkspaceService _workspace;
    private readonly ILogger<ResourceOperationsService> _logger;

    public ResourceOperationsService(
        IResourceCodeParser codeParser,
        IResourceScanner scanner,
        IResourceWorkspaceService workspace,
        ILogger<ResourceOperationsService> logger)
    {
        _codeParser = codeParser;
        _scanner = scanner;
        _workspace = workspace;
        _logger = logger;
    }

    public async Task<List<ResourceFileOperation>> PreviewRenameVideosAsync(
        string root,
        ResourceSettings? settings = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var effectiveSettings = settings?.Clone() ?? new ResourceSettings();
            effectiveSettings.Normalize();
            var extensions = effectiveSettings.VideoExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = EnumerateFiles(root, 0, int.MaxValue, ct)
                .Select(file => (File: file, Code: _codeParser.ParseCode(file.Directory?.Name ?? string.Empty)?.Normalized));
            return CreateRenameOperations(files, extensions, ct);
        }, ct);
    }

    public async Task<List<ResourceFileOperation>> PreviewRenameVideosInFoldersAsync(
        string root,
        IEnumerable<string> videoFolderPaths,
        ResourceSettings? settings = null,
        CancellationToken ct = default)
    {
        var folders = videoFolderPaths?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        return await Task.Run(() =>
        {
            var effectiveSettings = settings?.Clone() ?? new ResourceSettings();
            effectiveSettings.Normalize();
            var extensions = effectiveSettings.VideoExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = new List<(FileInfo File, string? Code)>();
            foreach (var folderPath in folders)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsPathInsideRoot(root, folderPath))
                    continue;

                try
                {
                    var directory = new DirectoryInfo(folderPath);
                    if (directory.Exists)
                    {
                        var code = _codeParser.ParseCode(directory.Name)?.Normalized;
                        foreach (var file in EnumerateFilesRecursive(directory, 0, 0, int.MaxValue, ct))
                            files.Add((file, code));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unable to enumerate videos in resource folder {Path}", folderPath);
                }
            }

            return CreateRenameOperations(files, extensions, ct);
        }, ct);
    }

    private List<ResourceFileOperation> CreateRenameOperations(
        IEnumerable<(FileInfo File, string? Code)> files,
        IReadOnlySet<string> extensions,
        CancellationToken ct)
    {
        var ops = new List<ResourceFileOperation>();
        var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in files)
        {
            ct.ThrowIfCancellationRequested();
            var file = entry.File;
            if (!IsVideoFile(file, extensions))
                continue;

            var parent = file.Directory;
            if (parent is null)
                continue;

            if (string.IsNullOrWhiteSpace(entry.Code))
            {
                ops.Add(new ResourceFileOperation
                {
                    Operation = "rename",
                    Source = file.FullName,
                    Status = "skip",
                    Reason = "视频所在文件夹无法识别番号"
                });
                continue;
            }

            var ext = file.Extension.TrimStart('.');
            var target = Path.Combine(parent.FullName, $"{entry.Code}.{ext}");
            var samePath = PathsEqual(file.FullName, target);
            var targetAlreadyPlanned = !samePath && !plannedTargets.Add(target);
            var status = samePath
                ? "ok"
                : targetAlreadyPlanned || PathExists(target)
                    ? "conflict"
                    : "ready";

            ops.Add(new ResourceFileOperation
            {
                Operation = "rename",
                Source = file.FullName,
                Target = target,
                Code = entry.Code,
                Status = status,
                Reason = targetAlreadyPlanned
                    ? "多个视频映射到了同一个目标文件名"
                    : status == "conflict"
                        ? "目标文件已存在"
                        : null
            });
        }

        return ops;
    }

    public async Task<List<ResourceFileOperation>> PreviewClassifySubtitlesAsync(
        string root,
        ResourceSettings settings,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var effectiveSettings = settings.Clone();
            effectiveSettings.Normalize();
            var scanResult = _scanner.AnalyzeLibraryAsync(root, effectiveSettings, ct).GetAwaiter().GetResult();
            var ops = new List<ResourceFileOperation>();

            foreach (var folder in scanResult.Folders)
            {
                ct.ThrowIfCancellationRequested();
                if (folder.HasChineseSubtitle)
                    continue;

                var source = new DirectoryInfo(folder.Path);
                var parent = source.Parent;
                if (parent is null || !IsPathInsideRoot(root, source.FullName))
                    continue;

                var targetRoot = Path.Combine(parent.FullName, "无中文字幕");
                if (PathsEqual(source.Parent?.FullName, targetRoot))
                    continue;

                var target = Path.Combine(targetRoot, folder.Name);
                var status = Directory.Exists(target) ? "conflict" : "ready";
                ops.Add(new ResourceFileOperation
                {
                    Operation = "classify_no_subtitle",
                    Source = folder.Path,
                    Target = target,
                    Code = folder.Code,
                    Status = status,
                    Reason = status == "conflict" ? "无中文字幕目录中已存在同名文件夹" : null
                });
            }

            return ops;
        }, ct);
    }

    public List<ResourceFileOperation> ApplyConflictStrategy(List<ResourceFileOperation> ops, string strategy)
    {
        var resolved = new List<ResourceFileOperation>(ops.Count);
        foreach (var op in ops)
        {
            if (op.Status != "conflict" || string.IsNullOrEmpty(op.Target))
            {
                resolved.Add(op);
                continue;
            }

            var isDirectory = IsDirectoryOperation(op);
            switch (strategy)
            {
                case "skip":
                    resolved.Add(new ResourceFileOperation
                    {
                        Operation = op.Operation,
                        Source = op.Source,
                        Target = op.Target,
                        Code = op.Code,
                        Status = "skip",
                        Reason = "按策略跳过冲突"
                    });
                    break;

                case "keep_both":
                    var directory = Path.GetDirectoryName(op.Target) ?? ".";
                    var stem = isDirectory
                        ? Path.GetFileName(op.Target)
                        : Path.GetFileNameWithoutExtension(op.Target);
                    var extension = isDirectory ? string.Empty : Path.GetExtension(op.Target);
                    var found = false;
                    for (var i = 2; i < 10000; i++)
                    {
                        var candidate = Path.Combine(directory, $"{stem}-{i:D2}{extension}");
                        if (PathExists(candidate))
                            continue;

                        resolved.Add(new ResourceFileOperation
                        {
                            Operation = op.Operation,
                            Source = op.Source,
                            Target = candidate,
                            Code = op.Code,
                            Status = "ready"
                        });
                        found = true;
                        break;
                    }

                    if (!found)
                    {
                        resolved.Add(new ResourceFileOperation
                        {
                            Operation = op.Operation,
                            Source = op.Source,
                            Target = op.Target,
                            Code = op.Code,
                            Status = "conflict",
                            Reason = "没有可用的备用名称"
                        });
                    }
                    break;

                case "overwrite":
                    resolved.Add(isDirectory
                        ? new ResourceFileOperation
                        {
                            Operation = op.Operation,
                            Source = op.Source,
                            Target = op.Target,
                            Code = op.Code,
                            Status = "conflict",
                            Reason = "为避免数据丢失，文件夹不支持自动覆盖"
                        }
                        : new ResourceFileOperation
                        {
                            Operation = op.Operation,
                            Source = op.Source,
                            Target = op.Target,
                            Code = op.Code,
                            Status = "ready_overwrite",
                            Reason = "执行前会自动备份原目标文件"
                        });
                    break;

                default:
                    resolved.Add(op);
                    break;
            }
        }

        return resolved;
    }

    public async Task<List<ResourceFileOperation>> ExecuteOperationsAsync(string root, List<ResourceFileOperation> ops, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<ResourceFileOperation>(ops.Count);
            var directoryPathMappings = new List<(string SourcePath, string TargetPath)>();
            foreach (var op in ops)
            {
                ct.ThrowIfCancellationRequested();
                if (op.Status is not ("ready" or "ready_overwrite"))
                {
                    results.Add(op);
                    continue;
                }

                if (string.IsNullOrEmpty(op.Target))
                {
                    results.Add(FailedOperation(op, "目标为空", "conflict"));
                    continue;
                }

                if (!IsPathInsideRoot(root, op.Source) || !IsPathInsideRoot(root, op.Target))
                {
                    results.Add(FailedOperation(op, "操作路径必须位于当前资源库内", "conflict"));
                    continue;
                }

                try
                {
                    var overwrite = op.Status == "ready_overwrite";
                    var sourceIsFile = File.Exists(op.Source);
                    var sourceIsDirectory = Directory.Exists(op.Source);
                    if (!sourceIsFile && !sourceIsDirectory)
                    {
                        results.Add(FailedOperation(op, "源文件或文件夹不存在", "failed"));
                        continue;
                    }

                    if (PathsEqual(op.Source, op.Target))
                    {
                        results.Add(new ResourceFileOperation
                        {
                            Operation = op.Operation,
                            Source = op.Source,
                            Target = op.Target,
                            Code = op.Code,
                            Status = "ok"
                        });
                        continue;
                    }

                    if (PathExists(op.Target) && !overwrite)
                    {
                        results.Add(FailedOperation(op, "目标文件或文件夹已存在", "conflict"));
                        continue;
                    }

                    var parent = Path.GetDirectoryName(op.Target);
                    if (parent is not null)
                        Directory.CreateDirectory(parent);

                    if (sourceIsDirectory)
                    {
                        // Never merge or delete a user's existing directory.
                        if (PathExists(op.Target))
                        {
                            results.Add(FailedOperation(op, "为避免数据丢失，文件夹不支持自动覆盖", "conflict"));
                            continue;
                        }

                        Directory.Move(op.Source, op.Target);
                        directoryPathMappings.Add((op.Source, op.Target));
                        results.Add(DoneOperation(op));
                        continue;
                    }

                    if (Directory.Exists(op.Target))
                    {
                        results.Add(FailedOperation(op, "目标是文件夹", "conflict"));
                        continue;
                    }

                    string? backup = null;
                    if (overwrite && File.Exists(op.Target))
                    {
                        backup = CreateBackupPath(root, op.Target);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Move(op.Target, backup);
                    }

                    try
                    {
                        File.Move(op.Source, op.Target);
                    }
                    catch
                    {
                        if (backup is not null && File.Exists(backup) && !PathExists(op.Target))
                            File.Move(backup, op.Target);
                        throw;
                    }

                    results.Add(DoneOperation(op, backup));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Resource operation failed: {Source} -> {Target}", op.Source, op.Target);
                    results.Add(FailedOperation(op, ex.Message, "failed"));
                }
            }

            if (directoryPathMappings.Count > 0)
                _workspace.RemapItemPaths(directoryPathMappings);

            var done = results.Where(r => r.Status == "done").ToList();
            if (done.Count > 0)
                AppendHistory(root, done);
            return results;
        }, ct);
    }

    public async Task<List<ResourceFileOperation>> RestoreOperationsAsync(string root, List<ResourceFileOperation> ops, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<ResourceFileOperation>(ops.Count);
            var directoryPathMappings = new List<(string SourcePath, string TargetPath)>();
            foreach (var op in ops.AsEnumerable().Reverse())
            {
                ct.ThrowIfCancellationRequested();
                var restored = new ResourceFileOperation
                {
                    Operation = op.Operation,
                    Source = op.Source,
                    Target = op.Target,
                    Code = op.Code,
                    Status = op.Status,
                    Reason = op.Reason,
                    Backup = op.Backup,
                };

                if (op.Status is "restored" or "already_restored")
                {
                    results.Add(restored);
                    continue;
                }

                if (!IsPathInsideRoot(root, op.Source) || !IsPathInsideRoot(root, op.Target)
                    || (!string.IsNullOrWhiteSpace(op.Backup) && !IsPathInsideRoot(root, op.Backup)))
                {
                    restored.Status = "conflict";
                    restored.Reason = "快照中的操作路径不在资源库内";
                    results.Add(restored);
                    continue;
                }

                if (PathsEqual(op.Source, op.Target))
                {
                    restored.Status = "already_restored";
                    results.Add(restored);
                    continue;
                }

                var sourceExists = PathExists(op.Source);
                var targetExists = PathExists(op.Target);
                var backupExists = !string.IsNullOrWhiteSpace(op.Backup) && File.Exists(op.Backup);

                // A prior attempt may have moved the changed file back but failed
                // while restoring the displaced target from its backup.
                if (sourceExists && !targetExists && backupExists)
                {
                    try
                    {
                        File.Move(op.Backup!, op.Target);
                        TryDeleteEmptyBackupDirectory(op.Backup!);
                        restored.Status = "restored";
                        restored.Reason = null;
                    }
                    catch (Exception ex)
                    {
                        restored.Status = "conflict";
                        restored.Reason = ex.Message;
                    }
                    results.Add(restored);
                    continue;
                }

                // The operation never ran, or a previous attempt already restored it.
                if (sourceExists && !targetExists && !backupExists)
                {
                    restored.Status = "already_restored";
                    restored.Reason = null;
                    results.Add(restored);
                    continue;
                }

                if (!targetExists || sourceExists)
                {
                    restored.Status = "conflict";
                    restored.Reason = "原路径或目标路径已变化，无法安全回退";
                    results.Add(restored);
                    continue;
                }

                try
                {
                    if (backupExists && (Directory.Exists(op.Source) || Directory.Exists(op.Target)))
                        throw new IOException("目录操作不能带有覆盖备份");

                    var parent = Path.GetDirectoryName(op.Source);
                    if (parent is not null)
                        Directory.CreateDirectory(parent);

                    if (Directory.Exists(op.Target))
                    {
                        Directory.Move(op.Target, op.Source);
                        directoryPathMappings.Add((op.Target, op.Source));
                    }
                    else
                        File.Move(op.Target, op.Source);

                    if (backupExists)
                    {
                        if (PathExists(op.Target))
                            throw new IOException("恢复的原文件名已被占用，无法还原覆盖前文件");
                        File.Move(op.Backup!, op.Target);
                        TryDeleteEmptyBackupDirectory(op.Backup!);
                    }

                    restored.Status = "restored";
                    restored.Reason = null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to restore resource operation {Source} -> {Target}", op.Source, op.Target);
                    restored.Status = "conflict";
                    restored.Reason = ex.Message;
                }

                results.Add(restored);
            }

            if (directoryPathMappings.Count > 0)
                _workspace.RemapItemPaths(directoryPathMappings);
            return results;
        }, ct);
    }

    public async Task<List<ResourceOperationBatch>> GetOperationHistoryAsync(string root, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var path = HistoryPath(root);
            if (!File.Exists(path))
                return [];

            try
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path), ResourceManagerJsonSerializerContext.Default.ListResourceOperationBatch) ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to read resource operation history");
                return [];
            }
        }, ct);
    }

    public async Task<List<ResourceFileOperation>> UndoLastOperationAsync(string root, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var history = ReadHistory(root);
            if (history.Count == 0)
                return [];

            var batch = history[^1];
            var reversed = new List<ResourceFileOperation>();
            var directoryPathMappings = new List<(string SourcePath, string TargetPath)>();
            foreach (var op in batch.Operations.AsEnumerable().Reverse().ToList())
            {
                ct.ThrowIfCancellationRequested();
                var undo = new ResourceFileOperation
                {
                    Operation = $"undo_{op.Operation}",
                    Source = op.Target,
                    Target = op.Source,
                    Code = op.Code,
                    Status = "ready",
                    Backup = op.Backup
                };

                if (!IsPathInsideRoot(root, undo.Source) || !IsPathInsideRoot(root, undo.Target))
                {
                    undo.Status = "conflict";
                    undo.Reason = "撤销路径不在当前资源库内";
                    reversed.Add(undo);
                    continue;
                }

                if (!PathExists(undo.Source) || PathExists(undo.Target))
                {
                    undo.Status = "conflict";
                    undo.Reason = "撤销源不存在或原路径已被占用";
                    reversed.Add(undo);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(op.Backup)
                    && (!IsPathInsideRoot(root, op.Backup) || !File.Exists(op.Backup)))
                {
                    undo.Status = "conflict";
                    undo.Reason = "覆盖操作的备份文件不存在，无法完整撤销";
                    reversed.Add(undo);
                    continue;
                }

                try
                {
                    var parent = Path.GetDirectoryName(undo.Target);
                    if (parent is not null)
                        Directory.CreateDirectory(parent);

                    if (Directory.Exists(undo.Source))
                    {
                        Directory.Move(undo.Source, undo.Target);
                        directoryPathMappings.Add((undo.Source, undo.Target));
                    }
                    else
                        File.Move(undo.Source, undo.Target);

                    if (!string.IsNullOrWhiteSpace(op.Backup))
                    {
                        File.Move(op.Backup, op.Target);
                        TryDeleteEmptyBackupDirectory(op.Backup);
                    }

                    undo.Status = "done";
                    batch.Operations.Remove(op);
                }
                catch (Exception ex)
                {
                    undo.Status = "failed";
                    undo.Reason = ex.Message;
                }

                reversed.Add(undo);
            }

            if (directoryPathMappings.Count > 0)
                _workspace.RemapItemPaths(directoryPathMappings);

            history.RemoveAll(x => x.Operations.Count == 0);
            if (batch.Operations.Count > 0 && !history.Contains(batch))
                history.Add(batch);
            WriteHistory(root, history);
            return reversed;
        }, ct);
    }

    private static ResourceFileOperation DoneOperation(ResourceFileOperation op, string? backup = null) => new()
    {
        Operation = op.Operation,
        Source = op.Source,
        Target = op.Target,
        Code = op.Code,
        Status = "done",
        Backup = backup
    };

    private static ResourceFileOperation FailedOperation(ResourceFileOperation op, string reason, string status) => new()
    {
        Operation = op.Operation,
        Source = op.Source,
        Target = op.Target,
        Code = op.Code,
        Status = status,
        Reason = reason
    };

    private static bool IsDirectoryOperation(ResourceFileOperation op) =>
        op.Operation.StartsWith("classify", StringComparison.OrdinalIgnoreCase)
        || Directory.Exists(op.Source);

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool PathsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(Path.GetFullPath(left!), Path.GetFullPath(right!), StringComparison.OrdinalIgnoreCase);

    private static bool IsPathInsideRoot(string root, string candidate)
    {
        try
        {
            var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidatePath = Path.GetFullPath(candidate);
            return candidatePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidatePath, rootPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string HistoryPath(string root) => Path.Combine(root, ".files-resource-manager-history.json");

    private static string CreateBackupPath(string root, string target)
    {
        var name = Path.GetFileName(target);
        return Path.Combine(root, BackupDirectoryName, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}-{name}");
    }

    private void AppendHistory(string root, List<ResourceFileOperation> done)
    {
        try
        {
            var history = ReadHistory(root);
            var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            history.Add(new ResourceOperationBatch
            {
                Id = $"batch-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}",
                CreatedAt = now,
                Summary = $"完成 {done.Count} 项文件操作",
                Operations = done
            });
            WriteHistory(root, history);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append resource operation history");
        }
    }

    private List<ResourceOperationBatch> ReadHistory(string root)
    {
        var path = HistoryPath(root);
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ResourceManagerJsonSerializerContext.Default.ListResourceOperationBatch) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read resource operation history");
            return [];
        }
    }

    private void WriteHistory(string root, List<ResourceOperationBatch> history)
    {
        var path = HistoryPath(root);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(history, ResourceManagerJsonSerializerContext.Default.ListResourceOperationBatch));
            File.Move(temporaryPath, path, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to write resource operation history");
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Keep the last complete history file.
            }
        }
    }

    private static void TryDeleteEmptyBackupDirectory(string backupPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(backupPath);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch
        {
            // Backup cleanup is best effort and must not invalidate a successful undo.
        }
    }

    private static bool IsVideoFile(FileInfo file, IReadOnlySet<string> extensions) =>
        extensions.Contains(file.Extension.TrimStart('.'));

    private static IEnumerable<FileInfo> EnumerateFiles(string root, int minDepth, int maxDepth, CancellationToken ct)
    {
        var directory = new DirectoryInfo(root);
        if (!directory.Exists)
            yield break;

        foreach (var file in EnumerateFilesRecursive(directory, 0, minDepth, maxDepth, ct))
            yield return file;
    }

    private static IEnumerable<FileInfo> EnumerateFilesRecursive(DirectoryInfo directory, int depth, int minDepth, int maxDepth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > maxDepth || IsReparsePoint(directory))
            yield break;

        FileInfo[] files;
        try
        {
            files = directory.GetFiles();
        }
        catch
        {
            yield break;
        }

        if (depth >= minDepth)
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                yield return file;
            }
        }

        DirectoryInfo[] directories;
        try
        {
            directories = directory.GetDirectories();
        }
        catch
        {
            yield break;
        }

        foreach (var child in directories)
        {
            if (child.Name.StartsWith('.') || child.Name.Equals("@eaDir", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var file in EnumerateFilesRecursive(child, depth + 1, minDepth, maxDepth, ct))
                yield return file;
        }
    }

    private static bool IsReparsePoint(DirectoryInfo directory)
    {
        try
        {
            return directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }
}
