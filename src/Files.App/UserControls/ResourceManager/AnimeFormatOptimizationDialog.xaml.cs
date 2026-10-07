// Copyright (c) Files Community. Licensed under the MIT License.
using Files.App.Data.Models.ResourceManager;
using Files.App.Services.ResourceManager;
using Files.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Diagnostics;
namespace Files.App.UserControls.ResourceManager;
public sealed partial class AnimeFormatOptimizationDialog : UserControl
{
    private readonly string _root;
    private string _scope;
    private readonly bool _includeScope;
    private readonly IResourceWorkspaceService _workspace;
    private readonly IResourceOperationsService _operations;
    private readonly Dictionary<CheckBox, ResourceFileOperation> _choices = [];
    private readonly Dictionary<string, string?> _assignments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (CheckBox Check, ComboBox Video, TextBlock Target)> _assignmentRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Button Execute, TextBlock Result)> _folderActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ComboBox> _groupSelectors = [];
    private bool _buildingAssignments;
    private int _selectedFunction = -1;
    private readonly Dictionary<(int Function, string Folder), bool> _expandedGroups = [];
    private bool ManualMode => _selectedFunction == (int)AnimeOptimizationKind.Illustrations;
    public List<(string SourcePath, string TargetPath)> AppliedMappings { get; } = [];
    public bool IsBusy { get; private set; }
    public bool HasChanges => AppliedMappings.Count > 0;
    public event EventHandler? RequestClose;
    public AnimeFormatOptimizationDialog(string root, string scope, bool includeScope, IResourceWorkspaceService workspace)
    {
        InitializeComponent(); _root = root; _scope = scope; _includeScope = includeScope; _workspace = workspace;
        _operations = new ResourceOperationsService(Ioc.Default.GetRequiredService<IResourceCodeParser>(), Ioc.Default.GetRequiredService<IResourceScanner>(), workspace, Ioc.Default.GetRequiredService<ILogger<ResourceOperationsService>>());
        var close = ResourceDialogPresentation.CreateIconButton("\uE8BB", Strings.Close.GetLocalizedResource());
        CloseButton.Content = new FontIcon { Glyph = "\uE8BB", FontSize = 16 }; CloseButton.Background = close.Background; CloseButton.Foreground = close.Foreground; CloseButton.CornerRadius = close.CornerRadius;
        FolderFunction.Content = CreateFunctionLabel(Strings.AnimeOptimizationFolders, Strings.AnimeOptimizationFoldersDescription);
        PosterFunction.Content = CreateFunctionLabel(Strings.AnimeOptimizationPosters, Strings.ResourceOptimizeClassificationDescription);
        IllustrationFunction.Content = CreateFunctionLabel(Strings.AnimeOptimizationIllustrations, Strings.AnimeOptimizationIllustrationsDescription);
    }
    private static StackPanel CreateFunctionLabel(string title, string description)
    {
        var label = new StackPanel { Spacing = 4 };
        label.Children.Add(new TextBlock { Text = title.GetLocalizedResource(), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        label.Children.Add(new TextBlock { Text = description.GetLocalizedResource(), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        return label;
    }
    private async void Function_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || sender is not Button button) return;
        _selectedFunction = int.Parse((string)button.Tag);
        foreach (var option in new[] { FolderFunction, PosterFunction, IllustrationFunction })
            option.BorderBrush = (Brush)Application.Current.Resources[ReferenceEquals(option, button) ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];
        await PreviewAsync();
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await PreviewAsync();
    private string GetGroupPath(ResourceFileOperation operation)
        => _selectedFunction == (int)AnimeOptimizationKind.Folders ? operation.Source : Path.GetDirectoryName(operation.Source)!;
    private void AddFolderMenu(CheckBox check, string source)
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = Strings.AnimeOptimizationOpenFolder.GetLocalizedResource(), Icon = new SymbolIcon(Symbol.OpenFile) };
        open.Click += async (_, _) =>
        {
            try
            {
                var path = source;
                foreach (var mapping in AppliedMappings)
                    if (string.Equals(path, mapping.SourcePath, StringComparison.OrdinalIgnoreCase) || path.StartsWith(mapping.SourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        path = mapping.TargetPath + path[mapping.SourcePath.Length..];
                var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
                await Windows.System.Launcher.LaunchFolderAsync(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder));
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        };
        menu.Items.Add(open); check.ContextFlyout = menu;
    }
    private Border CreateGroup(string folder, int count, StackPanel rows, bool defaultExpanded, FrameworkElement? extraHeader = null)
    {
        var key = (_selectedFunction, folder);
        var expanded = _expandedGroups.GetValueOrDefault(key, defaultExpanded);
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = Path.GetFileName(folder) + $" ({count})", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        var detail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        detail.Children.Add(new TextBlock { Text = Strings.ResourceOptimizationViewDetails.GetLocalizedResource(), Foreground = accent });
        var arrow = new FontIcon { Glyph = expanded ? "\uE70E" : "\uE70D", FontSize = 12, Foreground = accent }; detail.Children.Add(arrow);
        Grid.SetColumn(detail, 1); header.Children.Add(detail);
        var toggle = new Button { Content = header, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 12, 16, 12), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        if (extraHeader is not null) rows.Children.Insert(0, extraHeader);
        rows.Padding = new Thickness(16, 4, 16, 12); rows.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        toggle.Click += (_, _) => { var show = rows.Visibility != Visibility.Visible; _expandedGroups[key] = show;
            rows.Visibility = show ? Visibility.Visible : Visibility.Collapsed; arrow.Glyph = show ? "\uE70E" : "\uE70D"; };
        var card = new StackPanel(); card.Children.Add(toggle); card.Children.Add(rows);
        return new Border { Child = card, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] };
    }
    private void SetBusy(bool busy)
    {
        IsBusy = busy; FunctionOptions.IsHitTestVisible = !busy; RetryButton.IsEnabled = !busy; CloseButton.IsEnabled = !busy; SelectAllButton.IsEnabled = !busy; SelectNoneButton.IsEnabled = !busy;
        foreach (var pair in _choices) pair.Key.IsEnabled = !busy && (ManualMode || pair.Value.Status == "ready");
        foreach (var row in _assignmentRows.Values) row.Video.IsEnabled = !busy;
        foreach (var selector in _groupSelectors) selector.IsEnabled = !busy;
        UpdateExecute();
    }
    private void UpdateExecute()
    {
        var selected = _choices.Where(pair => pair.Key.IsChecked == true).Select(pair => pair.Value).ToArray();
        ExecuteButton.Visibility = ManualMode ? Visibility.Collapsed : Visibility.Visible;
        if (!IsBusy && !ManualMode && _choices.Count > 0) StatusText.Text = string.Format(Strings.ResourceOptimizationSelectedCount.GetLocalizedResource(), selected.Length, _choices.Count);
        foreach (var folder in _folderActions)
        {
            var selectedFolder = _choices.Where(pair => pair.Key.IsChecked == true && string.Equals(Path.GetDirectoryName(pair.Value.Source), folder.Key, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value).ToArray();
            folder.Value.Execute.IsEnabled = !IsBusy && selectedFolder.Length > 0 && selectedFolder.All(operation => operation.Status == "ready");
        }
        ExecuteButton.IsEnabled = !IsBusy && selected.Length > 0 && (ManualMode ? selected.All(operation => operation.Status == "ready") : selected.Any(operation => operation.Status == "ready"));
    }
    private async Task PreviewAsync()
    {
        if (IsBusy || _selectedFunction < 0) return; SetBusy(true); _choices.Clear(); _assignments.Clear(); _assignmentRows.Clear(); _folderActions.Clear(); _groupSelectors.Clear(); PreviewRows.Children.Clear(); StatusText.Text = Strings.AnimeOptimizationScanning.GetLocalizedResource();
        try
        {
            var operations = await AnimeFormatOptimizationService.PreviewAsync(_scope, _workspace.Settings.Clone(), (AnimeOptimizationKind)_selectedFunction, _includeScope, workspace: _workspace);
            if (ManualMode)
            {
                BuildAssignmentRows(operations);
                RebuildAssignments();
                if (operations.Count == 0) StatusText.Text = Strings.AnimeOptimizationNoChanges.GetLocalizedResource();
                return;
            }
            var groups = operations.GroupBy(operation => GetGroupPath(operation)).ToArray();
            foreach (var group in groups)
            {
                var rows = new StackPanel { Spacing = 4 };
                foreach (var operation in group)
                {
                    var details = new StackPanel { Spacing = 3 };
                    details.Children.Add(new TextBlock { Text = Path.GetFileName(operation.Source), FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap });
                    var target = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                    target.Inlines.Add(new Run { Text = Path.GetFileName(operation.Target) });
                    target.Inlines.Add(new Run { Text = "  new", Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
                    details.Children.Add(target);
                    if (operation.Status != "ready") details.Children.Add(new TextBlock { Text = operation.Reason, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                    var check = new CheckBox { Content = details, IsChecked = operation.Status == "ready", IsEnabled = operation.Status == "ready", HorizontalAlignment = HorizontalAlignment.Stretch };
                    AddFolderMenu(check, operation.Source);
                    check.Checked += (_, _) => UpdateExecute(); check.Unchecked += (_, _) => UpdateExecute(); _choices[check] = operation; rows.Children.Add(check);
                }
                PreviewRows.Children.Add(CreateGroup(group.Key, group.Count(), rows, groups.Length == 1));
            }
            StatusText.Text = operations.Count == 0 ? Strings.AnimeOptimizationNoChanges.GetLocalizedResource() : string.Format(Strings.AnimeOptimizationCount.GetLocalizedResource(), operations.Count(operation => operation.Status == "ready"), operations.Count(operation => operation.Status != "ready"));
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); RetryButton.Visibility = Visibility.Visible; SelectionButtons.Visibility = _choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
    }
    private void BuildAssignmentRows(List<ResourceFileOperation> operations)
    {
        _buildingAssignments = true;
        try
        {
            foreach (var group in operations.GroupBy(operation => Path.GetDirectoryName(operation.Source)!))
            {
                var header = new Grid { ColumnSpacing = 12 }; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.Children.Add(new TextBlock { Text = Path.GetRelativePath(_root, group.Key) + $" ({group.Count()})", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                var open = new Button { Content = Strings.AnimeOptimizationOpenFolder.GetLocalizedResource() }; Grid.SetColumn(open, 1);
                open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo { FileName = group.Key, UseShellExecute = true }); } catch (Exception ex) { StatusText.Text = ex.Message; } }; header.Children.Add(open);
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var optimize = new Button { Content = Strings.AnimeOptimizationFolderExecute.GetLocalizedResource() }; Grid.SetColumn(optimize, 2); header.Children.Add(optimize);
                var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
                _folderActions[group.Key] = (optimize, result);
                optimize.Click += async (_, _) => await ExecuteFolderAsync(group.Key);
                var rows = new StackPanel { Spacing = 12 }; rows.Children.Add(result);
                var videos = Directory.EnumerateFiles(group.Key).Where(file => _workspace.Settings.VideoExtensions.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase)).OrderBy(file => Path.GetFileName(file), new EpisodeNameComparer()).ToArray();
                foreach (var imageGroup in group.GroupBy(operation => AnimeLibraryService.GetIllustrationGroup(operation.Source) ?? string.Empty))
                {
                    var imageRows = new StackPanel { Spacing = 12 };
                    ComboBox? groupVideo = null;
                    Grid? groupHeader = null;
                    if (imageGroup.Key.Length > 0)
                    {
                        groupVideo = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
                        groupVideo.Items.Add(new ComboBoxItem { Content = Strings.AnimeOptimizationAssignGroup.GetLocalizedResource() });
                        foreach (var file in videos) groupVideo.Items.Add(new ComboBoxItem { Content = Path.GetFileName(file), Tag = file });
                        groupVideo.SelectedIndex = 0;
                        groupHeader = new Grid { ColumnSpacing = 16 }; groupHeader.ColumnDefinitions.Add(new ColumnDefinition()); groupHeader.ColumnDefinitions.Add(new ColumnDefinition());
                        groupHeader.Children.Add(new TextBlock { Text = imageGroup.Key + $" ({imageGroup.Count()})", VerticalAlignment = VerticalAlignment.Center });
                        Grid.SetColumn(groupVideo, 1); groupHeader.Children.Add(groupVideo);
                        _groupSelectors.Add(groupVideo);
                    }
                    foreach (var operation in imageGroup)
                    {
                    var source = operation.Source;
                    var check = new CheckBox { Content = Path.GetFileName(source), IsChecked = true };
                    AddFolderMenu(check, source);
                    var video = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
                    video.Items.Add(new ComboBoxItem { Content = Strings.AnimeOptimizationAssignVideo.GetLocalizedResource() });
                    foreach (var file in videos) video.Items.Add(new ComboBoxItem { Content = Path.GetFileName(file), Tag = file });
                    var number = AnimeLibraryService.GetIllustrationEpisodeNumber(operation.Target);
                    var matching = videos.Where(file => AnimeLibraryService.GetEpisodeNumber(file) == number && number is not null).ToArray();
                    var assigned = operation.Status == "ready" && matching.Length == 1 ? matching[0] : videos.Length == 1 && operation.Status == "ready" ? videos[0] : null;
                    video.SelectedIndex = assigned is null ? 0 : Array.IndexOf(videos, assigned) + 1;
                    _assignments[source] = assigned;
                    var target = new TextBlock { TextWrapping = TextWrapping.Wrap };
                    _assignmentRows[source] = (check, video, target); _choices[check] = operation;
                    var row = new Grid { RowSpacing = 4, ColumnSpacing = 16 }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition()); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    row.Children.Add(check); Grid.SetColumn(video, 1); row.Children.Add(video); Grid.SetRow(target, 1); Grid.SetColumnSpan(target, 2); row.Children.Add(target); imageRows.Children.Add(row);
                    video.SelectionChanged += (_, _) => { _assignments[source] = (video.SelectedItem as ComboBoxItem)?.Tag as string; RebuildAssignments(); };
                    check.Checked += (_, _) => RebuildAssignments(); check.Unchecked += (_, _) => RebuildAssignments();
                    }
                    if (groupVideo is not null)
                    {
                        var groupedSources = imageGroup.Select(operation => operation.Source).ToArray();
                        groupVideo.SelectionChanged += (_, _) =>
                        {
                            if (IsBusy) return;
                            var assignedVideo = (groupVideo.SelectedItem as ComboBoxItem)?.Tag as string;
                            _buildingAssignments = true;
                            try
                            {
                                foreach (var source in groupedSources.Where(source => _assignmentRows.ContainsKey(source)))
                                {
                                    var row = _assignmentRows[source];
                                    _assignments[source] = assignedVideo;
                                    row.Video.SelectedIndex = assignedVideo is null ? 0 : Array.IndexOf(videos, assignedVideo) + 1;
                                }
                            }
                            finally { _buildingAssignments = false; }
                            RebuildAssignments();
                        };
                    }
                    if (groupHeader is not null) rows.Children.Add(new Expander { Header = groupHeader, Content = imageRows, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                    else rows.Children.Add(imageRows);
                }
                PreviewRows.Children.Add(CreateGroup(group.Key, group.Count(), rows, operations.Select(GetGroupPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1, header));
            }
        }
        finally { _buildingAssignments = false; }
    }
    private void RebuildAssignments()
    {
        if (_buildingAssignments) return;
        try
        {
            var selected = _assignments.Where(pair => _assignmentRows[pair.Key].Check.IsChecked == true).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var plan = AnimeFormatOptimizationService.PreviewAssignments(selected, _workspace.Settings);
            foreach (var operation in plan)
            {
                var row = _assignmentRows[operation.Source]; _choices[row.Check] = operation;
                row.Target.Text = operation.Status == "ready" ? "→ " + Path.GetFileName(operation.Target) : operation.Reason;
            }
            foreach (var row in _assignmentRows.Values.Where(row => row.Check.IsChecked != true)) row.Target.Text = string.Empty;
            StatusText.Text = string.Format(Strings.AnimeOptimizationAssignments.GetLocalizedResource(), plan.Count(operation => operation.Status == "ready"), plan.Count(operation => operation.Status != "ready"));
        }
        catch (Exception ex) { StatusText.Text = ex.Message; ExecuteButton.IsEnabled = false; foreach (var action in _folderActions.Values) action.Execute.IsEnabled = false; return; }
        UpdateExecute();
    }

    private async Task ExecuteFolderAsync(string folder)
    {
        if (IsBusy) return;
        var selected = _choices.Where(pair => pair.Key.IsChecked == true && string.Equals(Path.GetDirectoryName(pair.Value.Source), folder, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value).ToList();
        if (selected.Count == 0 || selected.Any(operation => operation.Status != "ready")) return;
        SetBusy(true);
        var status = _folderActions[folder].Result;
        status.Visibility = Visibility.Visible;
        try
        {
            var results = await _operations.ExecuteOperationsAsync(_root, selected);
            foreach (var operation in results.Where(operation => operation.Status == "done"))
            {
                _workspace.RemapItemPaths(operation.Source, operation.Target);
                AppliedMappings.Add((operation.Source, operation.Target));
                if (_assignmentRows.Remove(operation.Source, out var row))
                {
                    if (row.Check.Parent is FrameworkElement element) element.Visibility = Visibility.Collapsed;
                    _choices.Remove(row.Check);
                }
                _assignments.Remove(operation.Source);
            }
            status.Text = Strings.AnimeOptimizationCompleted.GetLocalizedResource() + " · " + string.Format(Strings.AnimeOptimizationResult.GetLocalizedResource(), results.Count(operation => operation.Status == "done"), results.Count(operation => operation.Status != "done" && operation.Status != "ok"));
            foreach (var operation in results.Where(operation => operation.Status != "done" && operation.Status != "ok")) status.Text += "\n" + Path.GetFileName(operation.Source) + " · " + operation.Reason;
            RebuildAssignments();
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return; var selected = _choices.Where(pair => pair.Key.IsChecked == true && pair.Value.Status == "ready").Select(pair => pair.Value).ToList(); if (selected.Count == 0) return; SetBusy(true);
        try
        {
            var results = await _operations.ExecuteOperationsAsync(_root, selected);
            foreach (var result in results.Where(result => result.Status == "done")) {
                _workspace.RemapItemPaths(result.Source, result.Target); AppliedMappings.Add((result.Source, result.Target));
                if (string.Equals(_scope, result.Source, StringComparison.OrdinalIgnoreCase) || _scope.StartsWith(result.Source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) _scope = result.Target + _scope[result.Source.Length..];
            }
            _choices.Clear(); _assignmentRows.Clear(); _assignments.Clear();
            CompletionRows.Children.Clear();
            foreach (var result in results)
                CompletionRows.Children.Add(new TextBlock { Text = Path.GetRelativePath(_root, result.Source) + " → " + Path.GetFileName(result.Target) + (result.Status == "done" || result.Status == "ok" ? string.Empty : " · " + result.Reason), TextWrapping = TextWrapping.Wrap });
            CompletionSummary.Text = string.Format(Strings.AnimeOptimizationResult.GetLocalizedResource(), results.Count(result => result.Status == "done"), results.Count(result => result.Status != "done" && result.Status != "ok"));
            FunctionOptions.Visibility = PreviewScroll.Visibility = PreviewFooter.Visibility = Visibility.Collapsed;
            CompletionView.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        CompletionView.Visibility = Visibility.Collapsed;
        FunctionOptions.Visibility = PreviewScroll.Visibility = PreviewFooter.Visibility = Visibility.Visible;
        await PreviewAsync();
    }
    private void Close_Click(object sender, RoutedEventArgs e) { if (!IsBusy) RequestClose?.Invoke(this, EventArgs.Empty); }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var pair in _choices.Where(pair => ManualMode || pair.Value.Status == "ready").ToArray()) pair.Key.IsChecked = true; }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var pair in _choices.ToArray()) pair.Key.IsChecked = false; }
}
