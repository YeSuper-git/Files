// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;
using Files.App.Helpers;
using Files.App.Services.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using System.Globalization;
using System.IO;

namespace Files.App.UserControls.ResourceManager;

public sealed class MikanAnimeDiscoveryDialog
{
    private readonly IResourceWorkspaceService _workspace;
    private readonly MikanProjectService _mikan;
    private readonly BangumiScraperService? _bangumi;
    private readonly string _libraryPath;
    private readonly ContentDialog _dialog;
    private readonly ComboBox _yearSelector = new() { MinWidth = 110 };
    private readonly ListView _items = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly Image _poster = new() { Width = 154, Height = 216, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _airing = new() { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
    private readonly TextBlock _synopsis = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _releasePanel = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
    private readonly ComboBox _categorySelector = new() { MinWidth = 260 };
    private readonly ComboBox _targetFolderSelector = new() { MinWidth = 280 };
    private readonly ComboBox _candidateFilter = new() { MinWidth = 150 };
    private readonly ComboBox _metadataSourceSelector = new() { MinWidth = 150 };
    private readonly ComboBox _bangumiSelector = new() { MinWidth = 300, Visibility = Visibility.Collapsed };
    private readonly ComboBox _groupSelector = new() { MinWidth = 190 };
    private readonly ComboBox _versionSelector = new() { MinWidth = 230, Visibility = Visibility.Collapsed };
    private readonly Button _addButton = new() { Content = Strings.MikanAddFollow.GetLocalizedResource() };
    private readonly Button _copyAllButton = new() { Content = Strings.MikanCopyAllMagnets.GetLocalizedResource() };
    private readonly Button _markSeenButton = new() { Content = Strings.MikanMarkUpdatesSeen.GetLocalizedResource(), Visibility = Visibility.Collapsed };
    private readonly Button _refreshButton = new() { Content = Strings.MikanRefresh.GetLocalizedResource() };
    private readonly Button _savePreferenceButton = new() { Content = Strings.MikanSaveReleasePreference.GetLocalizedResource(), Visibility = Visibility.Collapsed };
    private readonly Button _wantButton = new() { Content = Strings.MikanWantToFollow.GetLocalizedResource(), Visibility = Visibility.Collapsed };
    private readonly Button _dismissButton = new() { Content = Strings.MikanNotInterested.GetLocalizedResource(), Visibility = Visibility.Collapsed };
    private readonly Button _discoverTab = new() { Content = Strings.MikanDiscoverTab.GetLocalizedResource() };
    private readonly Button _updatesTab = new() { Content = Strings.MikanUpdatesTab.GetLocalizedResource() };
    private readonly List<MikanDisplayRow> _rows = [];
    private MikanAnimeInfo? _currentInfo;
    private MikanDisplayRow? _currentRow;
    private BangumiSubject? _selectedBangumiSubject;
    private IReadOnlyList<MikanEpisodeRelease> _visibleReleases = [];
    private bool _updatingReleaseFilters;
    private bool _updatingMetadataSource;
    private bool _showUpdates;
    private bool _isBusy;

    private sealed record MikanDisplayRow(MikanAnimeCandidate Candidate, MikanAnimeFollow? Follow, int NewReleaseCount = 0)
    {
        public string DisplayLabel => Follow is null
            ? Disposition switch
            {
                MikanAnimeCandidateDisposition.WantToFollow => $"{Candidate.Title}  ·  {Strings.MikanWantToFollow.GetLocalizedResource()}",
                MikanAnimeCandidateDisposition.NotInterested => $"{Candidate.Title}  ·  {Strings.MikanNotInterested.GetLocalizedResource()}",
                _ => Candidate.Title,
            }
            : NewReleaseCount > 0
                ? $"{Candidate.Title}  ·  {string.Format(Strings.MikanNewReleaseCount.GetLocalizedResource(), NewReleaseCount)}"
                : Candidate.Title;

        public MikanAnimeCandidateDisposition Disposition { get; init; } = MikanAnimeCandidateDisposition.Undecided;
    }

    private sealed record CategoryOption(string Name, string Path)
    {
        public override string ToString() => Name;
    }

    private sealed record FolderOption(string Name, string? Path)
    {
        public override string ToString() => Name;
    }

    private sealed record DispositionOption(string Name, MikanAnimeCandidateDisposition? Disposition)
    {
        public override string ToString() => Name;
    }

    private sealed record GroupOption(string Name, int? Id)
    {
        public override string ToString() => Name;
    }

    private sealed record VersionOption(string Name, string? Key)
    {
        public override string ToString() => Name;
    }

    private sealed record SeasonOption(int Year, string Season, string Label)
    {
        public override string ToString() => Label;
    }

    private MikanAnimeDiscoveryDialog(XamlRoot xamlRoot, IResourceWorkspaceService workspace, MikanProjectService mikan, string libraryPath, BangumiScraperService? bangumi)
    {
        _workspace = workspace;
        _mikan = mikan;
        _bangumi = bangumi;
        _libraryPath = libraryPath;
        _dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Strings.MikanDiscoveryTitle.GetLocalizedResource(),
            CloseButtonText = Strings.AnimeLibraryCancel.GetLocalizedResource(),
            DefaultButton = ContentDialogButton.Close,
            MaxWidth = 1120,
            Content = BuildContent(),
        };
        _items.SelectionChanged += async (_, _) => await LoadSelectedAsync();
        _discoverTab.Click += async (_, _) => await SwitchModeAsync(false);
        _updatesTab.Click += async (_, _) => await SwitchModeAsync(true);
        _refreshButton.Click += async (_, _) => await LoadItemsAsync();
        _savePreferenceButton.Click += (_, _) => SaveReleasePreference();
        _addButton.Click += async (_, _) => await AddFollowAsync();
        _copyAllButton.Click += async (_, _) => await CopyAllMagnetsAsync();
        _markSeenButton.Click += (_, _) => MarkUpdatesSeen();
        _wantButton.Click += (_, _) => SetCandidateDisposition(MikanAnimeCandidateDisposition.WantToFollow);
        _dismissButton.Click += (_, _) => SetCandidateDisposition(MikanAnimeCandidateDisposition.NotInterested);
        _categorySelector.SelectionChanged += (_, _) => PopulateTargetFolderSelector();
        _candidateFilter.SelectionChanged += async (_, _) => { if (!_showUpdates) await LoadItemsAsync(); };
        _groupSelector.SelectionChanged += (_, _) => { if (!_updatingReleaseFilters) UpdateReleaseFilter(); };
        _versionSelector.SelectionChanged += (_, _) => { if (!_updatingReleaseFilters) RenderSelectedReleases(); };
        _metadataSourceSelector.SelectionChanged += async (_, _) => await UpdateMetadataSourceAsync();
        _bangumiSelector.SelectionChanged += (_, _) => UpdateSelectedBangumiMetadata();
        PopulateSeasonSelector();
        _yearSelector.SelectionChanged += async (_, _) => await LoadItemsAsync();
        PopulateCategorySelector();
    }

    public static async Task ShowAsync(XamlRoot xamlRoot, IResourceWorkspaceService workspace, MikanProjectService mikan, string libraryPath, BangumiScraperService? bangumi = null, int? initialMikanId = null)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
            return;

        var view = new MikanAnimeDiscoveryDialog(xamlRoot, workspace, mikan, libraryPath, bangumi);
        view._dialog.Opened += async (_, _) =>
        {
            if (initialMikanId is null)
            {
                await view.LoadItemsAsync();
                return;
            }

            await view.SwitchModeAsync(true);
            var index = view._rows.FindIndex(row => row.Candidate.Id == initialMikanId);
            if (index >= 0)
                view._items.SelectedIndex = index;
        };
        _ = await view._dialog.ShowAsync();
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Width = 980, Height = 650, RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(_discoverTab);
        Grid.SetColumn(_updatesTab, 1); toolbar.Children.Add(_updatesTab);
        Grid.SetColumn(_yearSelector, 2); toolbar.Children.Add(_yearSelector);
        Grid.SetColumn(_candidateFilter, 3); toolbar.Children.Add(_candidateFilter);
        Grid.SetColumn(_refreshButton, 5); toolbar.Children.Add(_refreshButton);
        Grid.SetColumn(_copyAllButton, 6); toolbar.Children.Add(_copyAllButton);
        Grid.SetRow(toolbar, 0); root.Children.Add(toolbar);

        var body = new Grid { ColumnSpacing = 14 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            CornerRadius = new CornerRadius(6),
            Child = _items,
        };
        body.Children.Add(left);

        var detail = new Grid { RowSpacing = 10 };
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var metadata = new Grid { ColumnSpacing = 16 };
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metadata.Children.Add(_poster);
        var summary = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        summary.Children.Add(_title);
        summary.Children.Add(_airing);
        var sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sourceRow.Children.Add(new TextBlock { Text = Strings.MikanMetadataSource.GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Center });
        sourceRow.Children.Add(_metadataSourceSelector);
        sourceRow.Children.Add(_bangumiSelector);
        summary.Children.Add(sourceRow);
        summary.Children.Add(new TextBlock { Text = Strings.AnimeLibrarySynopsis.GetLocalizedResource(), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        summary.Children.Add(_synopsis);
        Grid.SetColumn(summary, 1); metadata.Children.Add(summary);
        detail.Children.Add(metadata);

        var releaseContent = new StackPanel { Spacing = 8 };
        var releaseFilterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        releaseFilterRow.Children.Add(new TextBlock { Text = Strings.MikanGroupFilter.GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Center });
        releaseFilterRow.Children.Add(_groupSelector);
        releaseFilterRow.Children.Add(_versionSelector);
        releaseFilterRow.Children.Add(_savePreferenceButton);
        releaseContent.Children.Add(releaseFilterRow);
        releaseContent.Children.Add(_releasePanel);
        var releaseScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = releaseContent };
        Grid.SetRow(releaseScroll, 1); detail.Children.Add(releaseScroll);

        var footer = new StackPanel { Spacing = 8 };
        var categoryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        categoryRow.Children.Add(new TextBlock { Text = Strings.MikanTargetCategory.GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Center });
        categoryRow.Children.Add(_categorySelector);
        categoryRow.Children.Add(_targetFolderSelector);
        categoryRow.Children.Add(_addButton);
        categoryRow.Children.Add(_wantButton);
        categoryRow.Children.Add(_dismissButton);
        categoryRow.Children.Add(_markSeenButton);
        footer.Children.Add(categoryRow);
        footer.Children.Add(_status);
        Grid.SetRow(footer, 2); detail.Children.Add(footer);

        Grid.SetColumn(detail, 1); body.Children.Add(detail);
        Grid.SetRow(body, 1); root.Children.Add(body);
        return root;
    }

    private void PopulateSeasonSelector()
    {
        var now = DateTime.Now;
        var currentSeason = now.Month switch { <= 3 => "冬", <= 6 => "春", <= 9 => "夏", _ => "秋" };
        var seasons = new List<SeasonOption>();
        for (var year = now.Year; year >= now.Year - 2; year--)
            foreach (var (season, label) in new[] { ("冬", "冬季"), ("秋", "秋季"), ("夏", "夏季"), ("春", "春季") })
                seasons.Add(new SeasonOption(year, season, $"{year} {label}"));
        _yearSelector.ItemsSource = seasons;
        _yearSelector.DisplayMemberPath = nameof(SeasonOption.Label);
        _yearSelector.SelectedItem = seasons.FirstOrDefault(option => option.Year == now.Year && option.Season == currentSeason);
        _candidateFilter.ItemsSource = new[]
        {
            new DispositionOption(Strings.MikanFilterAllCandidates.GetLocalizedResource(), null),
            new DispositionOption(Strings.MikanFilterUndecided.GetLocalizedResource(), MikanAnimeCandidateDisposition.Undecided),
            new DispositionOption(Strings.MikanWantToFollow.GetLocalizedResource(), MikanAnimeCandidateDisposition.WantToFollow),
            new DispositionOption(Strings.MikanNotInterested.GetLocalizedResource(), MikanAnimeCandidateDisposition.NotInterested),
        };
        _candidateFilter.SelectedIndex = 0;
        _metadataSourceSelector.Items.Add(Strings.MikanSourceMikan.GetLocalizedResource());
        _metadataSourceSelector.Items.Add(Strings.MikanSourceBangumi.GetLocalizedResource());
        _metadataSourceSelector.SelectedIndex = 0;
    }

    private void PopulateCategorySelector()
    {
        try
        {
            _categorySelector.ItemsSource = Directory.EnumerateDirectories(_libraryPath)
                .Select(path => new CategoryOption(Path.GetFileName(path), path))
                .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (_categorySelector.Items.Count > 0)
                _categorySelector.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Strings.MikanLoadFailed.GetLocalizedResource(), ex.Message);
        }
    }

    private void PopulateTargetFolderSelector()
    {
        if (_categorySelector.SelectedItem is not CategoryOption category)
        {
            _targetFolderSelector.ItemsSource = null;
            return;
        }

        try
        {
            var folderName = SanitizeFolderName(_currentInfo?.Candidate.Title ?? _currentRow?.Candidate.Title ?? string.Empty);
            var options = new List<FolderOption> { new(string.Format(Strings.MikanCreateFolderOption.GetLocalizedResource(), folderName), null) };
            options.AddRange(Directory.EnumerateDirectories(category.Path)
                .Select(path => new FolderOption(Path.GetFileName(path), path))
                .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase));
            _targetFolderSelector.ItemsSource = options;
            var sameName = options.FindIndex(option => string.Equals(option.Name, folderName, StringComparison.OrdinalIgnoreCase));
            _targetFolderSelector.SelectedIndex = sameName > 0 ? sameName : 0;
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Strings.MikanLoadFailed.GetLocalizedResource(), ex.Message);
        }
    }

    private async Task SwitchModeAsync(bool showUpdates)
    {
        if (_showUpdates == showUpdates)
            return;
        _showUpdates = showUpdates;
        _yearSelector.Visibility = showUpdates ? Visibility.Collapsed : Visibility.Visible;
        _candidateFilter.Visibility = showUpdates ? Visibility.Collapsed : Visibility.Visible;
        _categorySelector.Visibility = showUpdates ? Visibility.Collapsed : Visibility.Visible;
        _targetFolderSelector.Visibility = showUpdates ? Visibility.Collapsed : Visibility.Visible;
        _addButton.Visibility = showUpdates ? Visibility.Collapsed : Visibility.Visible;
        _wantButton.Visibility = showUpdates ? Visibility.Collapsed : Visibility.Visible;
        _dismissButton.Visibility = showUpdates ? Visibility.Collapsed : Visibility.Visible;
        _markSeenButton.Visibility = showUpdates ? Visibility.Visible : Visibility.Collapsed;
        await LoadItemsAsync();
    }

    private async Task LoadItemsAsync()
    {
        if (_isBusy)
            return;
        _isBusy = true;
        _refreshButton.IsEnabled = false;
        _items.ItemsSource = null;
        _rows.Clear();
        _currentInfo = null;
        _currentRow = null;
        _releasePanel.Children.Clear();
        _status.Text = Strings.MikanLoading.GetLocalizedResource();
        try
        {
            if (_showUpdates)
            {
                var follows = _workspace.MikanAnimeFollows;
                var semaphore = new SemaphoreSlim(4);
                var loaded = await Task.WhenAll(follows.Select(async follow =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var info = await _mikan.GetAnimeAsync(follow.MikanBangumiId);
                        return (Follow: follow, Info: info, Error: (Exception?)null);
                    }
                    catch (Exception ex)
                    {
                        return (Follow: follow, Info: (MikanAnimeInfo?)null, Error: ex);
                    }
                    finally { semaphore.Release(); }
                }));
                var checkedAt = DateTimeOffset.Now;
                var updateRows = new List<MikanDisplayRow>();
                foreach (var result in loaded)
                {
                    if (result.Info is null)
                    {
                        updateRows.Add(new MikanDisplayRow(new MikanAnimeCandidate(result.Follow.MikanBangumiId, result.Follow.Title, string.Empty), result.Follow));
                        continue;
                    }
                    var relevantReleases = GetPreferredReleases(result.Follow, result.Info);
                    var newCount = relevantReleases.Count(release => !result.Follow.SeenEpisodeIds.Contains(release.Id, StringComparer.OrdinalIgnoreCase));
                    _workspace.UpdateMikanFollowCheck(result.Follow.MikanBangumiId, checkedAt, newCount);
                    var refreshedFollow = _workspace.MikanAnimeFollows.First(item => item.MikanBangumiId == result.Follow.MikanBangumiId);
                    updateRows.Add(new MikanDisplayRow(result.Info.Candidate, refreshedFollow, newCount));
                }
                _rows.AddRange(updateRows.OrderByDescending(row => row.NewReleaseCount).ThenBy(row => row.Candidate.Title, StringComparer.CurrentCultureIgnoreCase));
                _status.Text = string.Format(Strings.MikanFollowingCount.GetLocalizedResource(), _rows.Count, _rows.Sum(row => row.NewReleaseCount));
            }
            else
            {
                if (_yearSelector.SelectedItem is not SeasonOption season)
                    return;
                var dispositions = _workspace.MikanAnimeCandidateStates.ToDictionary(item => item.MikanBangumiId, item => item.Disposition);
                var filter = (_candidateFilter.SelectedItem as DispositionOption)?.Disposition;
                var candidates = await _mikan.GetSeasonAsync(season.Year, season.Season);
                foreach (var candidate in candidates)
                {
                    var follow = _workspace.MikanAnimeFollows.FirstOrDefault(item => item.MikanBangumiId == candidate.Id);
                    var disposition = dispositions.GetValueOrDefault(candidate.Id, MikanAnimeCandidateDisposition.Undecided);
                    if (follow is null && (filter is null || filter == disposition))
                        _rows.Add(new MikanDisplayRow(candidate, null) { Disposition = disposition });
                    else if (follow is not null)
                        _rows.Add(new MikanDisplayRow(candidate, follow));
                }
                _status.Text = string.Format(Strings.MikanCandidateCount.GetLocalizedResource(), _rows.Count);
            }
            _items.ItemsSource = _rows;
            _items.DisplayMemberPath = nameof(MikanDisplayRow.DisplayLabel);
            if (_rows.Count > 0)
                _items.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Strings.MikanLoadFailed.GetLocalizedResource(), ex.Message);
        }
        finally
        {
            _refreshButton.IsEnabled = true;
            _isBusy = false;
            if (_items.SelectedItem is MikanDisplayRow selected)
                await LoadSelectedAsync();
        }
    }

    private async Task LoadSelectedAsync()
    {
        if (_items.SelectedItem is not MikanDisplayRow row || _isBusy)
            return;
        _currentRow = row;
        _currentInfo = null;
        _releasePanel.Children.Clear();
        _title.Text = row.Candidate.Title;
        _airing.Text = string.Empty;
        _synopsis.Text = Strings.MikanLoading.GetLocalizedResource();
        _poster.Source = null;
        _addButton.IsEnabled = row.Follow is null;
        _wantButton.Visibility = row.Follow is null && !_showUpdates ? Visibility.Visible : Visibility.Collapsed;
        _dismissButton.Visibility = row.Follow is null && !_showUpdates ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = Strings.MikanLoading.GetLocalizedResource();
        try
        {
            var info = await _mikan.GetAnimeAsync(row.Candidate.Id);
            if (_items.SelectedItem is not MikanDisplayRow selected || selected.Candidate.Id != row.Candidate.Id)
                return;
            _currentInfo = info;
            _selectedBangumiSubject = null;
            _updatingMetadataSource = true;
            _metadataSourceSelector.SelectedIndex = 0;
            _bangumiSelector.ItemsSource = null;
            _bangumiSelector.Visibility = Visibility.Collapsed;
            _metadataSourceSelector.Visibility = _bangumi is null ? Visibility.Collapsed : Visibility.Visible;
            _updatingMetadataSource = false;
            _title.Text = info.Candidate.Title;
            _airing.Text = info.AiringInfo;
            _synopsis.Text = string.IsNullOrWhiteSpace(info.Synopsis) ? Strings.AnimeLibraryNoSynopsis.GetLocalizedResource() : info.Synopsis;
            if (!string.IsNullOrWhiteSpace(info.Candidate.PosterUrl))
                _poster.Source = new BitmapImage(new Uri(info.Candidate.PosterUrl));
            PopulateReleaseFilters(info);
            RenderReleases(info);
            var known = row.Follow?.SeenEpisodeIds ?? [];
            var unread = _visibleReleases.Count(release => row.Follow is not null && !known.Contains(release.Id, StringComparer.OrdinalIgnoreCase));
            _status.Text = row.Follow is null
                ? string.Format(Strings.MikanReleaseCount.GetLocalizedResource(), info.Groups.Count, info.Releases.Count)
                : string.Format(Strings.MikanUpdateStatusChecked.GetLocalizedResource(), unread, info.Releases.Count, row.Follow.LastCheckedAt?.ToString("g", CultureInfo.CurrentCulture) ?? Strings.MikanNeverChecked.GetLocalizedResource());
            _addButton.IsEnabled = row.Follow is null && !_showUpdates;
            _markSeenButton.IsEnabled = row.Follow is not null && unread > 0;
            _savePreferenceButton.Visibility = row.Follow is not null ? Visibility.Visible : Visibility.Collapsed;
            PopulateTargetFolderSelector();
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Strings.MikanLoadFailed.GetLocalizedResource(), ex.Message);
        }
    }

    private void RenderReleases(MikanAnimeInfo info)
    {
        _releasePanel.Children.Clear();
        var selectedGroupId = (_groupSelector.SelectedItem as GroupOption)?.Id;
        var selectedVariant = (_versionSelector.SelectedItem as VersionOption)?.Key;
        var groups = info.Groups.Where(group => selectedGroupId is null || group.Id == selectedGroupId.Value).ToList();
        foreach (var group in groups)
        {
            var releases = group.Releases
                .Where(release => string.IsNullOrWhiteSpace(selectedVariant) || string.Equals(release.VariantKey, selectedVariant, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _releasePanel.Children.Add(new TextBlock
            {
                Text = $"{group.Name}  ·  {releases.Count}",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            });
            foreach (var release in releases)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel { Spacing = 3 };
                text.Children.Add(new TextBlock { Text = release.Title, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                text.Children.Add(new TextBlock
                {
                    Text = string.Join("  ·  ", new[] { release.VariantKey, release.Size, release.PublishedAt }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
                row.Children.Add(text);
                var copy = new Button { Content = Strings.MikanCopyMagnet.GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Center, Tag = release.MagnetUri };
                copy.Click += async (_, _) => await CopyMagnetAsync((string)copy.Tag);
                Grid.SetColumn(copy, 1); row.Children.Add(copy);
                _releasePanel.Children.Add(new Border
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    Padding = new Thickness(8, 6, 4, 6),
                    Child = row,
                });
            }
        }
        _visibleReleases = groups.SelectMany(group => group.Releases)
            .Where(release => string.IsNullOrWhiteSpace(selectedVariant) || string.Equals(release.VariantKey, selectedVariant, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (_releasePanel.Children.Count == 0)
            _releasePanel.Children.Add(new TextBlock { Text = Strings.MikanNoReleases.GetLocalizedResource() });
    }

    private void PopulateReleaseFilters(MikanAnimeInfo info)
    {
        _updatingReleaseFilters = true;
        var groups = new List<GroupOption> { new(Strings.MikanAllGroups.GetLocalizedResource(), null) };
        groups.AddRange(info.Groups.Select(group => new GroupOption(group.Name, group.Id)));
        _groupSelector.ItemsSource = groups;
        _groupSelector.DisplayMemberPath = nameof(GroupOption.Name);
        var preferredId = _currentRow?.Follow?.PreferredGroupId;
        _groupSelector.SelectedItem = groups.FirstOrDefault(group => group.Id == preferredId) ?? groups[0];
        _savePreferenceButton.Visibility = _currentRow?.Follow is not null ? Visibility.Visible : Visibility.Collapsed;
        PopulateVariantFilter();
        _updatingReleaseFilters = false;
    }

    private void PopulateVariantFilter()
    {
        var selectedGroup = _groupSelector.SelectedItem as GroupOption;
        var group = selectedGroup?.Id is int groupId ? _currentInfo?.Groups.FirstOrDefault(item => item.Id == groupId) : null;
        _versionSelector.Visibility = group is null ? Visibility.Collapsed : Visibility.Visible;
        if (group is null)
        {
            _versionSelector.ItemsSource = null;
            return;
        }

        var variants = group.Releases.GroupBy(release => release.VariantKey, StringComparer.OrdinalIgnoreCase)
            .Select(items => new VersionOption($"{items.Key}  ·  {items.Count()}", items.Key))
            .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        variants.Insert(0, new VersionOption(Strings.MikanAllVersions.GetLocalizedResource(), null));
        _versionSelector.ItemsSource = variants;
        _versionSelector.DisplayMemberPath = nameof(VersionOption.Name);
        var preferred = _currentRow?.Follow?.PreferredVariants?.GetValueOrDefault(group.Id);
        _versionSelector.SelectedItem = variants.FirstOrDefault(option => option.Key is not null && string.Equals(option.Key, preferred, StringComparison.OrdinalIgnoreCase)) ?? variants[0];
    }

    private void UpdateReleaseFilter()
    {
        if (_currentInfo is null)
            return;
        _updatingReleaseFilters = true;
        if (_groupSelector.SelectedItem is GroupOption)
            PopulateVariantFilter();
        _updatingReleaseFilters = false;
        RenderSelectedReleases();
    }

    private void RenderSelectedReleases()
    {
        if (_currentInfo is not null)
            RenderReleases(_currentInfo);
    }

    private void SaveReleasePreference()
    {
        if (_currentRow?.Follow is not { } follow || _currentInfo is null)
            return;
        var groupId = (_groupSelector.SelectedItem as GroupOption)?.Id;
        var variant = groupId is null ? null : (_versionSelector.SelectedItem as VersionOption)?.Key;
        _workspace.SaveMikanReleasePreferences(follow.MikanBangumiId, groupId, variant);
        var updated = _workspace.MikanAnimeFollows.First(item => item.MikanBangumiId == follow.MikanBangumiId);
        _currentRow = _currentRow with { Follow = updated };
        RenderReleases(_currentInfo!);
        _status.Text = Strings.MikanReleasePreferenceSaved.GetLocalizedResource();
    }

    private static IReadOnlyList<MikanEpisodeRelease> GetPreferredReleases(MikanAnimeFollow? follow, MikanAnimeInfo info)
    {
        if (follow is null)
            return info.Releases;
        var releases = info.Releases.AsEnumerable();
        if (follow.PreferredGroupId is int groupId)
            releases = releases.Where(release => release.GroupId == groupId);
        if (follow.PreferredGroupId is int preferredGroup && follow.PreferredVariants?.TryGetValue(preferredGroup, out var variant) == true)
            releases = releases.Where(release => release.GroupId == preferredGroup && string.Equals(release.VariantKey, variant, StringComparison.OrdinalIgnoreCase));
        return releases.ToArray();
    }

    private void SetCandidateDisposition(MikanAnimeCandidateDisposition disposition)
    {
        if (_currentRow is null || _currentRow.Follow is not null)
            return;
        _workspace.SetMikanAnimeCandidateDisposition(_currentRow.Candidate.Id, _currentRow.Candidate.Title, disposition);
        var index = _rows.FindIndex(row => row.Candidate.Id == _currentRow.Candidate.Id);
        if (index < 0)
            return;
        _rows[index] = _currentRow with { Disposition = disposition };
        _currentRow = _rows[index];
        _items.ItemsSource = null;
        _items.ItemsSource = _rows;
        _items.DisplayMemberPath = nameof(MikanDisplayRow.DisplayLabel);
        _items.SelectedIndex = index;
        _status.Text = disposition == MikanAnimeCandidateDisposition.WantToFollow
            ? Strings.MikanMarkedWantToFollow.GetLocalizedResource()
            : Strings.MikanMarkedNotInterested.GetLocalizedResource();
    }

    private async Task UpdateMetadataSourceAsync()
    {
        if (_updatingMetadataSource || _currentInfo is null)
            return;
        if (_metadataSourceSelector.SelectedIndex == 0)
        {
            _selectedBangumiSubject = null;
            _bangumiSelector.Visibility = Visibility.Collapsed;
            _synopsis.Text = string.IsNullOrWhiteSpace(_currentInfo.Synopsis) ? Strings.AnimeLibraryNoSynopsis.GetLocalizedResource() : _currentInfo.Synopsis;
            _airing.Text = _currentInfo.AiringInfo;
            _poster.Source = string.IsNullOrWhiteSpace(_currentInfo.Candidate.PosterUrl) ? null : new BitmapImage(new Uri(_currentInfo.Candidate.PosterUrl));
            return;
        }

        if (_bangumi is null)
        {
            _status.Text = Strings.MikanBangumiUnavailable.GetLocalizedResource();
            return;
        }
        _bangumiSelector.Visibility = Visibility.Visible;
        _bangumiSelector.ItemsSource = null;
        _status.Text = Strings.MikanSearchingBangumi.GetLocalizedResource();
        try
        {
            var results = await _bangumi.SearchAnimeAsync(_currentInfo.Candidate.Title);
            if (_currentInfo is null || _metadataSourceSelector.SelectedIndex != 1)
                return;
            _bangumiSelector.ItemsSource = results;
            _bangumiSelector.DisplayMemberPath = nameof(BangumiSubject.DisplayLabel);
            var preferredId = _currentRow?.Follow?.BangumiSubjectId;
            var preferred = results.FirstOrDefault(subject => subject.Id == preferredId);
            if (preferred is not null)
                _bangumiSelector.SelectedItem = preferred;
            else if (results.Count == 1)
                _bangumiSelector.SelectedIndex = 0;
            else
                _status.Text = results.Count == 0 ? Strings.MikanBangumiNoResults.GetLocalizedResource() : Strings.MikanBangumiChooseResult.GetLocalizedResource();
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Strings.MikanBangumiSearchFailed.GetLocalizedResource(), ex.Message);
        }
    }

    private void UpdateSelectedBangumiMetadata()
    {
        if (_bangumiSelector.SelectedItem is not BangumiSubject subject || _metadataSourceSelector.SelectedIndex != 1)
            return;
        _selectedBangumiSubject = subject;
        _synopsis.Text = string.IsNullOrWhiteSpace(subject.Summary) ? Strings.AnimeLibraryNoSynopsis.GetLocalizedResource() : subject.Summary;
        _airing.Text = subject.Date ?? _currentInfo?.AiringInfo ?? string.Empty;
        _poster.Source = string.IsNullOrWhiteSpace(subject.ImageUrl) ? null : new BitmapImage(new Uri(subject.ImageUrl));
        _status.Text = Strings.MikanBangumiPreviewSelected.GetLocalizedResource();
    }

    private async Task AddFollowAsync()
    {
        if (_currentRow is null || _currentInfo is null || _currentRow.Follow is not null)
            return;
        if (_categorySelector.SelectedItem is not CategoryOption category)
        {
            _status.Text = Strings.MikanChooseCategory.GetLocalizedResource();
            return;
        }

        var folderName = SanitizeFolderName(_currentInfo.Candidate.Title);
        if (string.IsNullOrWhiteSpace(folderName))
            return;
        if (_targetFolderSelector.SelectedItem is not FolderOption target)
        {
            _status.Text = Strings.MikanChooseTargetFolder.GetLocalizedResource();
            return;
        }
        var isNewFolder = target.Path is null;
        var folderPath = target.Path ?? Path.Combine(category.Path, folderName);
        if (!isNewFolder && !Directory.Exists(folderPath))
        {
            _status.Text = Strings.MikanTargetFolderMissing.GetLocalizedResource();
            PopulateTargetFolderSelector();
            return;
        }
        if (isNewFolder && Directory.Exists(folderPath))
        {
            _status.Text = Strings.MikanFolderExists.GetLocalizedResource();
            PopulateTargetFolderSelector();
            return;
        }
        if (_workspace.MikanAnimeFollows.Any(item => PathEquals(item.FolderPath, folderPath)))
        {
            _status.Text = Strings.MikanFolderAlreadyLinked.GetLocalizedResource();
            return;
        }
        if (_metadataSourceSelector.SelectedIndex == 1 && _selectedBangumiSubject is null)
        {
            _status.Text = Strings.MikanBangumiChooseResult.GetLocalizedResource();
            return;
        }

        var synopsis = _selectedBangumiSubject?.Summary ?? _currentInfo.Synopsis;
        var airDateText = _selectedBangumiSubject?.Date;
        var airDate = ParseFlexibleAirDate(airDateText) ?? ParseAirDate(_currentInfo.AiringInfo);
        var bangumiSubjectId = _selectedBangumiSubject?.Id ?? _currentInfo.BangumiSubjectId;
        var posterUrl = _selectedBangumiSubject?.ImageUrl ?? _currentInfo.Candidate.PosterUrl;

        _addButton.IsEnabled = false;
        try
        {
            if (isNewFolder)
                Directory.CreateDirectory(folderPath);
            var details = new ResourceVideoDetails
            {
                Synopsis = synopsis,
                AirDate = airDate,
                BangumiSubjectId = bangumiSubjectId,
            };
            _workspace.SetVideoDetails(folderPath, details);
            string? posterError = null;
            var existingPoster = !isNewFolder && HasPosterFile(folderPath);
            if (_workspace.GetPosterOverride(folderPath) is null && !existingPoster && !string.IsNullOrWhiteSpace(posterUrl))
            {
                string? temporaryImage = null;
                try
                {
                    var bytes = _selectedBangumiSubject is not null && _bangumi is not null
                        ? await _bangumi.DownloadImageAsync(posterUrl)
                        : await _mikan.DownloadImageAsync(posterUrl);
                    var extension = posterUrl.Contains("format=webp", StringComparison.OrdinalIgnoreCase) ? ".webp" : ".jpg";
                    temporaryImage = Path.Combine(Path.GetTempPath(), $"mikan-{Guid.NewGuid():N}{extension}");
                    await File.WriteAllBytesAsync(temporaryImage, bytes);
                    var importedPoster = await _workspace.ImportPosterAsync(temporaryImage);
                    _workspace.SetPosterOverride(folderPath, importedPoster);
                }
                catch (Exception ex)
                {
                    posterError = ex.Message;
                }
                finally
                {
                    if (temporaryImage is not null && File.Exists(temporaryImage))
                        File.Delete(temporaryImage);
                }
            }

            _workspace.AddMikanAnimeFollow(new MikanAnimeFollow
            {
                MikanBangumiId = _currentInfo.Candidate.Id,
                BangumiSubjectId = bangumiSubjectId,
                Title = _currentInfo.Candidate.Title,
                FolderPath = folderPath,
                SeenEpisodeIds = _currentInfo.Releases.Select(release => release.Id).ToList(),
            });
            _currentRow = _currentRow with { Follow = _workspace.MikanAnimeFollows.First(item => item.MikanBangumiId == _currentInfo.Candidate.Id) };
            _addButton.Visibility = Visibility.Collapsed;
            _wantButton.Visibility = Visibility.Collapsed;
            _dismissButton.Visibility = Visibility.Collapsed;
            _status.Text = posterError is not null
                ? string.Format(Strings.MikanFollowAddedPosterFailed.GetLocalizedResource(), posterError)
                : isNewFolder ? Strings.MikanFollowAdded.GetLocalizedResource() : Strings.MikanFolderLinked.GetLocalizedResource();
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(Strings.MikanAddFailed.GetLocalizedResource(), ex.Message);
            _addButton.IsEnabled = true;
        }
    }

    private async Task CopyAllMagnetsAsync()
    {
        if (_currentInfo is null || _visibleReleases.Count == 0)
            return;
        await CopyMagnetAsync(string.Join(Environment.NewLine, _visibleReleases.Select(release => release.MagnetUri).Where(uri => !string.IsNullOrWhiteSpace(uri))));
        _status.Text = string.Format(Strings.MikanCopiedMagnets.GetLocalizedResource(), _visibleReleases.Count);
    }

    private async Task CopyMagnetAsync(string magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet))
            return;
        var package = new DataPackage();
        package.SetText(magnet);
        Clipboard.SetContent(package);
        await Task.CompletedTask;
    }

    private void MarkUpdatesSeen()
    {
        if (_currentRow?.Follow is not { } follow || _currentInfo is null)
            return;
        _workspace.MarkMikanEpisodesSeen(follow.MikanBangumiId, _visibleReleases.Select(release => release.Id));
        _status.Text = Strings.MikanUpdatesMarkedSeen.GetLocalizedResource();
        _markSeenButton.IsEnabled = false;
        _ = LoadItemsAsync();
    }

    private static DateTimeOffset? ParseAirDate(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d{1,2}/\d{1,2}/\d{4}");
        return match.Success && DateTimeOffset.TryParse(match.Value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeLocal, out var date)
            ? date
            : null;
    }

    private static DateTimeOffset? ParseFlexibleAirDate(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date : null;

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(name.Select(character => invalid.Contains(character) ? ' ' : character)).Trim().TrimEnd('.');
    }

    private static bool HasPosterFile(string folderPath)
    {
        try
        {
            var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
            return Directory.EnumerateFiles(folderPath)
                .Any(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
