// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Models.ResourceManager;
using Files.App.Services.ResourceManager;
using Files.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.IO;

namespace Files.App.UserControls.ResourceManager;

public sealed partial class ResourceToolsDialog : UserControl
{
    private const string ChineseSubtitleTagName = "中文字幕";
    private const string NoSubtitleFolderName = "无中文字幕";

    private readonly IResourceBrowserService _browser = Ioc.Default.GetRequiredService<IResourceBrowserService>();
    private readonly IResourceCodeParser _codeParser = Ioc.Default.GetRequiredService<IResourceCodeParser>();
    private readonly IResourceOperationsService _operations = Ioc.Default.GetRequiredService<IResourceOperationsService>();
    private readonly IResourceWorkspaceService _workspace = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();
    private readonly string _libraryPath;
    private readonly string _scopePath;
    private readonly ResourceBrowserLocationKind _scopeKind;
    private readonly IReadOnlyList<ResourceBrowserItem> _scopeItems;
    private List<ResourceFileOperation> _pendingOperations = [];
    private List<string> _pendingTagPaths = [];
    private List<TagRemovalMutation> _pendingTagRemovals = [];
    private List<ResourceFileOperation> _executedOperations = [];
    private List<string> _executedTagAddPaths = [];
    private List<TagRemovalMutation> _executedTagRemovals = [];
    private string? _pendingTagId;
    private bool _isBusy;

    public ObservableCollection<string> PreviewItems { get; } = [];
    private readonly Dictionary<CheckBox, OptimizationChoice> _choices = [];
    public bool IsBusy => _isBusy;
    public bool HasChanges { get; private set; }
    public event EventHandler? RequestClose;

    public ResourceToolsDialog(
        string libraryPath,
        string scopePath,
        ResourceBrowserLocationKind scopeKind,
        IReadOnlyList<ResourceBrowserItem> scopeItems)
    {
        InitializeComponent();
        _libraryPath = Path.GetFullPath(libraryPath);
        _scopePath = Path.GetFullPath(scopePath);
        _scopeKind = scopeKind;
        _scopeItems = scopeItems;
        SelectAllButton.Content = Strings.ResourceOptimizationSelectAll.GetLocalizedResource();
        SelectNoneButton.Content = Strings.ResourceOptimizationSelectNone.GetLocalizedResource();
        ExecuteButton.Content = Strings.ResourceOptimizationConfirm.GetLocalizedResource();
        var close = ResourceDialogPresentation.CreateIconButton("\uE8BB", Strings.Close.GetLocalizedResource());
        CloseButton.Content = new FontIcon { Glyph = "\uE8BB", FontSize = 16 };
        CloseButton.Background = close.Background;
        CloseButton.Foreground = close.Foreground;
    }

    public void StartFormatOptimizationPreview()
        => PreviewFormatOptimization_Click(this, new RoutedEventArgs());

    private async void PreviewFormatOptimization_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginPreview())
            return;

        try
        {
            var settings = _workspace.Settings.Clone();
            settings.Normalize();
            var folders = await GetVideoFoldersAsync(settings);
            var existingTag = _workspace.GetResourceTagByName(ChineseSubtitleTagName);
            var tagId = existingTag?.Uid;
            var moves = new List<ResourceFileOperation>();
            var tagPaths = new List<string>();
            var tagRemovals = new List<TagRemovalMutation>();
            var actorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                var actorName = GetActorName(folder.Path);
                actorNames.Add(actorName);
                var hasChineseSubtitle = _codeParser.HasChineseSubtitle(folder.Name, []);

                if (hasChineseSubtitle)
                {
                    if (tagId is null || !_workspace.GetResourceTagIds(folder.Path).Contains(tagId, StringComparer.OrdinalIgnoreCase))
                    {
                        tagPaths.Add(folder.Path);
                    }
                    continue;
                }

                var parent = Directory.GetParent(folder.Path);
                if (parent is null || string.Equals(parent.Name, NoSubtitleFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    if (tagId is not null && _workspace.GetResourceTagIds(folder.Path).Contains(tagId, StringComparer.OrdinalIgnoreCase))
                        tagRemovals.Add(new TagRemovalMutation(folder.Path, folder.Path));
                    continue;
                }

                var targetRoot = Path.Combine(parent.FullName, NoSubtitleFolderName);
                var targetPath = GetAvailableFolderTarget(targetRoot, folder.Name);
                moves.Add(new ResourceFileOperation
                {
                    Operation = "classify_no_subtitle",
                    Source = folder.Path,
                    Target = targetPath,
                    Code = _codeParser.ParseCode(folder.Name)?.Normalized,
                    Status = "ready",
                    Reason = string.Equals(Path.GetFileName(targetPath), folder.Name, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : "目标已存在，将保留两者并为该文件夹添加序号"
                });
                if (tagId is not null && _workspace.GetResourceTagIds(folder.Path).Contains(tagId, StringComparer.OrdinalIgnoreCase))
                    tagRemovals.Add(new TagRemovalMutation(targetPath, folder.Path));
            }

            var renameOperations = await _operations.PreviewRenameVideosInFoldersAsync(
                _libraryPath,
                folders.Select(folder => folder.Path),
                settings);
            foreach (var operation in renameOperations)
                actorNames.Add(GetActorName(Path.GetDirectoryName(operation.Source) ?? _libraryPath));

            // Rename files while their video folders still have their original paths, then move no-subtitle folders.
            _pendingOperations = renameOperations.Concat(moves).ToList();
            _pendingTagPaths = tagPaths;
            _pendingTagRemovals = tagRemovals;
            _pendingTagId = tagId;

            BuildChoiceGroups();
            StatusText.Text = _choices.Count == 0 ? Strings.ResourceFormatNoChanges.GetLocalizedResource() : string.Empty;
            PreviewList.Visibility = Visibility.Collapsed;
            UpdateSelectionState();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"格式优化检查失败：{ex.Message}";
        }
        finally
        {
            _isBusy = false;
            UpdateSelectionState();
        }
    }

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var selected = _choices.Where(pair => pair.Key.IsChecked == true).Select(pair => pair.Value).ToList();
        if (selected.Count == 0) return;
        _pendingOperations = selected.Where(choice => choice.Operation is not null).Select(choice => choice.Operation!).ToList();
        _pendingTagPaths = selected.Where(choice => choice.AddTagPath is not null).Select(choice => choice.AddTagPath!).ToList();
        _pendingTagRemovals = selected.Where(choice => choice.RemoveTag is not null).Select(choice =>
        {
            var mutation = choice.RemoveTag!;
            var move = _pendingOperations.FirstOrDefault(op => op.Operation.StartsWith("classify") &&
                string.Equals(op.Source, mutation.RollbackPath, StringComparison.OrdinalIgnoreCase));
            return new TagRemovalMutation(move?.Target ?? mutation.RollbackPath, mutation.RollbackPath);
        }).ToList();
        _isBusy = true;
        SetChoiceEditingEnabled(false);
        ExecuteButton.IsEnabled = false;
        ResourceToolSnapshot? snapshot = null;
        string? executedCreatedTagUid = null;
        try
        {
            var existingTag = _pendingTagPaths.Count > 0
                ? _workspace.GetResourceTagByName(ChineseSubtitleTagName)
                : null;
            var createdTagUid = _pendingTagPaths.Count > 0 && existingTag is null
                ? Guid.NewGuid().ToString()
                : null;
            var tagSnapshotPaths = _pendingTagPaths
                .Concat(_pendingTagRemovals.Select(mutation => mutation.RollbackPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            snapshot = new ResourceToolSnapshot
            {
                LibraryPath = _libraryPath,
                ScopePath = _scopePath,
                Action = "格式优化",
                Summary = "执行前快照",
                Operations = _pendingOperations
                    .Where(operation => operation.Status is "ready" or "ready_overwrite")
                    .Select(CloneOperation)
                    .ToList(),
                TagAssignments = tagSnapshotPaths
                    .Select(path => new ResourceToolTagAssignmentSnapshot
                    {
                        ItemPath = path,
                        TagIds = _workspace.GetResourceTagIds(path).ToList(),
                    })
                    .ToList(),
                CreatedTagUid = createdTagUid,
            };
            if (!_workspace.SaveResourceToolSnapshot(snapshot))
            {
                StatusText.Text = "无法保存回退快照，未执行任何修改。请检查应用设置目录的写入权限后重试。";
                return;
            }

            var results = _pendingOperations.Count > 0
                ? await _operations.ExecuteOperationsAsync(_libraryPath, _pendingOperations.ToList())
                : [];
            _executedOperations = results.Where(result => result.Status == "done").ToList();
            var movedCount = _executedOperations.Count(result => result.Operation.StartsWith("classify", StringComparison.OrdinalIgnoreCase));
            var renamedCount = _executedOperations.Count(result => result.Operation == "rename");
            var failedCount = results.Count(result => result.Status is "failed" or "conflict");

            var taggedCount = 0;
            var removedTagCount = 0;
            _executedTagAddPaths = [];
            _executedTagRemovals = [];

            if (_pendingTagPaths.Count > 0)
            {
                var tag = existingTag ?? _workspace.CreateResourceTag(ChineseSubtitleTagName, "#0072BD", createdTagUid);
                if (existingTag is null)
                    executedCreatedTagUid = tag.Uid;
                var tagPathsToAdd = _pendingTagPaths
                    .Where(path => !_workspace.GetResourceTagIds(path).Contains(tag.Uid, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (tagPathsToAdd.Count > 0)
                    _workspace.AddResourceTagToItems(tagPathsToAdd, tag.Uid);
                _executedTagAddPaths = tagPathsToAdd
                    .Where(path => _workspace.GetResourceTagIds(path).Contains(tag.Uid, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                taggedCount = _executedTagAddPaths.Count;
            }

            if (_pendingTagRemovals.Count > 0 && !string.IsNullOrWhiteSpace(_pendingTagId))
            {
                var tagRemovalsToApply = _pendingTagRemovals
                    .Select(mutation => Directory.Exists(mutation.ExecutePath) ? mutation : mutation with { ExecutePath = mutation.RollbackPath })
                    .Where(mutation => _workspace.GetResourceTagIds(mutation.ExecutePath).Contains(_pendingTagId, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                if (tagRemovalsToApply.Count > 0)
                    _workspace.RemoveResourceTagFromItems(tagRemovalsToApply.Select(mutation => mutation.ExecutePath), _pendingTagId);
                _executedTagRemovals = tagRemovalsToApply
                    .Where(mutation => !_workspace.GetResourceTagIds(mutation.ExecutePath).Contains(_pendingTagId, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                removedTagCount = _executedTagRemovals.Count;
            }

            var hasChanges = _executedOperations.Count > 0 || taggedCount > 0 || removedTagCount > 0;
            if (hasChanges)
            {
                HasChanges = true;
                ChoiceGroups.Visibility = Visibility.Collapsed;
                SelectionButtons.Visibility = Visibility.Collapsed;
                PreviewItems.Clear();
                foreach (var line in BuildExecutedSummaries(taggedCount, removedTagCount))
                    PreviewItems.Add(line);
                PreviewList.Visibility = PreviewItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            StatusText.Text = $"格式优化完成：视频文件改名 {renamedCount} 个；归档无中字作品 {movedCount} 部；新增中文字幕标签 {taggedCount} 部；清除不匹配标签 {removedTagCount} 部。{(failedCount > 0 ? $"另有 {failedCount} 项冲突或失败。" : string.Empty)}";

            if (hasChanges)
            {
                snapshot.Operations = _executedOperations.Select(CloneOperation).ToList();
                snapshot.Summary = StatusText.Text;
                if (!_workspace.SaveResourceToolSnapshot(snapshot))
                    StatusText.Text += " 回退快照已在执行前保存，但最新统计未能更新。";
                StatusText.Text += "回退快照已保存，可在设置 > 资源管理中回退或删除。";
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(executedCreatedTagUid)
                    && !_workspace.IsResourceTagAssigned(executedCreatedTagUid))
                    _workspace.DeleteResourceTag(executedCreatedTagUid);
                _workspace.DeleteResourceToolSnapshot(snapshot.Id);
            }

        }
        catch (Exception ex)
        {
            StatusText.Text = $"执行失败：{ex.Message}";
            ExecuteButton.IsEnabled = true;
            if (snapshot is not null)
            {
                snapshot.Summary = $"执行中断，可在回退前检查：{ex.Message}";
                _workspace.SaveResourceToolSnapshot(snapshot);
            }
        }
        finally
        {
            _isBusy = false;
            SetChoiceEditingEnabled(!HasChanges);
            UpdateSelectionState();
        }
    }

    private void BuildChoiceGroups()
    {
        _choices.Clear();
        ChoiceGroups.Children.Clear();
        ChoiceGroups.Visibility = SelectionButtons.Visibility = Visibility.Visible;
        var choices = _pendingOperations.Where(op => op.Status == "ready")
            .Select(op => new OptimizationChoice(GetActorName(Path.GetDirectoryName(op.Source) ?? _libraryPath),
                op.Operation.StartsWith("classify") ? Strings.ResourceOptimizationMoveSummary : Strings.ResourceOptimizationRenameSummary,
                $"{Path.GetFileName(op.Source)} → {Path.GetRelativePath(_libraryPath, op.Target)}", Operation: op))
            .Concat(_pendingTagPaths.Select(path => new OptimizationChoice(GetActorName(path), Strings.ResourceOptimizationAddTagSummary,
                Path.GetFileName(path), AddTagPath: path)))
            .Concat(_pendingTagRemovals.Select(change => new OptimizationChoice(GetActorName(change.RollbackPath), Strings.ResourceOptimizationRemoveTagSummary,
                Path.GetFileName(change.RollbackPath), RemoveTag: change)));
        foreach (var group in choices.GroupBy(choice => (choice.Actor, choice.Kind)).OrderBy(group => group.Key.Actor, StringComparer.CurrentCultureIgnoreCase))
        {
            var rows = new StackPanel { Spacing = 4 };
            foreach (var choice in group)
            {
                var label = new TextBlock { Text = choice.Label, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                var check = new CheckBox { Content = label, IsChecked = true, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                ToolTipService.SetToolTip(check, choice.Label);
                _choices.Add(check, choice);
                check.Checked += (_, _) => UpdateSelectionState();
                check.Unchecked += (_, _) => UpdateSelectionState();
                rows.Children.Add(check);
            }
            var header = new StackPanel { Spacing = 4 };
            header.Children.Add(new TextBlock { Text = string.Format(group.Key.Kind.GetLocalizedResource(), group.Key.Actor, group.Count()), TextWrapping = TextWrapping.Wrap });
            header.Children.Add(new TextBlock { Text = Strings.ResourceOptimizationViewDetails.GetLocalizedResource(), FontSize = 12 });
            ChoiceGroups.Children.Add(new Expander { Header = header, Content = rows, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = false });
        }
    }

    private void UpdateSelectionState()
    {
        ExecuteButton.IsEnabled = !_isBusy && !HasChanges && _choices.Keys.Any(check => check.IsChecked == true);
    }

    private void SetChoiceEditingEnabled(bool enabled)
    {
        SelectAllButton.IsEnabled = SelectNoneButton.IsEnabled = enabled;
        foreach (var check in _choices.Keys) check.IsEnabled = enabled;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy) foreach (var check in _choices.Keys) check.IsChecked = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBusy) foreach (var check in _choices.Keys) check.IsChecked = false;
    }

    private sealed record OptimizationChoice(string Actor, string Kind, string Label,
        ResourceFileOperation? Operation = null, string? AddTagPath = null, TagRemovalMutation? RemoveTag = null);

    private static ResourceFileOperation CloneOperation(ResourceFileOperation operation) => new()
    {
        Operation = operation.Operation,
        Source = operation.Source,
        Target = operation.Target,
        Code = operation.Code,
        Status = operation.Status,
        Reason = operation.Reason,
        Backup = operation.Backup,
    };

    private bool TryBeginPreview()
    {
        if (_isBusy)
            return false;

        _isBusy = true;
        _pendingOperations = [];
        _pendingTagPaths = [];
        _pendingTagRemovals = [];
        _pendingTagId = null;
        PreviewItems.Clear();
        PreviewList.Visibility = Visibility.Collapsed;
        ExecuteButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = "正在检查当前窗口中的项目…";
        return true;
    }

    private async Task<List<ResourceBrowserItem>> GetVideoFoldersAsync(ResourceSettings settings)
    {
        var videoFolders = new List<ResourceBrowserItem>();
        if (_scopeKind == ResourceBrowserLocationKind.VideoFolder)
        {
            if (Directory.Exists(_scopePath))
                videoFolders.Add(new ResourceBrowserItem
                {
                    Name = Path.GetFileName(_scopePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    Path = _scopePath,
                    Kind = ResourceBrowserItemKind.VideoFolder,
                });
            return videoFolders;
        }

        foreach (var item in _scopeItems)
            await VisitFolderAsync(item, settings, videoFolders);

        return videoFolders;
    }

    private async Task VisitFolderAsync(
        ResourceBrowserItem folder,
        ResourceSettings settings,
        List<ResourceBrowserItem> videoFolders)
    {
        if (folder.Kind == ResourceBrowserItemKind.VideoFolder)
        {
            videoFolders.Add(folder);
            return;
        }

        var locationKind = folder.Kind == ResourceBrowserItemKind.ActorFolder
            ? ResourceBrowserLocationKind.ActorFolder
            : ResourceBrowserLocationKind.CategoryFolder;
        var children = await _browser.GetChildrenAsync(folder.Path, locationKind, settings);
        foreach (var child in children)
            await VisitFolderAsync(child, settings, videoFolders);
    }

    private string GetActorName(string videoFolderPath)
    {
        try
        {
            var relativePath = Path.GetRelativePath(_libraryPath, videoFolderPath);
            var pathParts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return pathParts.FirstOrDefault(part => !string.IsNullOrWhiteSpace(part))
                ?? Path.GetFileName(Path.GetDirectoryName(videoFolderPath))
                ?? videoFolderPath;
        }
        catch
        {
            return Path.GetFileName(Path.GetDirectoryName(videoFolderPath)) ?? videoFolderPath;
        }
    }

    private IEnumerable<string> BuildExecutedSummaries(int taggedCount, int removedTagCount)
    {
        var summaries = new Dictionary<string, ExecutionActorSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in _executedOperations)
        {
            var actorName = GetActorName(Path.GetDirectoryName(operation.Source) ?? _libraryPath);
            var summary = GetOrAddExecutionSummary(summaries, actorName);
            if (operation.Operation.StartsWith("classify", StringComparison.OrdinalIgnoreCase))
                summary.MovedNoSubtitleWorks++;
            else if (operation.Operation == "rename")
                summary.RenamedVideos++;
        }

        foreach (var path in _executedTagAddPaths)
            GetOrAddExecutionSummary(summaries, GetActorName(path)).AddedChineseSubtitleTags++;
        foreach (var mutation in _executedTagRemovals)
            GetOrAddExecutionSummary(summaries, GetActorName(mutation.RollbackPath)).RemovedChineseSubtitleTags++;

        if (summaries.Count == 0 && (taggedCount > 0 || removedTagCount > 0))
            summaries["资源库"] = new ExecutionActorSummary { AddedChineseSubtitleTags = taggedCount, RemovedChineseSubtitleTags = removedTagCount };

        return summaries
            .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair =>
            {
                var parts = new List<string>();
                if (pair.Value.RenamedVideos > 0)
                    parts.Add($"已改名 {pair.Value.RenamedVideos} 个视频");
                if (pair.Value.AddedChineseSubtitleTags > 0)
                    parts.Add($"已贴标签 {pair.Value.AddedChineseSubtitleTags}");
                if (pair.Value.MovedNoSubtitleWorks > 0)
                    parts.Add($"已移无中字 {pair.Value.MovedNoSubtitleWorks}");
                if (pair.Value.RemovedChineseSubtitleTags > 0)
                    parts.Add($"已清旧标签 {pair.Value.RemovedChineseSubtitleTags}");
                return $"{pair.Key}：{string.Join("；", parts)} 部";
            });
    }

    private static ExecutionActorSummary GetOrAddExecutionSummary(
        IDictionary<string, ExecutionActorSummary> summaries,
        string actorName)
    {
        if (!summaries.TryGetValue(actorName, out var summary))
            summaries[actorName] = summary = new ExecutionActorSummary();
        return summary;
    }

    private static string GetAvailableFolderTarget(string targetRoot, string folderName)
    {
        var targetPath = Path.Combine(targetRoot, folderName);
        if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
            return targetPath;

        for (var suffix = 2; suffix < 10000; suffix++)
        {
            targetPath = Path.Combine(targetRoot, $"{folderName}-{suffix:D2}");
            if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
                return targetPath;
        }

        throw new IOException($"无法为“{folderName}”分配不冲突的目标文件夹名。");
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ExecutionActorSummary
    {
        public int AddedChineseSubtitleTags { get; set; }
        public int MovedNoSubtitleWorks { get; set; }
        public int RemovedChineseSubtitleTags { get; set; }
        public int RenamedVideos { get; set; }
    }

    private sealed record TagRemovalMutation(string ExecutePath, string RollbackPath);

}
