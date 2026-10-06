// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Models.ResourceManager;
using Files.App.Services.ResourceManager;
using Files.App.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
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
    private string _scopePath;
    private readonly ResourceBrowserLocationKind _scopeKind;
    private readonly Dictionary<string, string> _posterVideos = new(StringComparer.OrdinalIgnoreCase);
    private List<ResourceFileOperation> _pendingOperations = [];
    private List<string> _pendingTagPaths = [];
    private List<string> _previewTagPaths = [];
    private List<TagRemovalMutation> _pendingTagRemovals = [];
    private List<ResourceFileOperation> _executedOperations = [];
    private List<string> _executedTagAddPaths = [];
    private List<TagRemovalMutation> _executedTagRemovals = [];
    private string? _pendingTagId;
    private bool _isBusy;
    private int _selectedFunction = -1;
    private readonly Dictionary<(int Function, string Actor, string Kind), bool> _expandedGroups = [];

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
        OptimizeNames.Content = CreateFunctionLabel(Strings.ResourceOptimizeNames, Strings.ResourceOptimizeNamesDescription);
        OptimizeClassification.Content = CreateFunctionLabel(Strings.ResourceOptimizeClassification, Strings.ResourceOptimizeClassificationDescription);
        OptimizeSubtitleTags.Content = CreateFunctionLabel(Strings.ResourceOptimizeSubtitleTags, Strings.ResourceOptimizeSubtitleTagsDescription);
        RetryButton.Content = Strings.Retry.GetLocalizedResource();
        _libraryPath = Path.GetFullPath(libraryPath);
        _scopePath = Path.GetFullPath(scopePath);
        _scopeKind = scopeKind;
        SelectAllButton.Content = Strings.ResourceOptimizationSelectAll.GetLocalizedResource();
        SelectNoneButton.Content = Strings.ResourceOptimizationSelectNone.GetLocalizedResource();
        ExecuteButton.Content = Strings.ResourceOptimizationConfirm.GetLocalizedResource();
        var close = ResourceDialogPresentation.CreateIconButton("\uE8BB", Strings.Close.GetLocalizedResource());
        CloseButton.Content = new FontIcon { Glyph = "\uE8BB", FontSize = 16 };
        CloseButton.Background = close.Background;
        CloseButton.Foreground = close.Foreground;
    }

    private static StackPanel CreateFunctionLabel(string title, string description)
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock { Text = title.GetLocalizedResource(), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = description.GetLocalizedResource(), FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap });
        return content;
    }

    private void OptimizationFunction_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _selectedFunction = ReferenceEquals(sender, OptimizeNames) ? 0 : ReferenceEquals(sender, OptimizeClassification) ? 1 : 2;
        foreach (var button in new[] { OptimizeNames, OptimizeClassification, OptimizeSubtitleTags })
            button.BorderBrush = (Brush)Application.Current.Resources[ReferenceEquals(button, sender) ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];
        PreviewFormatOptimization_Click(sender, e);
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFunction >= 0) PreviewFormatOptimization_Click(sender, e);
    }

    private async void PreviewFormatOptimization_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginPreview())
            return;

        try
        {
            var settings = _workspace.Settings.Clone();
            settings.Normalize();
            var folders = await GetVideoFoldersAsync(settings);
            var existingTag = _selectedFunction == 2 ? _workspace.GetResourceTagByName(ChineseSubtitleTagName) : null;
            var tagId = existingTag?.Uid;
            var moves = new List<ResourceFileOperation>();
            var tagPaths = new List<string>();
            var tagRemovals = new List<TagRemovalMutation>();
            var actorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_selectedFunction == 2) foreach (var folder in folders)
            {
                var actorName = GetActorName(folder.Path);
                actorNames.Add(actorName);
                var code = _codeParser.ParseCode(folder.Name);
                var hasMarker = ResourceCodeParser.HasSubtitleMarkerBeforeTitle(folder.Name, code);
                var hasTag = tagId is not null && _workspace.GetResourceTagIds(folder.Path).Contains(tagId, StringComparer.OrdinalIgnoreCase);
                if (hasMarker || hasTag)
                {
                    if (!hasTag) tagPaths.Add(folder.Path);
                    var parentFolder = Directory.GetParent(folder.Path);
                    var moveOut = parentFolder?.Name.Equals(NoSubtitleFolderName, StringComparison.OrdinalIgnoreCase) == true && parentFolder.Parent is not null;
                    var name = !hasMarker && code is not null ? ResourceCodeParser.AddSubtitleMarker(folder.Name, code) : folder.Name;
                    var target = Path.Combine(moveOut ? parentFolder!.Parent!.FullName : Path.GetDirectoryName(folder.Path)!, name);
                    if (!string.Equals(folder.Path, target, StringComparison.OrdinalIgnoreCase))
                        moves.Add(new ResourceFileOperation { Operation = moveOut ? "classify_with_subtitle" : "classify_subtitle_name", Source = folder.Path, Target = target,
                            Code = code?.Normalized, Status = Directory.Exists(target) || File.Exists(target) ? "conflict" : "ready" });
                    continue;
                }
                var parent = Directory.GetParent(folder.Path);
                if (parent is null || string.Equals(parent.Name, NoSubtitleFolderName, StringComparison.OrdinalIgnoreCase)) continue;

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
            }

            var renameOperations = _selectedFunction == 0
                ? await _operations.PreviewRenameVideosInFoldersAsync(_libraryPath, folders.Select(folder => folder.Path), settings)
                : new List<ResourceFileOperation>();
            foreach (var operation in renameOperations)
                actorNames.Add(GetActorName(Path.GetDirectoryName(operation.Source) ?? _libraryPath));

            _posterVideos.Clear();
            var posterOperations = new List<ResourceFileOperation>();
            if (_selectedFunction == 1) foreach (var folder in folders)
            {
                var children = await _browser.GetChildrenAsync(folder.Path, ResourceBrowserLocationKind.VideoFolder, settings);
                foreach (var video in children.Where(item => item.Kind == ResourceBrowserItemKind.VideoFile)
                    .OrderBy(item => item.Name, new EpisodeNameComparer()))
                {
                    var poster = _workspace.GetPosterOverride(folder.Path) ?? video.PosterPath;
                    if (poster is not null && ResourceBrowserService.IsResourceIllustration(poster)) continue;
                    if (string.IsNullOrWhiteSpace(poster) || !File.Exists(poster) || _posterVideos.ContainsKey(poster)
                        || !string.Equals(Path.GetDirectoryName(poster), Path.GetDirectoryName(video.Path), StringComparison.OrdinalIgnoreCase)) continue;
                    _posterVideos[poster] = video.Path;
                    var target = Path.ChangeExtension(video.Path, Path.GetExtension(poster));
                    if (string.Equals(poster, target, StringComparison.OrdinalIgnoreCase)) continue;
                    posterOperations.Add(new ResourceFileOperation { Operation = "rename_poster", Source = poster, Target = target,
                        Status = File.Exists(target) || Directory.Exists(target) ? "conflict" : "ready" });
                }
            }
            _pendingOperations = renameOperations.Concat(posterOperations).Concat(moves).ToList();
            _pendingTagPaths = tagPaths;
            _previewTagPaths = tagPaths;
            _pendingTagRemovals = tagRemovals;
            _pendingTagId = tagId;

            BuildChoiceGroups();
            StatusText.Text = _pendingTagPaths.Count == 0 && _choices.Count == 0 ? Strings.ResourceFormatNoChanges.GetLocalizedResource() : string.Format(Strings.ResourceOptimizationSelectedCount.GetLocalizedResource(), _choices.Count(pair => pair.Key.IsChecked == true && IsFunctionSelected(pair.Value)), _choices.Count);
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
            SetChoiceEditingEnabled(true);
            RetryButton.Visibility = Visibility.Visible;
            UpdateSelectionState();
        }
    }

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var selected = _choices.Where(pair => pair.Key.IsChecked == true && IsFunctionSelected(pair.Value)).Select(pair => pair.Value).ToList();
        _pendingTagPaths = selected.Where(choice => choice.AddTagPath is not null).Select(choice => choice.AddTagPath!).ToList();
        if (selected.Count == 0 && _pendingTagPaths.Count == 0) return;
        _pendingOperations = selected.Where(choice => choice.Operation is not null).Select(choice => choice.Operation!).ToList();
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
            foreach (var operation in _executedOperations.Where(operation => operation.Operation.StartsWith("classify", StringComparison.Ordinal)))
                if (string.Equals(_scopePath, operation.Source, StringComparison.OrdinalIgnoreCase)) _scopePath = operation.Target;
            var movedCount = _executedOperations.Count(result => result.Operation == "classify_no_subtitle");
            var movedOutCount = _executedOperations.Count(result => result.Operation == "classify_with_subtitle");
            var renamedPosterCount = _executedOperations.Count(result => result.Operation == "rename_poster");
            var renamedFolderCount = _executedOperations.Count(result => result.Operation == "classify_subtitle_name");
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
                    .Select(path => _executedOperations.FirstOrDefault(operation => string.Equals(operation.Source, path, StringComparison.OrdinalIgnoreCase))?.Target ?? path)
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
                _choices.Clear();
                _previewTagPaths.Clear();
                SelectionButtons.Visibility = Visibility.Collapsed;
                PreviewItems.Clear();
                foreach (var line in BuildExecutedSummaries(taggedCount, removedTagCount))
                    PreviewItems.Add(line);
                PreviewList.Visibility = PreviewItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            StatusText.Text = $"格式优化完成：视频文件改名 {renamedCount} 个；海报改名 {renamedPosterCount} 张；补充-C {renamedFolderCount} 部；归档无中字作品 {movedCount} 部；移出无中字分类 {movedOutCount} 部；新增中文字幕标签 {taggedCount} 部；清除不匹配标签 {removedTagCount} 部。{(failedCount > 0 ? $"另有 {failedCount} 项冲突或失败。" : string.Empty)}";

            if (hasChanges)
            {
                snapshot.Operations = _executedOperations.Select(CloneOperation).ToList();
                snapshot.Summary = StatusText.Text;
                if (!_workspace.SaveResourceToolSnapshot(snapshot))
                    StatusText.Text += " 回退快照已在执行前保存，但最新统计未能更新。";
                StatusText.Text += "回退快照已保存，可在设置 > 追剧中回退或删除。";
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
            SetChoiceEditingEnabled(true);
            UpdateSelectionState();
        }
    }

    private void BuildChoiceGroups()
    {
        _choices.Clear();
        ChoiceGroups.Children.Clear();
        ChoiceGroups.Visibility = SelectionButtons.Visibility = Visibility.Visible;
        var choices = _pendingOperations.Where(op => op.Status == "ready" || op.Operation == "rename_poster")
            .Select(op => new OptimizationChoice(GetActorName(Path.GetDirectoryName(op.Source) ?? _libraryPath),
                op.Operation is "classify_no_subtitle" or "classify_with_subtitle" ? Strings.ResourceOptimizationMoveSummary : Strings.ResourceOptimizationRenameSummary,
                $"{Path.GetFileName(op.Source)} → {Path.GetFileName(op.Target)}", Operation: op))
            .Concat(_previewTagPaths.Select(path => new OptimizationChoice(GetActorName(path), Strings.ResourceOptimizationAddTagSummary,
                Path.GetFileName(path), AddTagPath: path)))
            .Concat(_pendingTagRemovals.Select(change => new OptimizationChoice(GetActorName(change.RollbackPath), Strings.ResourceOptimizationRemoveTagSummary,
                Path.GetFileName(change.RollbackPath), RemoveTag: change))).ToArray();
        var singleActor = choices.Select(choice => choice.Actor).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == 1;
        foreach (var group in choices.GroupBy(choice => (choice.Actor, choice.Kind)).OrderBy(group => group.Key.Actor, StringComparer.CurrentCultureIgnoreCase))
        {
            var rows = new StackPanel { Spacing = 4 };
            foreach (var choice in group)
            {
                var check = new CheckBox { Content = CreateChoiceDetails(choice), IsChecked = true, IsEnabled = IsFunctionSelected(choice), HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                ToolTipService.SetToolTip(check, choice.Label);
                var menu = new MenuFlyout();
                var openFolder = new MenuFlyoutItem { Text = Strings.AnimeOptimizationOpenFolder.GetLocalizedResource(), Icon = new SymbolIcon(Symbol.OpenFile) };
                openFolder.Click += async (_, _) =>
                {
                    try
                    {
                        var path = choice.Operation?.Source ?? choice.AddTagPath ?? choice.RemoveTag?.RollbackPath;
                        if (path is null) return;
                        if (!File.Exists(path) && !Directory.Exists(path) && choice.Operation is { } operation) path = operation.Target;
                        var folderPath = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                        if (folderPath is null) return;
                        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folderPath);
                        await Windows.System.Launcher.LaunchFolderAsync(folder);
                    }
                    catch (Exception ex) { App.Logger.LogWarning(ex, "Unable to open optimization item folder"); }
                };
                menu.Items.Add(openFolder);
                check.ContextFlyout = menu;
                _choices.Add(check, choice);
                check.Checked += (_, _) => UpdateSelectionState(updateSummary: true);
                check.Unchecked += (_, _) => UpdateSelectionState(updateSummary: true);
                rows.Children.Add(check);
            }
            var key = (_selectedFunction, group.Key.Actor, group.Key.Kind);
            var expanded = _expandedGroups.GetValueOrDefault(key, singleActor);
            _expandedGroups.TryAdd(key, expanded);
            var header = new Grid { ColumnSpacing = 16 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = string.Format(group.Key.Kind.GetLocalizedResource(), group.Key.Actor, group.Count()),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            details.Children.Add(new TextBlock { Text = Strings.ResourceOptimizationViewDetails.GetLocalizedResource(), Foreground = accent });
            var arrow = new FontIcon { Glyph = expanded ? "\uE70E" : "\uE70D", FontSize = 12, Foreground = accent };
            details.Children.Add(arrow);
            Grid.SetColumn(details, 1);
            header.Children.Add(details);
            var toggle = new Button { Content = header, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16, 12, 16, 12), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
            rows.Padding = new Thickness(16, 4, 16, 12);
            rows.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            toggle.Click += (_, _) =>
            {
                var show = rows.Visibility != Visibility.Visible;
                _expandedGroups[key] = show;
                rows.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                arrow.Glyph = show ? "\uE70E" : "\uE70D";
            };
            var card = new StackPanel();
            card.Children.Add(toggle);
            card.Children.Add(rows);
            ChoiceGroups.Children.Add(new Border { Child = card, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] });
        }
    }

    private static FrameworkElement CreateChoiceDetails(OptimizationChoice choice)
    {
        var content = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 4) };
        TextBlock Text(string value, bool secondary = false) => new()
        {
            Text = value, TextWrapping = TextWrapping.Wrap, FontSize = secondary ? 12 : 14,
            Foreground = (Brush)Application.Current.Resources[secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"]
        };
        if (choice.Operation is { } operation)
        {
            var oldName = Path.GetFileName(operation.Source);
            content.Children.Add(Text(oldName, secondary: true));
            if (operation.Operation is "classify_no_subtitle" or "classify_with_subtitle")
                content.Children.Add(Text((operation.Operation == "classify_no_subtitle" ? Strings.ResourceOptimizationMovedNoSubtitle : Strings.ResourceOptimizationMovedFromNoSubtitle).GetLocalizedResource()));
            else
            {
                var name = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                name.Inlines.Add(new Run { Text = Path.GetFileName(operation.Target) });
                name.Inlines.Add(new Run { Text = "  new", Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
                content.Children.Add(name);
            }
            if (operation.Status == "conflict") content.Children.Add(Text(Strings.ResourceOptimizationTargetConflict.GetLocalizedResource(), secondary: true));
        }
        else
        {
            content.Children.Add(Text(choice.Label));
            var tag = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            tag.Inlines.Add(new Run { Text = (choice.AddTagPath is not null ? Strings.ResourceOptimizationTagAdded : Strings.ResourceOptimizationTagRemoved).GetLocalizedResource() + " " });
            tag.Inlines.Add(new Run { Text = ChineseSubtitleTagName, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
            content.Children.Add(tag);
        }
        return content;
    }

    private bool IsFunctionSelected(OptimizationChoice choice)
        => choice.Operation is { } operation
            ? operation.Status == "ready" && (operation.Operation == "rename" ? _selectedFunction == 0 : operation.Operation == "rename_poster" ? _selectedFunction == 1 : _selectedFunction == 2)
            : _selectedFunction == 2;

    private void UpdateSelectionState(bool updateSummary = false)
    {
        ExecuteButton.IsEnabled = !_isBusy && _choices.Any(pair => pair.Key.IsChecked == true && IsFunctionSelected(pair.Value));
        if (updateSummary && !_isBusy && _choices.Count > 0)
            StatusText.Text = string.Format(Strings.ResourceOptimizationSelectedCount.GetLocalizedResource(),
                _choices.Count(pair => pair.Key.IsChecked == true && IsFunctionSelected(pair.Value)), _choices.Count);
    }

    private void SetChoiceEditingEnabled(bool enabled)
    {
        SelectAllButton.IsEnabled = SelectNoneButton.IsEnabled = enabled;
        RetryButton.IsEnabled = enabled;
        OptimizeNames.IsEnabled = OptimizeClassification.IsEnabled = OptimizeSubtitleTags.IsEnabled = enabled;
        foreach (var pair in _choices) pair.Key.IsEnabled = enabled && IsFunctionSelected(pair.Value);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        foreach (var check in _choices.Keys) check.IsChecked = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        foreach (var check in _choices.Keys) check.IsChecked = false;
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
        SetChoiceEditingEnabled(false);
        _choices.Clear();
        ChoiceGroups.Children.Clear();
        ChoiceGroups.Visibility = SelectionButtons.Visibility = Visibility.Collapsed;
        _pendingOperations = [];
        _pendingTagPaths = [];
        _previewTagPaths = [];
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

        var currentItems = await _browser.GetChildrenAsync(_scopePath, _scopeKind, settings);
        foreach (var item in currentItems)
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
            if (operation.Operation == "classify_no_subtitle")
                summary.MovedNoSubtitleWorks++;
            else if (operation.Operation == "classify_with_subtitle")
                summary.MovedOutSubtitleWorks++;
            else if (operation.Operation == "rename")
                summary.RenamedVideos++;
            else if (operation.Operation == "rename_poster")
                summary.RenamedPosters++;
            else if (operation.Operation == "classify_subtitle_name")
                summary.RenamedSubtitleFolders++;
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
                if (pair.Value.RenamedPosters > 0)
                    parts.Add($"已改名 {pair.Value.RenamedPosters} 张海报");
                if (pair.Value.RenamedSubtitleFolders > 0)
                    parts.Add($"已补充-C {pair.Value.RenamedSubtitleFolders} 部");
                if (pair.Value.AddedChineseSubtitleTags > 0)
                    parts.Add($"已贴标签 {pair.Value.AddedChineseSubtitleTags}");
                if (pair.Value.MovedNoSubtitleWorks > 0)
                    parts.Add($"已移无中字 {pair.Value.MovedNoSubtitleWorks}");
                if (pair.Value.MovedOutSubtitleWorks > 0)
                    parts.Add($"已移出无中字分类 {pair.Value.MovedOutSubtitleWorks}");
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
        public int MovedOutSubtitleWorks { get; set; }
        public int RemovedChineseSubtitleTags { get; set; }
        public int RenamedPosters { get; set; }
        public int RenamedSubtitleFolders { get; set; }
        public int RenamedVideos { get; set; }
    }

    private sealed record TagRemovalMutation(string ExecutePath, string RollbackPath);

}
