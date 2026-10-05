// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Contracts;
using Files.App.Data.Enums;
using Files.App.Data.EventArguments;
using Files.App.Data.Items.ResourceManager;
using Files.App.Data.Models;
using Files.App.Data.Models.ResourceManager;
using Files.App.Helpers;
using Files.App.Services.ResourceManager;
using Files.App.Services.Settings;
using Files.App.Utils;
using Files.App.Utils.FileTags;
using Files.App.UserControls.Assistant;
using Files.App.UserControls.ResourceManager;
using Files.App.ViewModels.Assistant;
using Files.App.ViewModels.ResourceManager;
using Files.App.ViewModels.UserControls;
using Files.App.Views.Shells;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;
using WinRT;

namespace Files.App.Views.ResourceManager;

public sealed partial class ResourceLibraryPage : Page, IPageSettingsContext
{
    public SettingsPageKind SettingsPage => _animeLibrary ? SettingsPageKind.AnimeLibraryPage : SettingsPageKind.ResourceManagerPage;
    private Dictionary<string, ResourceBrowserViewState> _viewStates = new(StringComparer.OrdinalIgnoreCase);
    private string? _loadedLocationKey;
    private string? _currentAnimeSeasonPath;
    private bool _restoringPosition;
    private bool _restoringSorting;
    private ResourceBrowserItemViewModel? _detailFolderItem;
    private ResourceVideoFolderListedItem? _detailFolderListedItem;
    private string? _pendingAnimeEditPath;
    private string? _animeEditingPath;
    private string[] _animeEditingPaths = [];
    private List<BitmapImage> _illustrationImages = [];
    private readonly Dictionary<BitmapImage, string> _animeIllustrationCache = [];
    private readonly Dictionary<BitmapImage, string> _illustrationPaths = [];

    private IResourceBrowserService _browser = Ioc.Default.GetRequiredService<IResourceBrowserService>();
    private IResourceWorkspaceService _workspace = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();
    private readonly IResourceTitleTranslationService _machineTranslation = Ioc.Default.GetRequiredService<IResourceTitleTranslationService>();
    private readonly IBailianQwenMtTitleTranslationService _bailianTranslation = Ioc.Default.GetRequiredService<IBailianQwenMtTitleTranslationService>();
    private readonly IAppSettingsService _appSettings = Ioc.Default.GetRequiredService<IAppSettingsService>();
    private readonly VideoAssistantSearchService _videoAssistantSearch = Ioc.Default.GetRequiredService<VideoAssistantSearchService>();
    private readonly IContentPageContext _contentPageContext = Ioc.Default.GetRequiredService<IContentPageContext>();
    private readonly IDisplayPageContext _displayContext = Ioc.Default.GetRequiredService<IDisplayPageContext>();
    private readonly Dictionary<ResourceBrowserItemViewModel, ListedItem> _sortItems = [];
    private readonly Dictionary<string, (DateTime Modified, Task<BitmapImage?> Image)> _posterCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _posterLoads = new(8);
    private readonly List<ResourceBrowserLocation> _locations = [];
    private readonly List<ActorVideoGroup> _actorVideoGroups = [];
    private readonly Dictionary<string, (ListedItem Item, ResourceBrowserItemViewModel ViewModel)> _selectedResourceItems = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loadCancellation;
    private VideoAssistantChatView? _assistantChatView;
    private Storyboard? _assistantButtonScaleAnimation;
    private int _animeSeasonSelection;
    private bool _animeLibrary;
    private string _libraryPath = string.Empty;
    private int _selectedActorGroupIndex;
    private string? _selectedActorGroupName;
    private string? _selectedActorGroupActorPath;
    private readonly HashSet<string> _titleTranslationsInProgress = new(StringComparer.OrdinalIgnoreCase);
    private Windows.Foundation.Point? _selectionDragOrigin;
    private ResourceBrowserItemViewModel? _activeVideoItem;

    public System.Collections.ObjectModel.ObservableCollection<ResourceBrowserItemViewModel> BrowserItems { get; } = [];
    public bool IsAnimeLibrary => _animeLibrary;
    public string CurrentPath => _locations.Count > 0 ? _locations[^1].Path : _libraryPath;

    private sealed record ActorVideoGroup(string Name, IReadOnlyList<ResourceBrowserItem> Items);

    public ResourceLibraryPage()
    {
        InitializeComponent();
        DataContext = this;
        foreach (var list in new ListViewBase[] { BrowserGrid, VideoFileList, AnimeEpisodeCards, AnimeEpisodeButtons })
        {
            list.AddHandler(PointerPressedEvent, new PointerEventHandler(BrowserGrid_PointerPressed), true);
            list.AddHandler(PointerMovedEvent, new PointerEventHandler(BrowserGrid_PointerMoved), true);
            list.AddHandler(PointerReleasedEvent, new PointerEventHandler(BrowserGrid_PointerReleased), true);
            list.AddHandler(PointerCanceledEvent, new PointerEventHandler(BrowserGrid_PointerReleased), true);
        }
        Loaded += (_, _) => { _displayContext.PropertyChanged -= OnSortingChanged; _displayContext.PropertyChanged += OnSortingChanged; RestoreBrowserSorting(); ApplyBrowserSorting(); };
        Unloaded += OnPageUnloaded;
    }

    private async void OnOpenVideoAssistant(object sender, RoutedEventArgs e)
    {
        if (AssistantFloatingPanel.Visibility == Visibility.Visible)
            return;

        try
        {
            if (_assistantChatView is null)
            {
                _assistantChatView = new VideoAssistantChatView();
                _assistantChatView.CloseRequested += (_, _) => AssistantFloatingPanel.Visibility = Visibility.Collapsed;
                AssistantFloatingPanel.Child = _assistantChatView;
            }

            AssistantFloatingPanel.Visibility = Visibility.Visible;
            await _assistantChatView.ConfigureAsync(new VideoAssistantContext
            {
                LibraryPath = _libraryPath,
                OpenResourceManagerAsync = () =>
                {
                    AssistantFloatingPanel.Visibility = Visibility.Collapsed;
                    _contentPageContext.ShellPage?.NavigateToResourceManager();
                    return Task.CompletedTask;
                },
                OpenLocationAsync = async candidate =>
                {
                    AssistantFloatingPanel.Visibility = Visibility.Collapsed;
                    await TryNavigateToResourcePathAsync(candidate.FolderPath);
                },
            });
        }
        catch (Exception ex)
        {
            AssistantFloatingPanel.Visibility = Visibility.Collapsed;
            SetResourceStatusMessage($"小咪打开失败：{ex.Message}");
        }
    }

    private void AssistantButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        => AnimateAssistantButtonScale(1.12);

    private void AssistantButton_PointerExited(object sender, PointerRoutedEventArgs e)
        => AnimateAssistantButtonScale(1);

    private void AssistantButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        => AnimateAssistantButtonScale(0.94);

    private void AssistantButton_PointerReleased(object sender, PointerRoutedEventArgs e)
        => AnimateAssistantButtonScale(1.12);

    private void AnimateAssistantButtonScale(double scale)
    {
        _assistantButtonScaleAnimation?.Stop();

        var animation = new DoubleAnimation
        {
            To = scale,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, AssistantButtonScaleTransform);
        Storyboard.SetTargetProperty(animation, nameof(ScaleTransform.ScaleX));
        storyboard.Children.Add(animation);

        var verticalAnimation = new DoubleAnimation
        {
            To = scale,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(verticalAnimation, AssistantButtonScaleTransform);
        Storyboard.SetTargetProperty(verticalAnimation, nameof(ScaleTransform.ScaleY));
        storyboard.Children.Add(verticalAnimation);

        _assistantButtonScaleAnimation = storyboard;
        storyboard.Begin();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            base.OnNavigatedTo(e);
            var args = e.Parameter as NavigationArguments;
            _viewStates = args?.ResourceViewStates ?? new(StringComparer.OrdinalIgnoreCase);
            if (args is not null) args.ResourceViewStates = _viewStates;
            _animeLibrary = args?.NavPathParam == "AnimeLibrary";
            _pendingAnimeEditPath = _animeLibrary ? args?.AnimeEditPath : null;
            if (args is not null) args.AnimeEditPath = null;
            if (_animeLibrary)
            {
                var anime = Ioc.Default.GetRequiredService<AnimeLibraryService>();
                _workspace = anime.Workspace;
                _browser = anime.Browser;
                AnimeRootPanel.Visibility = Visibility.Collapsed;
                AnimeRootButton.Content = Strings.AnimeLibraryChooseRoot.GetLocalizedResource();
                AnimeRootPath.Text = _workspace.LibraryPath;
                UpdateAnimePosterLayout();
                AssistantEntry.Visibility = Visibility.Collapsed;
            }
            VideoIllustrationsHeading.Text = (_animeLibrary ? Strings.AnimePreviewImages : Strings.ResourceVideoIllustrations).GetLocalizedResource();
            AddIllustrationsButton.Content = (_animeLibrary ? Strings.AnimeManualAddImages : Strings.ResourceVideoAddIllustration).GetLocalizedResource();
            AutoRecognizeIllustrations.Visibility = _animeLibrary ? Visibility.Visible : Visibility.Collapsed;
            var libraryPath = args?.ResourceLibraryPath ?? _workspace.LibraryPath;
            if (string.IsNullOrWhiteSpace(libraryPath)) { EmptyText.Visibility = Visibility.Visible; return; }
            _libraryPath = Path.GetFullPath(libraryPath);

            if (string.IsNullOrWhiteSpace(_libraryPath) || !Directory.Exists(_libraryPath))
            {
                SetResourceStatusMessage("资源库路径不可用，请重新选择路径。");
                return;
            }

            _locations.Clear();
            if (args?.IsResourceLibraryPage == true && TryRestoreLocations(args))
            {
                // The navigation entry already contains a validated virtual path trail.
            }
            else
            {
                _locations.Add(new ResourceBrowserLocation(_libraryPath, ResourceBrowserLocationKind.LibraryRoot, _libraryPath));
            }

            SetNativeSelection(null);
            await LoadLocationAsync(_locations[^1]);
        }
        catch (Exception ex)
        {
            App.Logger.LogError(ex, "Unable to initialize the resource library page for {LibraryPath}", _libraryPath);
            SetResourceStatusMessage($"资源管理初始化失败：{ex.Message}");
            EmptyText.Visibility = Visibility.Visible;
            LoadingRing.IsActive = false;
        }
    }

    private bool TryRestoreLocations(NavigationArguments args)
    {
        var paths = args.ResourceLocationPaths;
        if (paths is null || paths.Length == 0 ||
            !PathEquals(paths[0], _libraryPath) ||
            args.ResourceLocationKinds is not { Length: var kindsLength } || kindsLength != paths.Length)
            return false;

        var titles = args.ResourceLocationTitles;
        var restored = new List<ResourceBrowserLocation>(paths.Length);
        for (var index = 0; index < paths.Length; index++)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(paths[index]);
            }
            catch
            {
                return false;
            }

            if (!ResourceManagerPathScope.IsWithinLibrary(fullPath, _libraryPath) || !Directory.Exists(fullPath))
                return false;

            if (index == 0 && args.ResourceLocationKinds[index] != ResourceBrowserLocationKind.LibraryRoot)
                return false;

            var title = titles is not null && index < titles.Length && !string.IsNullOrWhiteSpace(titles[index])
                ? titles[index]
                : index == 0 ? _libraryPath : Path.GetFileName(fullPath);
            restored.Add(new ResourceBrowserLocation(fullPath, args.ResourceLocationKinds[index], title));
        }

        _locations.AddRange(restored);
        return true;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        RememberBrowserPosition();
        _displayContext.PropertyChanged -= OnSortingChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _displayContext.PropertyChanged -= OnSortingChanged;
        _sortItems.Clear();
        _loadCancellation?.Cancel();
        AssistantFloatingPanel.Visibility = Visibility.Collapsed;
        ClearSelectedResourceItems();
        if (GetNativeResourceStatusBarViewModel() is { } statusBarViewModel)
            statusBarViewModel.DirectoryItemCount = null;
    }

    private void OnSortingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IDisplayPageContext.SortOption) or nameof(IDisplayPageContext.SortDirection)
            or nameof(IDisplayPageContext.SortFilesFirst) or nameof(IDisplayPageContext.SortDirectoriesAlongsideFiles))
        {
            if (_contentPageContext.ShellPage is ModernShellPage shell && ReferenceEquals(shell.CurrentResourceLibraryPage, this))
            {
                if (_restoringSorting || _loadedLocationKey is null) return;
                var settings = _workspace.Settings.Clone();
                settings.BrowserSortOption = (int)_displayContext.SortOption;
                settings.BrowserSortDirection = (int)_displayContext.SortDirection;
                settings.BrowserSortFilesFirst = _displayContext.SortFilesFirst;
                settings.BrowserSortDirectoriesAlongsideFiles = _displayContext.SortDirectoriesAlongsideFiles;
                _workspace.UpdateSettings(settings);
                ApplyBrowserSorting();
            }
        }
    }

    private void RestoreBrowserSorting()
    {
        var settings = _workspace.Settings;
        _restoringSorting = true;
        try
        {
            if (settings.BrowserSortOption is int option && Enum.IsDefined(typeof(SortOption), option))
                _displayContext.SortOption = (SortOption)option;
            if (settings.BrowserSortDirection is int direction && Enum.IsDefined(typeof(SortDirection), direction))
                _displayContext.SortDirection = (SortDirection)direction;
            if (settings.BrowserSortOption is not null)
            {
                _displayContext.SortFilesFirst = settings.BrowserSortFilesFirst;
                _displayContext.SortDirectoriesAlongsideFiles = settings.BrowserSortDirectoriesAlongsideFiles;
            }
        }
        finally { _restoringSorting = false; }
    }

    private void ApplyBrowserSorting()
    {
        var current = BrowserItems.ToArray();
        if (_animeLibrary && current.Length > 0 && current.All(item => item.Kind == ResourceBrowserItemKind.VideoFile))
        {
            var episodes = current.OrderBy(item => item.Name, new EpisodeNameComparer()).ToArray();
            for (var index = 0; index < episodes.Length; index++)
            {
                var oldIndex = BrowserItems.IndexOf(episodes[index]);
                if (oldIndex != index) BrowserItems.Move(oldIndex, index);
            }
            return;
        }
        if (_animeLibrary && _displayContext.SortOption == SortOption.AnimeAirDate)
        {
            var dated = current.Select(item => (Item: item, Date: GetAnimeAirDate(item))).ToArray();
            var ordered = dated.OrderBy(item => item.Date is null);
            var sortedByDate = (_displayContext.SortDirection == SortDirection.Ascending
                ? ordered.ThenBy(item => item.Date)
                : ordered.ThenByDescending(item => item.Date))
                .ThenBy(item => item.Item.Name, new EpisodeNameComparer()).Select(item => item.Item).ToArray();
            for (var index = 0; index < sortedByDate.Length; index++)
            {
                var oldIndex = BrowserItems.IndexOf(sortedByDate[index]);
                if (oldIndex != index) BrowserItems.Move(oldIndex, index);
            }
            return;
        }
        foreach (var stale in _sortItems.Keys.Except(current).ToArray()) _sortItems.Remove(stale);
        foreach (var item in current)
        {
            if (!_sortItems.ContainsKey(item))
            {
                var info = item.Kind == ResourceBrowserItemKind.VideoFile ? (FileSystemInfo)new FileInfo(item.Path) : new DirectoryInfo(item.Path);
                _sortItems[item] = new ListedItem
                {
                    PrimaryItemAttribute = item.Kind == ResourceBrowserItemKind.VideoFile ? StorageItemTypes.File : StorageItemTypes.Folder,
                    ItemPath = item.Path,
                    ItemType = item.Kind == ResourceBrowserItemKind.VideoFile ? Path.GetExtension(item.Path) : "文件夹",
                    ItemDateModifiedReal = info.LastWriteTimeUtc,
                    ItemDateCreatedReal = info.CreationTimeUtc,
                    FileSizeBytes = info is FileInfo file && file.Exists ? file.Length : 0,
                    FileTags = item.FileTags.Select(tag => tag.Name).ToArray(),
                };
            }
            _sortItems[item].ItemNameRaw = item.Name;
        }
        var byItem = current.ToDictionary(item => _sortItems[item]);
        var sorted = Files.App.Utils.Storage.SortingHelper.OrderFileList(current.Select(item => _sortItems[item]).ToList(),
            _displayContext.SortOption, _displayContext.SortDirection, _displayContext.SortDirectoriesAlongsideFiles, _displayContext.SortFilesFirst).ToArray();
        for (var index = 0; index < sorted.Length; index++)
        {
            var oldIndex = BrowserItems.IndexOf(byItem[sorted[index]]);
            if (oldIndex != index) BrowserItems.Move(oldIndex, index);
        }
    }

    private DateTimeOffset? GetAnimeAirDate(ResourceBrowserItemViewModel item)
    {
        var date = _workspace.GetVideoDetails(item.Path).AirDate;
        if (date is not null || item.Kind != ResourceBrowserItemKind.VideoFolder) return date;
        try
        {
            var folders = new[] { item.Path }.Concat(Directory.EnumerateDirectories(item.Path)
                .Where(folder => AnimeLibraryService.IsSeasonFolder(Path.GetFileName(folder))
                    && !File.GetAttributes(folder).HasFlag(System.IO.FileAttributes.ReparsePoint)));
            return folders.SelectMany(folder => Directory.EnumerateFiles(folder))
                .Where(file => _workspace.Settings.VideoExtensions.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                .Select(file => _workspace.GetVideoDetails(file).AirDate).Where(value => value is not null).Min();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private string GetLocationKey(ResourceBrowserLocation location) => $"{_animeLibrary}:{location.Kind}:{NormalizeForComparison(location.Path)}";

    private ScrollViewer? BrowserScroller => VideoDetailView.Visibility == Visibility.Visible
        ? VideoDetailView : DependencyObjectHelpers.FindChild<ScrollViewer>(BrowserGrid);

    private void RememberBrowserPosition()
    {
        if (_restoringPosition || _loadedLocationKey is null || BrowserScroller is not { } scroller) return;
        string? anchor = null;
        double anchorTop = 0;
        if (BrowserGrid.Visibility == Visibility.Visible)
        {
            for (var index = 0; index < BrowserItems.Count; index++)
            {
                if (BrowserGrid.ContainerFromIndex(index) is not FrameworkElement container) continue;
                var top = container.TransformToVisual(scroller).TransformPoint(new(0, 0)).Y;
                if (top + container.ActualHeight <= 0) continue;
                anchor = BrowserItems[index].Path;
                anchorTop = top;
                break;
            }
        }
        _viewStates[_loadedLocationKey] = new(scroller.VerticalOffset, anchor, anchorTop, _activeVideoItem?.Path, _currentAnimeSeasonPath);
    }

    private async Task RestoreBrowserPositionAsync(CancellationToken token)
    {
        if (_loadedLocationKey is null || !_viewStates.TryGetValue(_loadedLocationKey, out var state)) return;
        _restoringPosition = true;
        try
        {
            if (state.SelectedVideoPath is not null && BrowserItems.FirstOrDefault(item => PathEquals(item.Path, state.SelectedVideoPath)) is { } video)
                ActiveBrowserList.SelectedItem = video;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(30, token);
                token.ThrowIfCancellationRequested();
                UpdateLayout();
                if (BrowserScroller is not { } scroller) continue;
                scroller.ChangeView(null, Math.Min(state.Offset, scroller.ScrollableHeight), null, true);
            }
            if (state.AnchorPath is not null && BrowserGrid.Visibility == Visibility.Visible && BrowserScroller is { } gridScroller)
            {
                var index = BrowserItems.ToList().FindIndex(item => PathEquals(item.Path, state.AnchorPath));
                if (index >= 0)
                {
                    BrowserGrid.ScrollIntoView(BrowserItems[index]);
                    await Task.Delay(30, token);
                    UpdateLayout();
                    if (BrowserGrid.ContainerFromIndex(index) is FrameworkElement container)
                    {
                        var top = container.TransformToVisual(gridScroller).TransformPoint(new(0, 0)).Y;
                        gridScroller.ChangeView(null, Math.Clamp(gridScroller.VerticalOffset + top - state.AnchorTop, 0, gridScroller.ScrollableHeight), null, true);
                    }
                }
            }
        }
        finally { _restoringPosition = false; }
    }

    public async Task RefreshAsync()
    {
        if (_locations.Count > 0)
            await LoadLocationAsync(_locations[^1]);
    }

    public void SelectAllItems()
        => ActiveBrowserList.SelectAll();

    private ListViewBase ActiveBrowserList => _locations.Count > 0 && _locations[^1].Kind == ResourceBrowserLocationKind.VideoFolder
        ? (AnimeEpisodeButtons.Visibility == Visibility.Visible ? AnimeEpisodeButtons : AnimeEpisodeCards.Visibility == Visibility.Visible ? AnimeEpisodeCards : VideoFileList)
        : BrowserGrid;

    public async Task<bool> TryDeleteSelectedActorFoldersAsync(IReadOnlyList<ListedItem> selectedItems)
    {
        if (selectedItems.Count == 0 || selectedItems.Any(item => item is not ResourceActorListedItem))
            return false;

        var actorFolders = selectedItems
            .Cast<ResourceActorListedItem>()
            .Select(item => (Path: item.GetRequiredPath(), Name: item.Name ?? Path.GetFileName(item.GetRequiredPath())))
            .ToArray();
        await DeleteActorFoldersAsync(actorFolders);
        return true;
    }

    public bool CanDeleteSelectedActorFolders(IReadOnlyList<ListedItem> selectedItems)
    {
        if (selectedItems.Count == 0 ||
            selectedItems.Any(item => item is not ResourceActorListedItem) ||
            _locations.Count == 0 ||
            _locations[^1].Kind != ResourceBrowserLocationKind.LibraryRoot)
            return false;

        return selectedItems
            .Cast<ResourceActorListedItem>()
            .All(selected => BrowserItems.FirstOrDefault(item =>
                item.Kind == ResourceBrowserItemKind.ActorFolder && PathEquals(item.Path, selected.GetRequiredPath()))?.ActorWorkCount == 0);
    }

    private async Task DeleteActorFoldersAsync(IReadOnlyList<(string Path, string Name)> actorFolders)
    {
        if (_locations.Count == 0 || _locations[^1].Kind != ResourceBrowserLocationKind.LibraryRoot)
        {
            SetResourceStatusMessage("只能在演员列表中删除演员文件夹。");
            return;
        }

        foreach (var (path, name) in actorFolders)
        {
            if (!ResourceManagerPathScope.IsWithinLibrary(path, _libraryPath) ||
                !PathEquals(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty, _libraryPath) ||
                !Directory.Exists(path))
            {
                SetResourceStatusMessage($"无法确认“{name}”是当前资源库中的演员文件夹，未执行删除。");
                return;
            }

            var workCount = await CountActorVideosAsync(path);
            if (workCount != 0)
            {
                SetResourceStatusMessage($"“{name}”仍包含 {workCount} 部作品，仅支持删除无作品的演员文件夹。");
                return;
            }
        }

        var confirmation = new ContentDialog
        {
            Title = "删除无作品的演员文件夹",
            Content = actorFolders.Count == 1
                ? $"确认将“{actorFolders[0].Name}”移至回收站？该文件夹中没有视频作品。"
                : $"确认将这 {actorFolders.Count} 个无视频作品的演员文件夹移至回收站？",
            PrimaryButtonText = "移至回收站",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await confirmation.TryShowAsync() != ContentDialogResult.Primary)
            return;

        // Recheck after confirmation so newly added videos cannot be deleted by a stale selection.
        foreach (var (path, name) in actorFolders)
        {
            if (!Directory.Exists(path) || await CountActorVideosAsync(path) != 0)
            {
                SetResourceStatusMessage($"“{name}”已包含作品或文件夹已不存在，已取消删除。");
                return;
            }
        }

        if (_contentPageContext.ShellPage is not { } shellPage)
            return;

        var storageItems = actorFolders.Select(folder =>
            StorageHelpers.FromPathAndType(folder.Path, FilesystemItemType.Directory));
        await shellPage.FilesystemHelpers.DeleteItemsAsync(storageItems, DeleteConfirmationPolicies.Never, permanently: false, registerHistory: true);
        await RefreshAsync();
        SetResourceStatusMessage(actorFolders.Count == 1
            ? $"已将“{actorFolders[0].Name}”移至回收站。"
            : $"已将 {actorFolders.Count} 个演员文件夹移至回收站。");
    }

    public async Task RenameSelectedItemAsync()
    {
        if (ActiveBrowserList.SelectedItems.Count != 1 ||
            ActiveBrowserList.SelectedItems.OfType<ResourceBrowserItemViewModel>().FirstOrDefault() is not { } viewModel)
            return;

        await RenameResourceItemAsync(viewModel);
    }

    private async Task RenameResourceItemAsync(ResourceBrowserItemViewModel viewModel)
    {
        if (_contentPageContext.ShellPage is not { } shellPage)
            return;

        var item = CreateListedItem(viewModel);
        var nameBox = new TextBox { Text = item.Name ?? string.Empty, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            Title = "重命名",
            Content = nameBox,
            PrimaryButtonText = "重命名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
            return;

        var oldPath = item.GetRequiredPath();
        var oldRawName = item.ItemNameRaw ?? Path.GetFileName(oldPath);
        var newRawName = string.IsNullOrEmpty(item.Name)
            ? string.Concat(nameBox.Text.Trim(), item.FileExtension)
            : oldRawName.Replace(item.Name, nameBox.Text.Trim(), StringComparison.Ordinal);
        if (!await UIFilesystemHelpers.RenameFileItemAsync(item, nameBox.Text.Trim(), shellPage, showExtensionDialog: false))
            return;

        var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? string.Empty, newRawName);
        _workspace.RemapItemPaths(oldPath, newPath);
        await RefreshAsync();
    }

    public void NavigateToParentLocation()
    {
        if (_locations.Count > 1)
            NavigateToLocation(_locations.Take(_locations.Count - 1).ToArray());
    }

    private async Task LoadLocationAsync(ResourceBrowserLocation location)
    {
        RememberBrowserPosition();
        _loadedLocationKey = null;
        var preferredActorGroupName = location.Kind == ResourceBrowserLocationKind.ActorFolder &&
            _selectedActorGroupActorPath is not null && PathEquals(_selectedActorGroupActorPath, location.Path)
            ? _selectedActorGroupName
            : null;
        CancellationTokenSource? currentLoad = null;
        CancellationToken cancellationToken = default;
        try
        {
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            currentLoad = _loadCancellation = new CancellationTokenSource();
            cancellationToken = currentLoad.Token;

            ClearSelectedResourceItems();
            SetNativeSelection(null);
            BrowserGrid.SelectedItems.Clear();
            LoadingRing.IsActive = true;
            var animeHome = _animeLibrary && location.Kind == ResourceBrowserLocationKind.LibraryRoot;
            AnimeHomeHeader.Visibility = animeHome ? Visibility.Visible : Visibility.Collapsed;
            BrowserGrid.Width = double.NaN;
            BrowserGrid.MaxWidth = animeHome ? 1080 : double.PositiveInfinity;
            BrowserGrid.HorizontalAlignment = animeHome ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            BrowserGrid.Padding = animeHome ? new Thickness(12, 8, 12, 24) : new Thickness(12);
            _animeEditingPath = null;
            VideoDetailTitle.Visibility = Visibility.Visible;
            SetAnimeEditing(false);

            if (_pendingAnimeEditPath is not null && !PathEquals(_pendingAnimeEditPath, location.Path)) _pendingAnimeEditPath = null;
            BrowserItems.Clear();
            VideoFileList.SelectedItems.Clear();
            AnimeEpisodeButtons.SelectedItems.Clear();
            AnimeEpisodeButtons.Visibility = Visibility.Collapsed;
            AnimeEpisodeCards.SelectedItems.Clear();
            AnimeEpisodeCards.Visibility = Visibility.Collapsed;
            VideoFileList.Visibility = _animeLibrary ? Visibility.Collapsed : Visibility.Visible;
            _activeVideoItem = null;
            VideoDetailView.Visibility = !_animeLibrary && location.Kind == ResourceBrowserLocationKind.VideoFolder ? Visibility.Visible : Visibility.Collapsed;
            BrowserGrid.Visibility = _animeLibrary || location.Kind == ResourceBrowserLocationKind.VideoFolder ? Visibility.Collapsed : Visibility.Visible;
            _animeIllustrationCache.Clear();
            EmptyText.Visibility = Visibility.Collapsed;
            ActorGroupButtons.Children.Clear();
            ActorGroupSelector.Visibility = Visibility.Collapsed;
            _actorVideoGroups.Clear();
            SetResourceStatusMessage("正在加载资源……");

            var items = await _browser.GetChildrenAsync(location.Path, location.Kind, _workspace.Settings, cancellationToken);
            if (!_animeLibrary && location.Kind == ResourceBrowserLocationKind.ActorFolder)
            {
                _selectedActorGroupActorPath = location.Path;
                await BuildActorVideoGroupsAsync(location, items, cancellationToken);
                if (_actorVideoGroups.Count > 0)
                {
                    _selectedActorGroupIndex = preferredActorGroupName is null
                        ? 0
                        : Math.Max(0, _actorVideoGroups.FindIndex(group => string.Equals(group.Name, preferredActorGroupName, StringComparison.OrdinalIgnoreCase)));
                    _selectedActorGroupName = _actorVideoGroups[_selectedActorGroupIndex].Name;
                    ActorGroupSelector.Visibility = Visibility.Visible;
                    BuildActorGroupButtons();
                    items = _actorVideoGroups[_selectedActorGroupIndex].Items;
                }
            }

            var initialGridLayout = items.Any(item => item.Kind is ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder)
                ? CalculateActorGridLayout()
                : null;
            if (initialGridLayout is { } layout && BrowserGrid.ItemsPanelRoot is ItemsWrapGrid initialItemsPanel)
                initialItemsPanel.ItemWidth = layout.ItemWidth;

            var posters = await Task.WhenAll(items.Select(item => LoadPosterAsync(item.PosterPath, cancellationToken)));
            var posterIndex = 0;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var viewModel = new ResourceBrowserItemViewModel(item, _workspace, _animeLibrary);
                if (initialGridLayout is { } initialLayout &&
                    item.Kind is ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder)
                    viewModel.SetAdaptiveCardWidth(initialLayout.CardWidth);

                viewModel.Poster = posters[posterIndex++];
                if (item.Kind == ResourceBrowserItemKind.VideoFolder)
                {
                    try
                    {
                        viewModel.UpdateFileTags(FileTagsHelper.ReadFileTag(item.Path));
                    }
                    catch
                    {
                        viewModel.UpdateFileTags([]);
                    }
                }
                BrowserItems.Add(viewModel);
            }

            RestoreBrowserSorting();
            ApplyBrowserSorting();
            EmptyText.Visibility = BrowserItems.Count == 0 && location.Kind != ResourceBrowserLocationKind.VideoFolder ? Visibility.Visible : Visibility.Collapsed;
            if (location.Kind == ResourceBrowserLocationKind.VideoFolder)
            {
                await PrepareVideoDetailFolderAsync(location, cancellationToken);
                if (!_animeLibrary && BrowserItems.Count > 0)
                    VideoFileList.SelectedItem = BrowserItems[0];
                else { _activeVideoItem = null; ShowActiveVideoDetails(); }
                if (_animeLibrary) await BuildAnimeSeasonButtonsAsync(location, cancellationToken);
                else await LoadVideoIllustrationsAsync(location.Path, cancellationToken);
            }
            if (_animeLibrary)
            {
                VideoDetailView.Visibility = location.Kind == ResourceBrowserLocationKind.VideoFolder ? Visibility.Visible : Visibility.Collapsed;
                BrowserGrid.Visibility = location.Kind == ResourceBrowserLocationKind.VideoFolder ? Visibility.Collapsed : Visibility.Visible;
            }
            SetResourceStatusMessage(string.Empty);
            UpdateActorGridLayout();
            UpdateNativeResourceStatus();
            _loadedLocationKey = GetLocationKey(location);
            await RestoreBrowserPositionAsync(cancellationToken);

            if (!_animeLibrary && location.Kind == ResourceBrowserLocationKind.LibraryRoot && BrowserItems.Count > 0)
                _ = LoadActorWorkCountsAsync(BrowserItems.ToArray(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (ReferenceEquals(currentLoad, _loadCancellation))
                SetResourceStatusMessage("已停止加载。");
        }
        catch (Exception ex)
        {
            App.Logger.LogError(ex, "Unable to load resource library location {LocationPath}", location.Path);
            EmptyText.Visibility = Visibility.Visible;
            UpdateNativeResourceStatus($"资源加载失败：{ex.Message}");
        }
        finally
        {
            if (currentLoad is null || ReferenceEquals(currentLoad, _loadCancellation))
                LoadingRing.IsActive = false;
        }
    }

    private void BrowserViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateActorGridLayout();

    private void BrowserGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateActorGridLayout();

    private void BrowserGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewBase list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != list; source = VisualTreeHelper.GetParent(source))
        {
            if (source is GridViewItem or ListViewItem)
                return;
        }
        _selectionDragOrigin = e.GetCurrentPoint(BrowserViewport).Position;
        list.CapturePointer(e.Pointer);
    }

    private void BrowserGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_selectionDragOrigin is not { } origin || sender is not ListViewBase list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;
        var current = e.GetCurrentPoint(BrowserViewport).Position;
        var left = Math.Min(origin.X, current.X);
        var top = Math.Min(origin.Y, current.Y);
        var width = Math.Abs(origin.X - current.X);
        var height = Math.Abs(origin.Y - current.Y);
        if (width < 4 && height < 4)
            return;
        SelectionMarquee.Visibility = Visibility.Visible;
        SelectionMarquee.Margin = new Thickness(left, top, 0, 0);
        SelectionMarquee.Width = width;
        SelectionMarquee.Height = height;
    }

    private void BrowserGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_selectionDragOrigin is not { } origin)
            return;
        if (sender is not ListViewBase list)
            return;
        var current = e.GetCurrentPoint(BrowserViewport).Position;
        var left = Math.Min(origin.X, current.X);
        var top = Math.Min(origin.Y, current.Y);
        var right = Math.Max(origin.X, current.X);
        var bottom = Math.Max(origin.Y, current.Y);
        var wasDragging = SelectionMarquee.Visibility == Visibility.Visible;
        _selectionDragOrigin = null;
        SelectionMarquee.Visibility = Visibility.Collapsed;
        list.ReleasePointerCapture(e.Pointer);
        if (!wasDragging)
            return;
        list.SelectedItems.Clear();
        for (var index = 0; index < BrowserItems.Count; index++)
        {
            if (list.ContainerFromIndex(index) is not FrameworkElement container)
                continue;
            var point = container.TransformToVisual(BrowserViewport).TransformPoint(new Windows.Foundation.Point());
            if (point.X < right && point.X + container.ActualWidth > left && point.Y < bottom && point.Y + container.ActualHeight > top)
                list.SelectedItems.Add(BrowserItems[index]);
        }
        e.Handled = true;
    }

    private void UpdateActorGridLayout()
    {
        if (BrowserGrid.ItemsPanelRoot is not ItemsWrapGrid itemsPanel)
            return;

        if (_animeLibrary && _locations.Count > 0 && _locations[^1].Kind == ResourceBrowserLocationKind.LibraryRoot)
        {
            var homeWidth = Math.Min(1080, BrowserViewport.ActualWidth);
            if (homeWidth <= 0) return;
            BrowserGrid.Width = homeWidth;
            AnimeHomeHeader.Width = Math.Max(0, homeWidth - 48);
            var availableWidth = homeWidth - BrowserGrid.Padding.Left - BrowserGrid.Padding.Right;
            if (availableWidth <= 0) return;
            var columns = availableWidth >= 620 ? 2 : 1;
            itemsPanel.ItemWidth = availableWidth / columns;
            foreach (var item in BrowserItems.Where(item => item.Kind == ResourceBrowserItemKind.CategoryFolder))
                item.SetAdaptiveCardWidth(Math.Max(160, itemsPanel.ItemWidth - 26));
            return;
        }

        const double defaultItemWidth = 276;
        var hasAdaptiveCards = BrowserItems.Any(item => item.Kind is ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder);
        if (!hasAdaptiveCards)
        {
            itemsPanel.ItemWidth = defaultItemWidth;
            return;
        }

        if (CalculateActorGridLayout() is not { } layout)
            return;

        itemsPanel.ItemWidth = layout.ItemWidth;
        foreach (var item in BrowserItems.Where(item => item.Kind is ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder))
            item.SetAdaptiveCardWidth(layout.CardWidth);
    }

    private (double ItemWidth, double CardWidth)? CalculateActorGridLayout()
    {
        // Use the outer viewport's stable width. The GridView's own width can change
        // when its vertical scrollbar appears, which used to resize every poster a
        // moment after the initial layout.
        var viewportWidth = BrowserGrid.Parent is FrameworkElement viewport && viewport.ActualWidth > 0
            ? viewport.ActualWidth
            : BrowserGrid.ActualWidth;
        var availableWidth = viewportWidth - BrowserGrid.Padding.Left - BrowserGrid.Padding.Right;
        if (availableWidth <= 0)
            return null;

        var minimumCardWidth = _animeLibrary ? _workspace.Settings.AnimePosterWidth : 250d;
        const double itemHorizontalChrome = 26;
        var maximumCardWidth = _animeLibrary ? _workspace.Settings.AnimePosterWidth + 20d : 340d;
        var minimumItemWidth = minimumCardWidth + itemHorizontalChrome;
        var columnCount = Math.Max(1, (int)Math.Floor(availableWidth / minimumItemWidth));
        var itemWidth = availableWidth / columnCount;
        var cardWidth = Math.Clamp(itemWidth - itemHorizontalChrome, _animeLibrary ? _workspace.Settings.AnimePosterWidth - 20 : 180, maximumCardWidth);
        return (itemWidth, cardWidth);
    }

    private async Task<BitmapImage?> LoadPosterAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var modified = File.GetLastWriteTimeUtc(path);
        if (!_posterCache.TryGetValue(path, out var cached) || cached.Modified != modified)
        {
            if (_posterCache.Count >= 400) _posterCache.Clear();
            cached = (modified, LoadPosterCoreAsync(path));
            _posterCache[path] = cached;
        }
        var image = await cached.Image.WaitAsync(cancellationToken);
        if (image is null) _posterCache.Remove(path);
        return image;
    }

    private async Task<BitmapImage?> LoadPosterCoreAsync(string path)
    {
        await _posterLoads.WaitAsync();
        try { return await DecodePosterAsync(path); }
        finally { _posterLoads.Release(); }
    }

    private static async Task<BitmapImage?> DecodePosterAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var image = new BitmapImage { DecodePixelWidth = 640 };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void ResourceVideos_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<ResourceBrowserItemViewModel>().Where(item => item.Kind is ResourceBrowserItemKind.VideoFile or ResourceBrowserItemKind.VideoFolder).ToArray();
        if (items.Length == 0)
        {
            e.Cancel = true;
            return;
        }
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (item.Kind == ResourceBrowserItemKind.VideoFile)
                        paths.Add(item.Path);
                    else if (item.Kind == ResourceBrowserItemKind.VideoFolder)
                    {
                        var children = await _browser.GetChildrenAsync(item.Path, ResourceBrowserLocationKind.VideoFolder, _workspace.Settings, CancellationToken.None);
                        foreach (var child in children.Where(child => child.Kind == ResourceBrowserItemKind.VideoFile))
                            paths.Add(child.Path);
                    }
                }
                var files = new List<StorageFile>();
                foreach (var path in paths)
                    files.Add(await StorageFile.GetFileFromPathAsync(path));
                request.SetData(files.Cast<IStorageItem>().ToArray());
            }
            catch (Exception ex)
            {
                App.Logger.LogWarning(ex, "Could not prepare resource videos for drag and drop");
            }
            finally { deferral.Complete(); }
            });
    }

    private void OnBrowserSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_locations.Count > 0 && _locations[^1].Kind == ResourceBrowserLocationKind.VideoFolder)
            return;
        UpdateResourceSelection(BrowserGrid.SelectedItems.OfType<ResourceBrowserItemViewModel>());
    }

    private void VideoFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_locations.Count == 0 || _locations[^1].Kind != ResourceBrowserLocationKind.VideoFolder)
            return;
        var list = (ListViewBase)sender;
        _animeEditingPath = null;
        VideoDetailTitle.Visibility = Visibility.Visible;
        SetAnimeEditing(false);

        _activeVideoItem = list.SelectedItem as ResourceBrowserItemViewModel;
        ShowActiveVideoDetails();
        if (_animeLibrary) UpdateAnimeIllustrations();
        UpdateResourceSelection(list.SelectedItems.OfType<ResourceBrowserItemViewModel>());
    }

    private void UpdateResourceSelection(IEnumerable<ResourceBrowserItemViewModel> selection)
    {
        ClearSelectedResourceItems();
        var selectedItems = new List<ListedItem>();
        foreach (var viewModel in selection)
        {
            var listedItem = CreateListedItem(viewModel);
            _selectedResourceItems[listedItem.ItemPath ?? viewModel.Path] = (listedItem, viewModel);
            selectedItems.Add(listedItem);
        }

        SetNativeSelection(selectedItems.Count == 0 ? null : selectedItems);
    }

    private void ShowActiveVideoDetails()
    {
        var item = _activeVideoItem;
        var hasVideo = item is not null && !item.Model.IsPosterOnly && File.Exists(item.Path);
        VideoDetailPlayButton.Content = (hasVideo ? Strings.ResourceVideoPlay : Strings.ResourceVideoNoResource).GetLocalizedResource();
        VideoDetailPlayButton.IsEnabled = hasVideo;
        VideoDetailModified.Visibility = _animeLibrary ? Visibility.Collapsed : Visibility.Visible;
        VideoDetailPath.Visibility = _animeLibrary ? Visibility.Collapsed : Visibility.Visible;
        VideoDetailPoster.Source = item?.Poster ?? _detailFolderItem?.Poster;
        VideoDetailTitle.Text = _detailFolderItem?.Name ?? (_locations.Count > 0 ? _locations[^1].Title : string.Empty);
        UpdateDetailTitleAction();
        UpdateAnimeMetadata();
        VideoDetailFileName.Text = item?.Name ?? Strings.ResourceVideoNoFiles.GetLocalizedResource();
        VideoDetailPath.Text = item?.Path ?? _detailFolderItem?.Path ?? string.Empty;
        if (item is null || item.Model.IsPosterOnly)
        {
            if (item?.Model.IsPosterOnly == true) VideoDetailFileName.Text = Strings.ResourceVideoNoFiles.GetLocalizedResource();
            VideoDetailFileSize.Text = string.Empty;
            VideoDetailModified.Text = string.Empty;
            return;
        }
        try
        {
            var info = new FileInfo(item.Path);
            VideoDetailFileSize.Text = string.Format(CultureInfo.CurrentCulture, Strings.ResourceVideoFileSize.GetLocalizedResource(), info.Length.ToSizeString());
            VideoDetailModified.Text = string.Format(CultureInfo.CurrentCulture, Strings.ResourceVideoModified.GetLocalizedResource(), info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture));
        }
        catch (IOException)
        {
            VideoDetailFileSize.Text = string.Format(CultureInfo.CurrentCulture, Strings.ResourceVideoFileSize.GetLocalizedResource(), Strings.Unknown.GetLocalizedResource());
            VideoDetailModified.Text = string.Empty;
        }
    }

    private string? AnimeMetadataPath => _activeVideoItem?.Path ?? _detailFolderItem?.Path;

    private void UpdateAnimeMetadata()
    {
        AnimeDetailMetadata.Visibility = _animeLibrary ? Visibility.Visible : Visibility.Collapsed;
        VideoDetailLayout.DataContext = _detailFolderItem;
        AnimeDetailMetadata.DataContext = _detailFolderItem;
        if (!_animeLibrary || _detailFolderItem is null) return;
        var details = _workspace.GetVideoDetails(AnimeMetadataPath!);
        AnimeSynopsis.Text = string.IsNullOrWhiteSpace(details.Synopsis) ? Strings.AnimeLibraryNoSynopsis.GetLocalizedResource() : details.Synopsis;
        AnimeAirDate.Text = details.AirDate?.ToString("Y", CultureInfo.CurrentCulture) ?? "—";
        if (_animeEditingPath is null)
        {
            AnimeSynopsisEditor.Text = details.Synopsis;
            AnimeMonthEditor.Text = details.AirDate?.ToString("yyyy-MM", CultureInfo.InvariantCulture) ?? string.Empty;
        }
        if (_pendingAnimeEditPath is not null && PathEquals(_pendingAnimeEditPath, _detailFolderItem.Path) && (BrowserItems.Any(item => item.Kind == ResourceBrowserItemKind.VideoFile) || BrowserItems.Count == 0))
        {
            _pendingAnimeEditPath = null;
            BeginAnimeEdit(applyToAllVideos: true);
        }
    }

    private Task EditAnimeDetailsAsync(ResourceBrowserItemViewModel item)
    {
        if (_detailFolderItem is not null && PathEquals(_detailFolderItem.Path, item.Path) && VideoDetailView.Visibility == Visibility.Visible)
            BeginAnimeEdit(applyToAllVideos: true);
        else
        {
            _pendingAnimeEditPath = item.Path;
            NavigateToLocation(_locations.Append(new ResourceBrowserLocation(item.Path, ResourceBrowserLocationKind.VideoFolder, item.Name)).ToArray());
        }
        return Task.CompletedTask;
    }

    private void SetAnimeEditing(bool editing)
    {
        AnimeMonthEditor.Visibility = AnimeSynopsisEditor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        AnimeAirDate.Visibility = AnimeSynopsisFrame.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        AnimeDetailCancelButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        AnimeDetailEditButton.Content = (editing ? Strings.AnimeLibrarySave : Strings.AnimeLibraryEditDetails).GetLocalizedResource();
        AnimeDetailEditButton.Style = editing ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
    }

    private void BeginAnimeEdit(bool applyToAllVideos = false, ResourceBrowserItemViewModel? clickedVideo = null)
    {
        if (AnimeMetadataPath is null) return;
        _animeEditingPath = clickedVideo?.Path ?? AnimeMetadataPath;
        IEnumerable<ResourceBrowserItemViewModel> videos = clickedVideo is not null ? [clickedVideo]
            : applyToAllVideos ? BrowserItems : ActiveBrowserList.SelectedItems.OfType<ResourceBrowserItemViewModel>();
        _animeEditingPaths = videos.Where(item => item.Kind == ResourceBrowserItemKind.VideoFile)
            .Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (applyToAllVideos && _detailFolderItem is not null)
            _animeEditingPaths = _animeEditingPaths.Append(_detailFolderItem.Path).ToArray();
        else if (_animeEditingPaths.Length == 0)
            _animeEditingPaths = [_animeEditingPath];
        var details = _workspace.GetVideoDetails(_animeEditingPath);
        AnimeSynopsisEditor.Text = details.Synopsis;
        AnimeMonthEditor.Text = details.AirDate?.ToString("yyyy-MM", CultureInfo.InvariantCulture) ?? string.Empty;
        AnimeEditError.Visibility = Visibility.Collapsed;
        SetAnimeEditing(true);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_animeEditingPath is null) return;
            AnimeMonthEditor.Focus(FocusState.Programmatic);
            AnimeMonthEditor.SelectAll();
        });
    }

    private static bool TryParseAnimeMonth(string text, out DateTime month)
    {
        text = text.Trim();
        if (text.Length == 4 && text.All(char.IsAsciiDigit)) text = "20" + text;
        return DateTime.TryParseExact(text, new[] { "yyyy-MM", "yyyyMM" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
    }

    private void AnimeMonthEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseAnimeMonth(AnimeMonthEditor.Text, out var month))
            AnimeMonthEditor.Text = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    private void AnimeMonthEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = AnimeMonthEditor.Text.Trim();
        if (text.Length == 6 && DateTime.TryParseExact(text, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            AnimeMonthEditor.Text = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            AnimeMonthEditor.SelectionStart = AnimeMonthEditor.Text.Length;
        }
        AnimeEditError.Visibility = Visibility.Collapsed;
    }

    private void AnimeMetadataSave_Click(object sender, RoutedEventArgs e)
    {
        if (_animeEditingPath is null) return;
        DateTimeOffset? month = null;
        if (!string.IsNullOrWhiteSpace(AnimeMonthEditor.Text))
        {
            if (!TryParseAnimeMonth(AnimeMonthEditor.Text, out var date))
            { AnimeEditError.Visibility = Visibility.Visible; return; }
            month = new DateTimeOffset(date);
        }
        var details = new ResourceVideoDetails { Synopsis = AnimeSynopsisEditor.Text, AirDate = month };
        foreach (var path in _animeEditingPaths)
            _workspace.SetVideoDetails(path, details);
        _animeEditingPaths = [];
        _animeEditingPath = null;
        UpdateResourceSelection(ActiveBrowserList.SelectedItems.OfType<ResourceBrowserItemViewModel>());
        VideoDetailTitle.Visibility = Visibility.Visible;
        SetAnimeEditing(false);

        UpdateAnimeMetadata();
    }

    private void AnimeMetadataCancel_Click(object sender, RoutedEventArgs e)
    {
        _animeEditingPath = null;
        VideoDetailTitle.Visibility = Visibility.Visible;
        SetAnimeEditing(false);
        UpdateAnimeMetadata();
    }

    private void AnimeEditor_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_animeEditingPath is null || e.Key != Windows.System.VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        AnimeMetadataSave_Click(sender, e);
    }

    private void AnimeDetailEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_animeEditingPath is null) BeginAnimeEdit();
        else AnimeMetadataSave_Click(sender, e);
    }

    private void VideoDetailPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_activeVideoItem is null || _activeVideoItem.Model.IsPosterOnly)
            return;
        try { Process.Start(new ProcessStartInfo { FileName = _activeVideoItem.Path, UseShellExecute = true }); }
        catch (Exception ex) { SetResourceStatusMessage($"无法打开视频：{ex.Message}"); }
    }

    private async Task PrepareVideoDetailFolderAsync(ResourceBrowserLocation location, CancellationToken token)
    {
        _detailFolderItem = null;
        _detailFolderListedItem = null;
        ResourceBrowserItem? model = null;
        if (_locations.Count > 1)
        {
            var parent = _locations[^2];
            var children = await _browser.GetChildrenAsync(parent.Path, parent.Kind, _workspace.Settings, token);
            model = children.FirstOrDefault(child => string.Equals(child.Path, location.Path, StringComparison.OrdinalIgnoreCase));
        }
        model ??= new ResourceBrowserItem { Name = Path.GetFileName(location.Path), Path = location.Path, Kind = ResourceBrowserItemKind.VideoFolder };
        var item = new ResourceBrowserItemViewModel(model, _workspace, _animeLibrary);
        var listed = CreateListedItem(item) as ResourceVideoFolderListedItem;
        if (!string.IsNullOrWhiteSpace(model.PosterPath))
            item.Poster = await LoadPosterAsync(model.PosterPath, token);
        token.ThrowIfCancellationRequested();
        _detailFolderItem = item;
        _detailFolderListedItem = listed;
        ShowActiveVideoDetails();
    }

    private async void AnimeRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _workspace.SetLibraryPath(folder.Path);
        _libraryPath = folder.Path;
        AnimeRootPath.Text = folder.Path;
        NavigateToLocation([new ResourceBrowserLocation(folder.Path, ResourceBrowserLocationKind.LibraryRoot, folder.Path)]);
    }

    private async Task BuildAnimeSeasonButtonsAsync(ResourceBrowserLocation location, CancellationToken token)
    {
        var seasons = await AnimeLibraryService.GetSeasonsAsync(location.Path, _workspace.Settings, token);
        if (seasons.Count == 0) return;
        ActorGroupButtons.Children.Clear();
        ActorGroupSelector.Visibility = !_workspace.Settings.AnimeFlattenSeasons && (seasons.Count > 1 || !PathEquals(seasons[0], location.Path)) ? Visibility.Visible : Visibility.Collapsed;
        async Task SelectSeasonAsync(string path)
        {
            foreach (var button in ActorGroupButtons.Children.OfType<Button>()) button.Style = PathEquals((string)button.Tag, path) ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
            var selection = ++_animeSeasonSelection;
            var episodes = await _browser.GetChildrenAsync(path, ResourceBrowserLocationKind.VideoFolder, _workspace.Settings, token);
            if (selection != _animeSeasonSelection) return;
            token.ThrowIfCancellationRequested();
            var models = new List<ResourceBrowserItemViewModel>();
            var posterCache = new Dictionary<string, BitmapImage?>(StringComparer.OrdinalIgnoreCase);
            foreach (var episode in episodes)
            {
                var model = new ResourceBrowserItemViewModel(episode, _workspace, true);
                model.EpisodeIndex = models.Count + 1;
                var poster = AnimeLibraryService.ResolveEpisodePoster(episode.Path, path, location.Path, _workspace.Settings, _detailFolderItem?.Model.PosterPath);
                if (!string.IsNullOrWhiteSpace(poster))
                {
                    if (!posterCache.TryGetValue(poster, out var image))
                    {
                        image = await LoadPosterAsync(poster, token);
                        posterCache[poster] = image;
                    }
                    model.Poster = image;
                }
                models.Add(model);
            }
            if (selection != _animeSeasonSelection) return;
            token.ThrowIfCancellationRequested();
            ClearSelectedResourceItems(); SetNativeSelection(null);
            BrowserItems.Clear();
            AnimeEpisodeCards.Visibility = episodes.Count > 0 && episodes.Count <= _workspace.Settings.AnimePosterEpisodeLimit ? Visibility.Visible : Visibility.Collapsed;
            AnimeEpisodeButtons.Visibility = episodes.Count > _workspace.Settings.AnimePosterEpisodeLimit ? Visibility.Visible : Visibility.Collapsed;
            VideoFileList.Visibility = Visibility.Collapsed;
            foreach (var model in models) BrowserItems.Add(model);
            ApplyBrowserSorting();
            _currentAnimeSeasonPath = path;
            ActiveBrowserList.SelectedItem = BrowserItems.FirstOrDefault();
            _activeVideoItem = ActiveBrowserList.SelectedItem as ResourceBrowserItemViewModel;
            ShowActiveVideoDetails(); UpdateNativeResourceStatus();
            await LoadVideoIllustrationsAsync(path, token);
        }
        foreach (var season in seasons)
        {
            var path = season;
            var button = new Button { Content = PathEquals(path, location.Path) ? Strings.AnimeLibraryEpisodes.GetLocalizedResource() : Path.GetRelativePath(location.Path, path), Tag = path, Padding = new Thickness(12, 6, 12, 6) };
            button.Click += async (_, _) => { try { await SelectSeasonAsync(path); } catch (OperationCanceledException) { } catch (Exception ex) { SetResourceStatusMessage(ex.Message); } };
            ActorGroupButtons.Children.Add(button);
        }
        var preferredSeason = _viewStates.TryGetValue(GetLocationKey(location), out var state) ? state.SeasonPath : null;
        var restoredSeason = preferredSeason is null ? null : seasons.FirstOrDefault(path => PathEquals(path, preferredSeason));
        await SelectSeasonAsync(restoredSeason ?? seasons[0]);
    }

    private void VideoDetailLayout_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAnimePosterLayout();

    private void UpdateAnimePosterLayout()
    {
        VideoInfoColumn.Width = _animeLibrary ? new GridLength(0.65, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(VideoFileInfoPanel, _animeLibrary ? 2 : 1);
        Grid.SetRow(VideoFileInfoPanel, _animeLibrary ? 0 : 1);
        VideoFileInfoPanel.Margin = _animeLibrary ? new Thickness(0) : new Thickness(0, 20, 0, 0);
        AnimeDetailEditButton.Visibility = _animeLibrary ? Visibility.Visible : Visibility.Collapsed;
        VideoDetailPoster.Stretch = _animeLibrary ? Stretch.UniformToFill : Stretch.Uniform;
        VideoDetailInfoPanel.Height = _animeLibrary ? (VideoDetailLayout.ActualWidth is > 0 and < 700 ? 280 : 380) : double.NaN;
        if (!_animeLibrary) return;
        var width = VideoDetailInfoPanel.Height * 2 / 3;
        VideoPosterColumn.Width = GridLength.Auto;
        VideoDetailLayout.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        VideoPosterFrame.Width = width;
        VideoPosterFrame.Height = VideoDetailInfoPanel.Height;
        VideoPosterFrame.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private void UpdateDetailTitleAction()
    {
        DetailTitleAction.Content = (_detailFolderListedItem?.IsTranslatedTitleShown == true ? Strings.ResourceDetailOriginal :
            _detailFolderListedItem?.HasTranslatedTitle == true ? Strings.ResourceDetailTranslation : Strings.ResourceDetailTranslate).GetLocalizedResource();
    }

    private async void DetailTitleAction_Click(object sender, RoutedEventArgs e)
    {
        if (_detailFolderItem is not { } item || _detailFolderListedItem is not { } listed) return;
        DetailTitleAction.IsEnabled = false;
        try { await ToggleVideoFolderTitleAsync(item, listed); VideoDetailTitle.Text = item.Name; UpdateDetailTitleAction(); }
        finally { DetailTitleAction.IsEnabled = true; }
    }

    private async void Illustration_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BitmapImage selected } || _illustrationImages.Count == 0) return;
        e.Handled = true;
        var images = _illustrationImages.ToArray();
        var paths = images.Select(item => _illustrationPaths.GetValueOrDefault(item)).ToArray();
        var index = Array.IndexOf(images, selected);
        if (index < 0) return;
        var image = new Image { Stretch = Stretch.Uniform };
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var previous = ResourceDialogPresentation.CreateIconButton("\uE76B", Strings.ResourcePreviousIllustration.GetLocalizedResource());
        var next = ResourceDialogPresentation.CreateIconButton("\uE76C", Strings.ResourceNextIllustration.GetLocalizedResource());
        previous.VerticalAlignment = next.VerticalAlignment = VerticalAlignment.Center;
        previous.HorizontalAlignment = HorizontalAlignment.Left;
        next.HorizontalAlignment = HorizontalAlignment.Right;
        previous.Margin = next.Margin = new Thickness(12);
        previous.IsEnabled = next.IsEnabled = images.Length > 1;
        var requestVersion = 0;
        async Task ShowAsync(int step)
        {
            index = (index + step + images.Length) % images.Length;
            var requestedIndex = index;
            var version = ++requestVersion;
            count.Text = $"{index + 1} / {images.Length}";
            image.Source = images[index];
            if (paths[index] is not { } path) return;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var fullImage = new BitmapImage { DecodePixelWidth = (int)(Math.Min(1200, XamlRoot.Size.Width - 80) * XamlRoot.RasterizationScale) };
                await fullImage.SetSourceAsync(stream);
                if (version == requestVersion && index == requestedIndex) image.Source = fullImage;
            }
            catch (Exception ex) { App.Logger.LogWarning(ex, "Unable to preview illustration {Path}", path); }
        }
        previous.Click += async (_, _) => await ShowAsync(-1);
        next.Click += async (_, _) => await ShowAsync(1);
        var close = ResourceDialogPresentation.CreateIconButton("\uE8BB", Strings.Close.GetLocalizedResource());
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 0, 8, 0);
        var content = new Grid { RowSpacing = 12 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid(); header.Children.Add(close);
        var picture = new Grid(); picture.Children.Add(image); picture.Children.Add(previous); picture.Children.Add(next);
        Grid.SetRow(picture, 1); content.Children.Add(header); content.Children.Add(picture);
        Grid.SetRow(count, 2); content.Children.Add(count);
        var dialog = ResourceDialogPresentation.Create(XamlRoot, content);
        close.Click += (_, _) => dialog.Hide();
        dialog.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Left) { args.Handled = true; await ShowAsync(-1); }
            else if (args.Key == Windows.System.VirtualKey.Right) { args.Handled = true; await ShowAsync(1); }
        }), true);
        await ShowAsync(0);
        await dialog.ShowAsync();
    }

    private async Task LoadVideoIllustrationsAsync(string folderPath, CancellationToken token)
    {
        var images = new List<BitmapImage>();
        var imagePaths = new Dictionary<BitmapImage, string>();
        try
        {
            var extensions = _workspace.Settings.ImageExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var posterPaths = BrowserItems.Select(item => item.Model.PosterPath)
                .Append(_detailFolderItem?.Model.PosterPath)
                .Append(_workspace.GetPosterOverride(folderPath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(folderPath)
                .Where(path => extensions.Contains(Path.GetExtension(path).TrimStart('.')) && !posterPaths.Contains(path)).OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (await LoadPosterAsync(path, token) is { } image)
                { images.Add(image); imagePaths[image] = path; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { App.Logger.LogWarning(ex, "Unable to load video illustrations from {Folder}", folderPath); }
        token.ThrowIfCancellationRequested();
        if (_animeLibrary)
        {
            _animeIllustrationCache.Clear();
            foreach (var pair in imagePaths) _animeIllustrationCache[pair.Key] = pair.Value;
            UpdateAnimeIllustrations();
            return;
        }
        _illustrationPaths.Clear();
        foreach (var pair in imagePaths) _illustrationPaths[pair.Key] = pair.Value;
        _illustrationImages = images;
        VideoIllustrations.ItemsSource = images;
        VideoIllustrationsEmpty.Visibility = images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAnimeIllustrations()
    {
        var orderedVideos = BrowserItems.OrderBy(item => item.EpisodeIndex).Select(item => item.Path).ToArray();
        var number = _activeVideoItem is null ? null : AnimeLibraryService.GetIllustrationNumber(_activeVideoItem.Path, orderedVideos);
        var matching = _activeVideoItem is null ? [] : _animeIllustrationCache.Where(pair => AnimeLibraryService.IsEpisodeIllustration(pair.Value, _activeVideoItem.Path, number)).ToArray();
        var images = matching.Length > 0 ? matching : _animeIllustrationCache.Where(pair => AnimeLibraryService.GetIllustrationGroup(pair.Value) is null && AnimeLibraryService.GetIllustrationEpisodeNumber(pair.Value) is null).ToArray();
        _illustrationPaths.Clear();
        foreach (var pair in images) _illustrationPaths[pair.Key] = pair.Value;
        _illustrationImages = images.Select(pair => pair.Key).ToList();
        VideoIllustrations.ItemsSource = _illustrationImages;
        VideoIllustrationsEmpty.Visibility = images.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddVideoIllustration_Click(object sender, RoutedEventArgs e)
    {
        if (_locations.Count == 0 || _locations[^1].Kind != ResourceBrowserLocationKind.VideoFolder)
            return;
        var folder = _locations[^1].Path;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        foreach (var extension in _workspace.Settings.ImageExtensions)
            picker.FileTypeFilter.Add($".{extension.TrimStart('.')}");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
            return;
        try
        {
            foreach (var file in files)
            {
                if (PathEquals(Path.GetDirectoryName(file.Path) ?? string.Empty, folder))
                    continue;
                var number = _animeLibrary && _activeVideoItem is not null ? AnimeLibraryService.GetIllustrationNumber(_activeVideoItem.Path, BrowserItems.OrderBy(item => item.EpisodeIndex).Select(item => item.Path).ToArray()) : null;
                var index = 1;
                var stem = number is null ? $"illustration-{Guid.NewGuid():N}" : $"{int.Parse(number):00}\u63D2\u56FE";
                var destination = Path.Combine(folder, $"{stem} ({index}){Path.GetExtension(file.Name)}");
                while (File.Exists(destination)) destination = Path.Combine(folder, $"{stem} ({++index}){Path.GetExtension(file.Name)}");
                File.Copy(file.Path, destination);
            }
            await LoadVideoIllustrationsAsync(folder, CancellationToken.None);
        }
        catch (Exception ex) { SetResourceStatusMessage($"添加插图失败：{ex.Message}"); }
    }

    private void ClearSelectedResourceItems()
    {
        _selectedResourceItems.Clear();
    }

    private void SetNativeSelection(List<ListedItem>? items)
    {
        if (_contentPageContext.ShellPage is ModernShellPage modernShellPage)
            modernShellPage.UpdateResourceLibrarySelection(items ?? []);
        if (_contentPageContext.ShellPage is { } shellPage)
            shellPage.ToolbarViewModel.SelectedItems = items;
    }

    private ListedItem CreateListedItem(ResourceBrowserItemViewModel item)
    {
        var isFile = item.Kind == ResourceBrowserItemKind.VideoFile;
        var info = isFile ? (FileSystemInfo)new FileInfo(item.Path) : new DirectoryInfo(item.Path);
        info.Refresh();
        var fileTags = Array.Empty<string>();
        ulong? fileReference = null;
        try
        {
            fileTags = FileTagsHelper.ReadFileTag(item.Path);
            fileReference = FileTagsHelper.GetFileFRN(item.Path);
        }
        catch
        {
            // Resource browsing and native previews should still work on volumes without tag support.
        }

        ResourceActorListedItem? actorListedItem = null;
        ResourceVideoFolderListedItem? videoFolderListedItem = null;
        ListedItem listedItem;
        if (item.Kind == ResourceBrowserItemKind.ActorFolder)
        {
            actorListedItem = new ResourceActorListedItem();
            listedItem = actorListedItem;
        }
        else if (item.Kind == ResourceBrowserItemKind.VideoFolder)
        {
            videoFolderListedItem = new ResourceVideoFolderListedItem
            {
                IsAnime = _animeLibrary,
                AnimeDetails = _animeLibrary ? _workspace.GetVideoDetails(item.Path) : null,
                PosterPath = item.Model.PosterPath,
                DisplayTitle = item.Model.Name,
            };
            listedItem = videoFolderListedItem;
        }
        else if (isFile)
        {
            listedItem = new ResourceVideoFileListedItem { IsAnime = _animeLibrary, AnimeDetails = _animeLibrary ? _workspace.GetVideoDetails(item.Path) : null };
        }
        else
        {
            listedItem = new ListedItem();
        }

        if (actorListedItem is not null)
        {
            var actor = actorListedItem;
            actor.ActorDetails = _workspace.GetActorDetails(item.Path);
            actor.ActorPosterPaths = GetActorPosterPaths(item, actor.ActorDetails);
            actor.MainPosterPath = _workspace.GetPosterOverride(item.Path) ?? item.Model.PosterPath;
            actor.CountActorVideosAsync = () => CountActorVideosAsync(item.Path);
            actor.EditActorDetailsAsync = async () =>
            {
                await EditActorDetailsAsync(item);
                actor.ActorDetails = _workspace.GetActorDetails(item.Path);
            };
            actor.AddActorPosterAsync = async () =>
            {
                var posterPath = await PickAndImportPosterAsync();
                if (posterPath is null)
                    return null;

                var updatedDetails = _workspace.GetActorDetails(item.Path);
                updatedDetails.ExcludedPosterPaths.RemoveAll(path =>
                    string.Equals(path, posterPath, StringComparison.OrdinalIgnoreCase));
                if (!updatedDetails.PosterPaths.Contains(posterPath, StringComparer.OrdinalIgnoreCase))
                    updatedDetails.PosterPaths.Add(posterPath);
                _workspace.SetActorDetails(item.Path, updatedDetails);
                var previousMainPoster = _workspace.GetPosterOverride(item.Path);
                _workspace.SetPosterOverride(item.Path, posterPath);
                if (previousMainPoster is not null)
                    _workspace.DeleteImportedPosterIfUnused(previousMainPoster);
                actor.ActorDetails = _workspace.GetActorDetails(item.Path);
                actor.ActorPosterPaths = GetActorPosterPaths(item, actor.ActorDetails);
                actor.MainPosterPath = posterPath;
                item.Poster = await LoadPosterAsync(posterPath, CancellationToken.None);
                return posterPath;
            };
            actor.SetActorMainPosterAsync = async posterPath =>
            {
                _workspace.SetPosterOverride(item.Path, posterPath);
                actor.MainPosterPath = posterPath;
                item.Poster = await LoadPosterAsync(posterPath, CancellationToken.None);
            };
            actor.DeleteActorPosterAsync = async posterPath =>
            {
                var updatedDetails = _workspace.GetActorDetails(item.Path);
                updatedDetails.PosterPaths.RemoveAll(path =>
                    string.Equals(path, posterPath, StringComparison.OrdinalIgnoreCase));
                if (!updatedDetails.ExcludedPosterPaths.Contains(posterPath, StringComparer.OrdinalIgnoreCase))
                    updatedDetails.ExcludedPosterPaths.Add(posterPath);

                _workspace.SetActorDetails(item.Path, updatedDetails);
                var remainingPosters = GetActorPosterPaths(item, updatedDetails);
                var wasMainPoster = string.Equals(actor.MainPosterPath, posterPath, StringComparison.OrdinalIgnoreCase);
                if (wasMainPoster)
                {
                    if (remainingPosters.Count > 0)
                    {
                        actor.MainPosterPath = remainingPosters[0];
                        _workspace.SetPosterOverride(item.Path, actor.MainPosterPath);
                    }
                    else
                    {
                        actor.MainPosterPath = null;
                        _workspace.ClearPosterOverride(item.Path);
                    }
                }

                actor.ActorDetails = _workspace.GetActorDetails(item.Path);
                actor.ActorPosterPaths = remainingPosters;
                item.Poster = actor.MainPosterPath is { } mainPosterPath
                    ? await LoadPosterAsync(mainPosterPath, CancellationToken.None)
                    : null;
                _workspace.DeleteImportedPosterIfUnused(posterPath);
            };
        }

        listedItem.ItemPath = item.Path;
        listedItem.ItemNameRaw = Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        listedItem.PrimaryItemAttribute = isFile ? StorageItemTypes.File : StorageItemTypes.Folder;
        listedItem.ItemType = isFile ? $"视频文件（{Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant()}）" : "文件夹";
        listedItem.FileExtension = isFile ? Path.GetExtension(item.Path) : null;
        listedItem.ItemDateModifiedReal = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        listedItem.ItemDateCreatedReal = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
        listedItem.ItemDateAccessedReal = new DateTimeOffset(info.LastAccessTimeUtc, TimeSpan.Zero);
        listedItem.FileTags = fileTags;
        listedItem.FileFRN = fileReference;

        if (videoFolderListedItem is not null)
        {
            var savedTranslation = _workspace.GetVideoTitleTranslation(GetVideoTitleTranslationKey(item, GetTranslationProvider()));
            if (savedTranslation is not null &&
                string.Equals(savedTranslation.SourceTitle, GetVideoTitleForTranslation(item), StringComparison.Ordinal))
            {
                videoFolderListedItem.HasTranslatedTitle = true;
                item.SetTranslatedTitle(savedTranslation.TranslatedTitle, showTranslatedTitle: false);
            }

            videoFolderListedItem.ToggleTitleAsync = () => ToggleVideoFolderTitleAsync(item, videoFolderListedItem);
        }

        if (info is FileInfo fileInfo)
        {
            try
            {
                listedItem.FileSizeBytes = fileInfo.Length;
                listedItem.FileSize = fileInfo.Length.ToSizeString();
            }
            catch (IOException)
            {
                // Preserve preview/selection if the file was removed during selection.
            }
            catch (UnauthorizedAccessException)
            {
                // Size is optional metadata; native preview can still attempt to open the file.
            }
        }

        return listedItem;
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private async void OnBrowserItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ResourceBrowserItemViewModel item)
            return;

        e.Handled = true;
        if (item.Kind == ResourceBrowserItemKind.VideoFile)
        {
            if (item.Model.IsPosterOnly) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetResourceStatusMessage($"无法打开视频：{ex.Message}");
            }

            return;
        }

        var locationKind = _animeLibrary && _locations.Count == 1 ? ResourceBrowserLocationKind.ActorFolder : item.Kind switch
        {
            ResourceBrowserItemKind.ActorFolder => ResourceBrowserLocationKind.ActorFolder,
            ResourceBrowserItemKind.VideoFolder => ResourceBrowserLocationKind.VideoFolder,
            _ => ResourceBrowserLocationKind.CategoryFolder,
        };

        if (!_animeLibrary && _locations.Count > 0 &&
            !PathEquals(Path.GetDirectoryName(item.Path) ?? string.Empty, _locations[^1].Path))
        {
            await TryNavigateToResourcePathAsync(item.Path);
            return;
        }

        NavigateToLocation(_locations.Append(new ResourceBrowserLocation(item.Path, locationKind, item.Name)).ToArray());
    }

    public async Task<bool> TryNavigateToResourcePathAsync(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch
        {
            return false;
        }

        var existingIndex = _locations.FindIndex(location => PathEquals(location.Path, fullPath));
        if (existingIndex >= 0)
        {
            if (existingIndex < _locations.Count - 1)
                NavigateToLocation(_locations.Take(existingIndex + 1).ToArray());
            return true;
        }

        if (!ResourceManagerPathScope.IsWithinLibrary(fullPath, _libraryPath))
            return false;

        if (PathEquals(fullPath, _libraryPath))
        {
            NavigateToLocation(new[] { new ResourceBrowserLocation(_libraryPath, ResourceBrowserLocationKind.LibraryRoot, _libraryPath) });
            return true;
        }

        var relative = Path.GetRelativePath(_libraryPath, fullPath);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            return false;

        var targetSegments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var trail = new List<ResourceBrowserLocation>
        {
            new(_libraryPath, ResourceBrowserLocationKind.LibraryRoot, _libraryPath),
        };

        var navigationCancellation = _loadCancellation?.Token ?? CancellationToken.None;
        try
        {
            for (var index = 0; index < targetSegments.Length; index++)
            {
                navigationCancellation.ThrowIfCancellationRequested();
                var current = trail[^1];
                var children = await _browser.GetChildrenAsync(current.Path, current.Kind, _workspace.Settings, navigationCancellation);
                var target = Path.Combine(current.Path, targetSegments[index]);
                var child = children.FirstOrDefault(item => PathEquals(item.Path, target));
                if (child is null)
                {
                    if (current.Kind == ResourceBrowserLocationKind.VideoFolder && index == targetSegments.Length - 1)
                    {
                        var video = children.FirstOrDefault(item => item.Kind == ResourceBrowserItemKind.VideoFile && PathEquals(item.Path, target));
                        if (video is not null)
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo { FileName = video.Path, UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                SetResourceStatusMessage($"无法打开视频：{ex.Message}");
                            }
                            return true;
                        }
                    }

                    SetResourceStatusMessage("该路径不属于资源管理可展示的演员、分类或视频内容。");
                    return true;
                }

                if (child.Kind == ResourceBrowserItemKind.VideoFile)
                {
                    if (index != targetSegments.Length - 1)
                    {
                        SetResourceStatusMessage("视频文件不能作为路径层级继续展开。");
                        return true;
                    }

                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = child.Path, UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        SetResourceStatusMessage($"无法打开视频：{ex.Message}");
                    }
                    return true;
                }

                var nextKind = child.Kind switch
                {
                    ResourceBrowserItemKind.ActorFolder => ResourceBrowserLocationKind.ActorFolder,
                    ResourceBrowserItemKind.VideoFolder => ResourceBrowserLocationKind.VideoFolder,
                    _ => ResourceBrowserLocationKind.CategoryFolder,
                };
                trail.Add(new ResourceBrowserLocation(child.Path, nextKind, child.Name));
            }
        }
        catch (OperationCanceledException) when (navigationCancellation.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex)
        {
            SetResourceStatusMessage($"无法打开资源路径：{ex.Message}");
            return true;
        }

        if (!IsLoaded)
            return true;

        NavigateToLocation(trail);
        return true;
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(NormalizeForComparison(left),
                NormalizeForComparison(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void NavigateToLocation(IReadOnlyList<ResourceBrowserLocation> locations)
    {
        RememberBrowserPosition();
        var arguments = new NavigationArguments
        {
            ResourceViewStates = _viewStates,
            NavPathParam = _animeLibrary ? "AnimeLibrary" : "ResourceManager",
            IsResourceLibraryPage = true,
            IsResourceManagerMode = false,
            ResourceLibraryPath = _libraryPath,
            AnimeEditPath = _pendingAnimeEditPath,
            ResourceLocationPaths = locations.Select(location => location.Path).ToArray(),
            ResourceLocationTitles = locations.Select(location => location.Title).ToArray(),
            ResourceLocationKinds = locations.Select(location => location.Kind).ToArray(),
        };

        if (_contentPageContext.ShellPage is { } shellPage)
            shellPage.NavigateToResourceLibraryLocation(arguments);
        else
        {
            _locations.Clear();
            _locations.AddRange(locations);
            _ = LoadLocationAsync(_locations[^1]);
        }
    }

    public async Task ChooseLibraryAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        _workspace.SetLibraryPath(folder.Path);
        _contentPageContext.ShellPage?.NavigateToResourceManager();
    }

    public async void ShowFormatOptimization(FrameworkElement anchor)
    {
        if (_locations.Count == 0 || LoadingRing.IsActive)
        {
            SetResourceStatusMessage("请等待当前资源窗口加载完成后再进行格式优化。");
            return;
        }

        if (_animeLibrary)
        {
            var current = _locations[^1];
            var content = new AnimeFormatOptimizationDialog(_libraryPath, current.Path, current.Kind == ResourceBrowserLocationKind.VideoFolder, _workspace);
            var animeDialog = ResourceDialogPresentation.Create(XamlRoot, content);
            content.RequestClose += (_, _) => animeDialog.Hide();
            animeDialog.Closing += (_, args) => args.Cancel = content.IsBusy;
            await animeDialog.ShowAsync();
            if (content.HasChanges)
            {
                var locations = _locations.Select(location =>
                {
                    var path = location.Path;
                    foreach (var mapping in content.AppliedMappings)
                        if (PathEquals(path, mapping.SourcePath) || path.StartsWith(mapping.SourcePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            path = mapping.TargetPath + path[mapping.SourcePath.Length..];
                    return new ResourceBrowserLocation(path, location.Kind, location.Kind == ResourceBrowserLocationKind.VideoFolder ? AnimeLibraryService.GetSeasonTitle(path) : location.Title);
                }).ToArray();
                NavigateToLocation(locations);
            }
            return;
        }

        var currentLocation = _locations[^1];
        var toolsDialog = new ResourceToolsDialog(
            _libraryPath,
            currentLocation.Path,
            currentLocation.Kind,
            BrowserItems.Select(item => item.Model).ToArray());
        var dialog = ResourceDialogPresentation.Create(XamlRoot, toolsDialog);
        toolsDialog.RequestClose += (_, _) => dialog.Hide();
        dialog.Closing += (_, args) => args.Cancel = toolsDialog.IsBusy;
        toolsDialog.StartFormatOptimizationPreview();
        await dialog.ShowAsync();
        if (toolsDialog.HasChanges && _locations.Count > 0)
            await LoadLocationAsync(_locations[^1]);
    }

    private async void AutoRecognizeIllustrations_Click(object sender, RoutedEventArgs e)
    {
        if (!_animeLibrary || _locations.Count == 0 || _locations[^1].Kind != ResourceBrowserLocationKind.VideoFolder) return;
        var item = _activeVideoItem ?? _detailFolderItem;
        if (item is not null) await ImportAnimeImagesAsync(item);
    }

    private async Task ImportAnimeImagesAsync(ResourceBrowserItemViewModel item)
    {
        try
        {
            var folder = item.Kind == ResourceBrowserItemKind.VideoFile ? Path.GetDirectoryName(item.Path)! : item.Path;
            var content = new AnimeImageImportDialog(folder, _workspace, item.Kind == ResourceBrowserItemKind.VideoFile ? item.Path : null);
            var dialog = ResourceDialogPresentation.Create(XamlRoot, content, 860, 600);
            dialog.Opened += async (_, _) => await content.InitializeSourceAsync();
            content.RequestClose += (_, _) => dialog.Hide();
            dialog.Closing += (_, args) => args.Cancel = content.IsBusy;
            await dialog.ShowAsync();
            if (content.HasChanges) await RefreshAsync();
        }
        catch (Exception ex) { SetResourceStatusMessage(Strings.AnimeImagesFailed.GetLocalizedResource() + " " + ex.Message); }
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void OnBrowserItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_animeLibrary && _locations.Count > 0 && _locations[^1].Kind == ResourceBrowserLocationKind.LibraryRoot) { e.Handled = true; return; }
        if (sender is not FrameworkElement element || element.DataContext is not ResourceBrowserItemViewModel item)
            return;

        var pointerPosition = e.GetPosition(element);
        e.Handled = true;
        var flyout = new MenuFlyout();
        if (_animeLibrary && item.Kind is ResourceBrowserItemKind.VideoFolder or ResourceBrowserItemKind.VideoFile)
        {
            var edit = new MenuFlyoutItem { Text = Strings.AnimeLibraryEditDetails.GetLocalizedResource() };
            edit.Click += async (_, _) => { if (item.Kind == ResourceBrowserItemKind.VideoFile) { ActiveBrowserList.SelectedItem = item; BeginAnimeEdit(clickedVideo: item); } else await EditAnimeDetailsAsync(item); };
            flyout.Items.Add(edit);
        }
        if (item.Kind == ResourceBrowserItemKind.ActorFolder)
        {
            var hide = new MenuFlyoutItem { Text = "在资源管理中隐藏" };
            hide.Click += async (_, _) =>
            {
                _workspace.SetActorFolderHidden(item.Path, true);
                await RefreshAsync();
                SetResourceStatusMessage($"已在资源管理中隐藏“{item.Name}”；本地文件夹属性未更改。");
            };
            flyout.Items.Add(hide);
        }

        if (item.Kind == ResourceBrowserItemKind.ActorFolder)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var editDetails = new MenuFlyoutItem { Text = "编辑演员信息" };
            editDetails.Click += async (_, _) => await EditActorDetailsAsync(item);
            flyout.Items.Add(editDetails);

            var renameActor = new MenuFlyoutItem { Text = "重命名" };
            renameActor.Click += async (_, _) => await RenameResourceItemAsync(item);
            flyout.Items.Add(renameActor);

            var deleteActor = new MenuFlyoutItem
            {
                Text = "删除演员文件夹",
                IsEnabled = item.ActorWorkCount == 0,
            };
            ToolTipService.SetToolTip(deleteActor, item.ActorWorkCount switch
            {
                null => "正在统计作品数量，统计完成后才能删除空文件夹。",
                > 0 => "该演员文件夹包含作品，不能删除。",
                _ => null,
            });
            deleteActor.Click += async (_, _) =>
                await DeleteActorFoldersAsync([(item.Path, item.Name)]);
            flyout.Items.Add(deleteActor);
        }

        if (item.Kind != ResourceBrowserItemKind.ActorFolder)
        {
            var editResourceTags = new MenuFlyoutItem { Text = "编辑资源管理标签" };
            editResourceTags.Click += async (_, _) => await EditResourceTagsAsync(item);
            flyout.Items.Add(editResourceTags);
        }

        if (item.CanSetPoster && item.Kind != ResourceBrowserItemKind.ActorFolder)
        {
            var setPoster = new MenuFlyoutItem { Text = "设置海报" };
            setPoster.Click += async (_, _) => await ChoosePosterAsync(item);
            flyout.Items.Add(setPoster);

            if (_workspace.GetPosterOverride(item.Path) is not null)
            {
                var restorePoster = new MenuFlyoutItem { Text = "恢复预设海报" };
                restorePoster.Click += async (_, _) =>
                {
                    _workspace.ClearPosterOverride(item.Path);
                    await RefreshAsync();
                    SetResourceStatusMessage($"已恢复“{item.Name}”的自动匹配海报。");
                };
                flyout.Items.Add(restorePoster);
            }
        }

        var openFolder = new MenuFlyoutItem { Text = "在文件夹中打开" };
        openFolder.Click += (_, _) => OpenContainingFolder(item.Path);
        flyout.Items.Add(openFolder);
        flyout.ShowAt(element, new FlyoutShowOptions { Position = pointerPosition });
    }

    private void OnOpenTranslationSettings(object sender, RoutedEventArgs e)
        => _contentPageContext.ShellPage?.NavigateToSettings();

    private async Task ToggleVideoFolderTitleAsync(ResourceBrowserItemViewModel item, ResourceVideoFolderListedItem listedItem)
    {
        if (listedItem.IsTranslatedTitleShown)
        {
            listedItem.IsTranslatedTitleShown = false;
            listedItem.DisplayTitle = item.Model.Name;
            item.SetTranslatedTitle(listedItem.HasTranslatedTitle ? item.TranslatedTitle : null, showTranslatedTitle: false);
            return;
        }

        if (!_titleTranslationsInProgress.Add(item.Path))
            return;

        try
        {
            var sourceTitle = GetVideoTitleForTranslation(item);
            if (string.IsNullOrWhiteSpace(sourceTitle))
            {
                SetResourceStatusMessage("没有可翻译的视频标题。");
                return;
            }

            var provider = GetTranslationProvider();
            var translationKey = GetVideoTitleTranslationKey(item, provider);
            var savedTranslation = _workspace.GetVideoTitleTranslation(translationKey);
            string translatedTitle;
            if (savedTranslation is not null && string.Equals(savedTranslation.SourceTitle, sourceTitle, StringComparison.Ordinal))
            {
                translatedTitle = savedTranslation.TranslatedTitle;
            }
            else
            {
                var accessKeyId = ResourceTitleTranslationCredentialStore.GetMachineAccessKeyId();
                var accessKeySecret = ResourceTitleTranslationCredentialStore.GetMachineAccessKeySecret();
                var bailianApiKey = ResourceTitleTranslationCredentialStore.GetBailianApiKey();
                if ((provider == ResourceManagerTranslationProvider.AliyunMachineTranslation &&
                     (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(accessKeySecret))) ||
                    (provider == ResourceManagerTranslationProvider.BailianQwenMt && string.IsNullOrWhiteSpace(bailianApiKey)))
                {
                    SetResourceStatusMessage("请先在设置 > 资源管理中配置当前翻译模型的凭证。");
                    return;
                }

                SetResourceStatusMessage($"正在翻译“{sourceTitle}”…");
                translatedTitle = provider switch
                {
                    ResourceManagerTranslationProvider.BailianQwenMt => await _bailianTranslation.TranslateJapaneseTitleAsync(sourceTitle, bailianApiKey),
                    _ => await _machineTranslation.TranslateJapaneseTitleAsync(sourceTitle, accessKeyId, accessKeySecret),
                };
                _workspace.SetVideoTitleTranslation(translationKey, sourceTitle, translatedTitle);
            }

            listedItem.HasTranslatedTitle = true;
            listedItem.IsTranslatedTitleShown = true;
            listedItem.DisplayTitle = translatedTitle;
            item.SetTranslatedTitle(translatedTitle, showTranslatedTitle: true);
            SetResourceStatusMessage(string.Empty);
        }
        catch (Exception ex)
        {
            SetResourceStatusMessage($"标题翻译失败：{ex.Message}");
        }
        finally
        {
            _titleTranslationsInProgress.Remove(item.Path);
        }
    }

    private async Task BuildActorVideoGroupsAsync(
        ResourceBrowserLocation actorLocation,
        IReadOnlyList<ResourceBrowserItem> actorItems,
        CancellationToken cancellationToken)
    {
        var directVideoFolders = actorItems
            .Where(item => item.Kind == ResourceBrowserItemKind.VideoFolder)
            .ToList();
        var categoryGroups = new List<ActorVideoGroup>();
        var categoriesWithVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task<bool> CollectCategoryGroupsAsync(ResourceBrowserItem category, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 8)
                return false;

            var children = await _browser.GetChildrenAsync(category.Path, ResourceBrowserLocationKind.CategoryFolder, _workspace.Settings, cancellationToken);
            var directVideos = children
                .Where(child => child.Kind == ResourceBrowserItemKind.VideoFolder)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var hasVideos = directVideos.Count > 0;

            if (hasVideos || category.Name == "无中文字幕")
            {
                var relativeName = string.Join(" / ", Path.GetRelativePath(actorLocation.Path, category.Path)
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries));
                categoryGroups.Add(new ActorVideoGroup(relativeName, directVideos));
            }

            foreach (var childCategory in children.Where(child => child.Kind == ResourceBrowserItemKind.CategoryFolder))
                hasVideos |= await CollectCategoryGroupsAsync(childCategory, depth + 1);

            if (hasVideos || category.Name == "无中文字幕")
                categoriesWithVideos.Add(category.Path);

            return hasVideos;
        }

        foreach (var category in actorItems.Where(item => item.Kind == ResourceBrowserItemKind.CategoryFolder))
            await CollectCategoryGroupsAsync(category, 1);

        var defaultItems = directVideoFolders
            .Concat(actorItems.Where(item => item.Kind == ResourceBrowserItemKind.CategoryFolder && !categoriesWithVideos.Contains(item.Path)))
            .ToList();
        if (defaultItems.Count > 0 || categoryGroups.Count == 0)
            _actorVideoGroups.Add(new ActorVideoGroup("作品", defaultItems));

        _actorVideoGroups.AddRange(categoryGroups
            .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase));
        _selectedActorGroupIndex = 0;
    }

    private void BuildActorGroupButtons()
    {
        ActorGroupButtons.Children.Clear();
        for (var index = 0; index < _actorVideoGroups.Count; index++)
        {
            var capturedIndex = index;
            var button = new Button
            {
                Content = _actorVideoGroups[index].Name,
                Padding = new Thickness(12, 6, 12, 6),
                MinWidth = 0,
                Tag = index,
            };
            if (index == _selectedActorGroupIndex && Resources.TryGetValue("ActorGroupSelectedButtonStyle", out var style))
                button.Style = style as Style;
            button.Click += async (_, _) => await SelectActorGroupAsync(capturedIndex);
            ActorGroupButtons.Children.Add(button);
        }
    }

    private async Task SelectActorGroupAsync(int index)
    {
        if (index < 0 || index >= _actorVideoGroups.Count || index == _selectedActorGroupIndex)
            return;

        _selectedActorGroupIndex = index;
        _selectedActorGroupName = _actorVideoGroups[index].Name;
        BuildActorGroupButtons();
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var currentLoad = _loadCancellation = new CancellationTokenSource();
        var cancellationToken = currentLoad.Token;
        ClearSelectedResourceItems();
        SetNativeSelection(null);
        BrowserGrid.SelectedItems.Clear();
        BrowserItems.Clear();
        EmptyText.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        try
        {
            await DisplayResourceItemsAsync(_actorVideoGroups[index].Items, cancellationToken);
            EmptyText.Visibility = BrowserItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateActorGridLayout();
            UpdateNativeResourceStatus();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(currentLoad, _loadCancellation))
                LoadingRing.IsActive = false;
        }
    }

    private async Task DisplayResourceItemsAsync(IReadOnlyList<ResourceBrowserItem> items, CancellationToken cancellationToken)
    {
        var initialGridLayout = items.Any(item => item.Kind is ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder)
            ? CalculateActorGridLayout()
            : null;
        if (initialGridLayout is { } layout && BrowserGrid.ItemsPanelRoot is ItemsWrapGrid initialItemsPanel)
            initialItemsPanel.ItemWidth = layout.ItemWidth;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var viewModel = new ResourceBrowserItemViewModel(item, _workspace, _animeLibrary);
            if (initialGridLayout is { } initialLayout &&
                item.Kind is ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder)
                viewModel.SetAdaptiveCardWidth(initialLayout.CardWidth);

            viewModel.Poster = await LoadPosterAsync(item.PosterPath, cancellationToken);
            if (item.Kind == ResourceBrowserItemKind.VideoFolder)
            {
                try { viewModel.UpdateFileTags(FileTagsHelper.ReadFileTag(item.Path)); }
                catch { viewModel.UpdateFileTags([]); }
            }
            BrowserItems.Add(viewModel);
        }
        ApplyBrowserSorting();
    }

    private StatusBarViewModel? GetNativeResourceStatusBarViewModel()
    {
        if (_contentPageContext.ShellPage is ModernShellPage shellPage &&
            ReferenceEquals(shellPage.CurrentResourceLibraryPage, this))
            return shellPage.ResourceLibraryStatusBarViewModel;

        return null;
    }

    private void SetResourceStatusMessage(string? message)
    {
        if (GetNativeResourceStatusBarViewModel() is not { } statusBarViewModel)
            return;

        statusBarViewModel.DirectoryItemCount = string.IsNullOrWhiteSpace(message)
            ? GetResourceCountStatus()
            : message;
    }

    public void UpdateNativeResourceStatus(string? description = null)
    {
        if (GetNativeResourceStatusBarViewModel() is not { } statusBarViewModel)
            return;

        statusBarViewModel.DirectoryItemCount = string.IsNullOrWhiteSpace(description)
            ? GetResourceCountStatus()
            : description;
    }

    private string GetResourceCountStatus()
    {
        var locationKind = _locations.Count > 0 ? _locations[^1].Kind : ResourceBrowserLocationKind.LibraryRoot;
        if (_animeLibrary) return $"{BrowserItems.Count} {Strings.Items.GetLocalizedFormatResource(BrowserItems.Count)}";
        if (locationKind == ResourceBrowserLocationKind.LibraryRoot)
        {
            var actorCount = BrowserItems.Count(item => item.Kind == ResourceBrowserItemKind.ActorFolder);
            return string.Format(CultureInfo.CurrentCulture, Strings.ResourceManagerActorsCollected.GetLocalizedResource(), actorCount);
        }

        if (locationKind is ResourceBrowserLocationKind.ActorFolder or ResourceBrowserLocationKind.CategoryFolder)
        {
            var workCount = BrowserItems.Count(item => item.Kind == ResourceBrowserItemKind.VideoFolder);
            return string.Format(CultureInfo.CurrentCulture, Strings.ResourceManagerWorksCollected.GetLocalizedResource(), workCount);
        }

        var count = BrowserItems.Count;
        return $"{count} {Strings.Items.GetLocalizedFormatResource(count)}";
    }

    private string GetVideoTitleForTranslation(ResourceBrowserItemViewModel item)
    {
        var itemName = item.Kind == ResourceBrowserItemKind.VideoFile
            ? Path.GetFileNameWithoutExtension(item.Model.Name)
            : item.Model.Name;
        return _videoAssistantSearch.GetVideoTitle(itemName).Trim();
    }

    private string GetVideoTitleTranslationKey(ResourceBrowserItemViewModel item, ResourceManagerTranslationProvider provider)
    {
        var itemName = item.Kind == ResourceBrowserItemKind.VideoFile
            ? Path.GetFileNameWithoutExtension(item.Model.Name)
            : item.Model.Name;
        return _videoAssistantSearch.GetVideoTitleTranslationKey(itemName, item.Path, provider.ToString());
    }

    private ResourceManagerTranslationProvider GetTranslationProvider()
        => Enum.TryParse<ResourceManagerTranslationProvider>(_appSettings.ResourceManagerTranslationProvider, true, out var provider)
            ? provider
            : ResourceManagerTranslationProvider.AliyunMachineTranslation;

    private async Task EditResourceTagsAsync(ResourceBrowserItemViewModel item)
    {
        var selectedTagIds = ReadTagIds(item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ContentDialog? dialog = null;
        var tagEditor = CreateTagEditor(selectedTagIds, () =>
        {
            dialog?.Hide();
            _ = ManageResourceTagsAsync();
        }).Panel;

        dialog = new ContentDialog
        {
            Title = $"编辑资源管理标签：{item.Name}",
            Content = new ScrollViewer { Content = tagEditor, MaxHeight = 520, MaxWidth = 620 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await SaveTagsAsync(item.Path, selectedTagIds);
        await RefreshAsync();
        SetResourceStatusMessage($"已保存“{item.Name}”的资源管理标签。");
    }

    [DynamicWindowsRuntimeCast(typeof(Microsoft.UI.Xaml.Media.Brush))]
    private async Task EditActorDetailsAsync(ResourceBrowserItemViewModel item)
    {
        var details = _workspace.GetActorDetails(item.Path);
        var content = new StackPanel { Spacing = 12 };

        var nameBox = new TextBox { Header = "姓名", Text = string.IsNullOrWhiteSpace(details.Name) ? item.Model.Name : details.Name };
        var aliasesBox = new TextBox { Header = "别名", Text = details.Aliases, PlaceholderText = "可填写多个别名" };
        var biographyBox = new TextBox
        {
            Header = "简介",
            Text = details.Biography,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 88,
            MaxLength = 4000,
        };
        var heightBox = new TextBox { Text = details.HeightCm, PlaceholderText = "身高" };
        var weightBox = new TextBox { Text = details.WeightKg, PlaceholderText = "体重" };
        heightBox.BeforeTextChanging += (_, args) => args.Cancel = !IsValidNumberInput(args.NewText, allowDecimal: false);
        weightBox.BeforeTextChanging += (_, args) => args.Cancel = !IsValidNumberInput(args.NewText, allowDecimal: true);
        var (bustField, bustBox) = CreateMeasurementField("B", details.Bust);
        var (waistField, waistBox) = CreateMeasurementField("W", details.Waist);
        var (hipField, hipBox) = CreateMeasurementField("H", details.Hip);
        var cupBox = new AutoSuggestBox
        {
            Width = 76,
            PlaceholderText = "A–Z",
            Text = details.CupSize,
            MaxSuggestionListHeight = 240,
            UpdateTextOnSelect = true,
        };
        var cupOptions = Enumerable.Range('A', 26).Select(value => ((char)value).ToString()).ToArray();
        cupBox.ItemsSource = cupOptions;
        var normalizingCupText = false;
        cupBox.TextChanged += (sender, args) =>
        {
            if (normalizingCupText || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;

            var normalized = new string(sender.Text
                .Where(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                .Take(1)
                .Select(char.ToUpperInvariant)
                .ToArray());
            if (!string.Equals(normalized, sender.Text, StringComparison.Ordinal))
            {
                normalizingCupText = true;
                sender.Text = normalized;
                normalizingCupText = false;
            }

            sender.ItemsSource = string.IsNullOrEmpty(normalized)
                ? cupOptions
                : cupOptions.Where(option => option.StartsWith(normalized, StringComparison.Ordinal)).ToArray();
            sender.IsSuggestionListOpen = true;
        };
        cupBox.SuggestionChosen += (sender, args) =>
        {
            if (args.SelectedItem is string selectedCup)
                sender.Text = selectedCup;
        };
        cupBox.GotFocus += (_, _) =>
        {
            cupBox.ItemsSource = cupOptions;
            cupBox.IsSuggestionListOpen = true;
        };

        var nameGrid = new Grid { ColumnSpacing = 12 };
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(aliasesBox, 1);
        nameGrid.Children.Add(nameBox);
        nameGrid.Children.Add(aliasesBox);

        var fields = new StackPanel { Spacing = 10 };
        var physicalGrid = new Grid { ColumnSpacing = 12 };
        for (var index = 0; index < 2; index++)
            physicalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var heightField = CreateUnitField("身高", heightBox, "cm");
        var weightField = CreateUnitField("体重", weightBox, "kg");
        Grid.SetColumn(weightField, 1);
        physicalGrid.Children.Add(heightField);
        physicalGrid.Children.Add(weightField);

        var measurementRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        measurementRow.Children.Add(bustField);
        measurementRow.Children.Add(waistField);
        measurementRow.Children.Add(hipField);
        measurementRow.Children.Add(cupBox);

        var birthDateBox = new TextBox
        {
            Header = "出生日期",
            Text = details.BirthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            PlaceholderText = "yyyy-MM-dd",
        };

        var careerStatusBox = new ComboBox { Header = "生涯", PlaceholderText = "选择现役或退役" };
        careerStatusBox.Items.Add("现役");
        careerStatusBox.Items.Add("退役");
        careerStatusBox.SelectedIndex = details.CareerRetirementDate is not null || details.IsCurrentlyActive == false
            ? 1
            : details.IsCurrentlyActive == true ? 0 : -1;
        var retirementBox = new TextBox
        {
            Text = details.CareerRetirementDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            PlaceholderText = "退役日期（yyyy-MM-dd）",
            Visibility = careerStatusBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed,
        };
        careerStatusBox.SelectionChanged += (_, _) =>
        {
            retirementBox.Visibility = careerStatusBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (careerStatusBox.SelectedIndex == 0)
                retirementBox.Text = string.Empty;
        };
        retirementBox.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(retirementBox.Text) && careerStatusBox.SelectedIndex != 1)
                careerStatusBox.SelectedIndex = 1;
        };

        var careerGrid = new Grid { ColumnSpacing = 12 };
        careerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        careerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(retirementBox, 1);
        careerGrid.Children.Add(careerStatusBox);
        careerGrid.Children.Add(retirementBox);

        var validationText = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };

        fields.Children.Add(physicalGrid);
        fields.Children.Add(new TextBlock { Text = "数值", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, -4) });
        fields.Children.Add(measurementRow);
        fields.Children.Add(birthDateBox);
        fields.Children.Add(careerGrid);
        fields.Children.Add(validationText);

        content.Children.Add(nameGrid);
        content.Children.Add(biographyBox);
        content.Children.Add(fields);

        var actorDialog = new ContentDialog
        {
            Title = "编辑演员信息",
            Content = new ScrollViewer { Content = content, MaxHeight = 620, MaxWidth = 680 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        actorDialog.PrimaryButtonClick += (sender, args) =>
        {
            if (!string.IsNullOrWhiteSpace(birthDateBox.Text) &&
                !TryParseRetirementDate(birthDateBox.Text, out _))
            {
                args.Cancel = true;
                birthDateBox.Focus(FocusState.Programmatic);
                validationText.Text = "出生日期请输入有效日期，例如 1990-01-01。";
                validationText.Visibility = Visibility.Visible;
            }
			else if (!string.IsNullOrWhiteSpace(retirementBox.Text) &&
				careerStatusBox.SelectedIndex == 1 &&
				!TryParseRetirementDate(retirementBox.Text, out _))
			{
				args.Cancel = true;
				retirementBox.Focus(FocusState.Programmatic);
				validationText.Text = "退役日期请输入有效日期，例如 2020-01-01；留空则只显示退役。";
                validationText.Visibility = Visibility.Visible;
            }
            else if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                args.Cancel = true;
                nameBox.Focus(FocusState.Programmatic);
            }
            else if (!string.IsNullOrWhiteSpace(heightBox.Text) &&
                !int.TryParse(heightBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var ignoredHeight))
            {
                args.Cancel = true;
                validationText.Text = "身高请输入整数厘米数值。";
                validationText.Visibility = Visibility.Visible;
                heightBox.Focus(FocusState.Programmatic);
            }
            else if (!string.IsNullOrWhiteSpace(weightBox.Text) &&
                !decimal.TryParse(weightBox.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var ignoredWeight))
            {
                args.Cancel = true;
                validationText.Text = "体重请输入有效的 kg 数值。";
                validationText.Visibility = Visibility.Visible;
                weightBox.Focus(FocusState.Programmatic);
            }
            else if (!IsValidMeasurement(bustBox.Text) || !IsValidMeasurement(waistBox.Text) || !IsValidMeasurement(hipBox.Text))
            {
                args.Cancel = true;
                validationText.Text = "B、W、H 请输入符合位数规则的数字。";
                validationText.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrEmpty(cupBox.Text) &&
                (cupBox.Text.Length != 1 || cupBox.Text[0] is < 'A' or > 'Z'))
            {
                args.Cancel = true;
                validationText.Text = "罩杯仅接受 A–Z 中的一个英文字母。";
                validationText.Visibility = Visibility.Visible;
                cupBox.Focus(FocusState.Programmatic);
            }
        };

        if (await actorDialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        DateTime? birthDate = null;
        if (!string.IsNullOrWhiteSpace(birthDateBox.Text) &&
            TryParseRetirementDate(birthDateBox.Text, out var parsedBirthDate))
            birthDate = parsedBirthDate.Date;

        DateTime? retirementDate = null;
        if (careerStatusBox.SelectedIndex == 1 &&
            TryParseRetirementDate(retirementBox.Text, out var parsedDate))
            retirementDate = parsedDate.Date;

        _workspace.SetActorDetails(item.Path, new ResourceActorDetails
        {
            Name = nameBox.Text.Trim(),
            Aliases = aliasesBox.Text.Trim(),
            Biography = biographyBox.Text.Trim(),
            HeightCm = heightBox.Text.Trim(),
            WeightKg = weightBox.Text.Trim(),
            Bust = bustBox.Text,
            Waist = waistBox.Text,
            Hip = hipBox.Text,
            CupSize = cupBox.Text,
            BirthDate = birthDate,
            IsCurrentlyActive = careerStatusBox.SelectedIndex switch
            {
                0 => true,
                1 => false,
                _ => null,
            },
            CareerRetirementDate = retirementDate,
            PosterPaths = details.PosterPaths,
            ExcludedPosterPaths = details.ExcludedPosterPaths,
        });

        await RefreshAsync();
        var refreshedActor = BrowserItems.FirstOrDefault(candidate => string.Equals(candidate.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        if (refreshedActor is not null)
            BrowserGrid.SelectedItems.Add(refreshedActor);
        SetResourceStatusMessage($"已保存“{nameBox.Text.Trim()}”的演员信息。");
    }

    private (StackPanel Panel, HashSet<string> TagIds) CreateTagEditor(HashSet<string> selectedTagIds, Action manageResourceTags)
    {
        var panel = new StackPanel { Spacing = 4 };
        var tagList = new StackPanel { Spacing = 4 };
        void RebuildTagChoices()
        {
            tagList.Children.Clear();
            foreach (var tag in _workspace.ResourceTags)
            {
                var checkBox = new CheckBox { Content = tag.Name, Tag = tag.Uid, IsChecked = selectedTagIds.Contains(tag.Uid) };
                checkBox.Checked += (_, _) => selectedTagIds.Add(tag.Uid);
                checkBox.Unchecked += (_, _) => selectedTagIds.Remove(tag.Uid);
                tagList.Children.Add(checkBox);
            }
        }

        RebuildTagChoices();
        panel.Children.Add(tagList);
        var newTagName = new TextBox { PlaceholderText = "输入资源标签，例如：中文字幕", MaxLength = 64 };
        var tagActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var addTag = new Button { Content = "新建资源标签并添加" };
        var manageTags = new Button { Content = "管理资源标签" };
        addTag.Click += (_, _) =>
        {
            var name = newTagName.Text.Trim();
            if (name.Length == 0)
                return;

            var tag = _workspace.GetResourceTagByName(name);
            try
            {
                tag ??= _workspace.CreateResourceTag(name, ColorHelpers.RandomColor());
            }
            catch (InvalidOperationException)
            {
                SetResourceStatusMessage($"资源标签“{name}”已存在。");
            }

            if (tag is not null)
                selectedTagIds.Add(tag.Uid);
            newTagName.Text = string.Empty;
            RebuildTagChoices();
        };
        manageTags.Click += (_, _) => manageResourceTags();
        tagActions.Children.Add(addTag);
        tagActions.Children.Add(manageTags);
        panel.Children.Add(newTagName);
        panel.Children.Add(tagActions);
        return (panel, selectedTagIds);
    }

    [DynamicWindowsRuntimeCast(typeof(Microsoft.UI.Xaml.Media.Brush))]
    private async Task ManageResourceTagsAsync()
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "资源管理标签仅在资源管理中显示，不会写入 Windows 原生文件标签。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var newTagName = new TextBox { Header = "新建资源标签", PlaceholderText = "例如：中文字幕", MaxLength = 64 };
        var newTagButton = new Button { Content = "新建", HorizontalAlignment = HorizontalAlignment.Left };
        var tagList = new StackPanel { Spacing = 8 };
        content.Children.Add(newTagName);
        content.Children.Add(newTagButton);
        content.Children.Add(tagList);

        var dialog = new ContentDialog
        {
            Title = "管理资源标签",
            Content = new ScrollViewer { Content = content, MaxHeight = 620, MaxWidth = 720 },
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        void AddTagRow(ResourceTagDefinition tag)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBox = new TextBox { Text = tag.Name, PlaceholderText = "标签名称", MinWidth = 180 };
            var colorPicker = new CommunityToolkit.WinUI.Controls.ColorPicker
            {
                Color = ColorHelpers.FromHex(tag.Color),
                IsAlphaEnabled = false,
                Width = 170,
            };
            var saveButton = new Button { Content = "保存" };
            var deleteButton = new Button { Content = "删除" };
            Grid.SetColumn(colorPicker, 1);
            Grid.SetColumn(saveButton, 2);
            Grid.SetColumn(deleteButton, 3);
            row.Children.Add(nameBox);
            row.Children.Add(colorPicker);
            row.Children.Add(saveButton);
            row.Children.Add(deleteButton);

            saveButton.Click += (_, _) =>
            {
                var color = CommunityToolkit.WinUI.Helpers.ColorHelper.ToHex(colorPicker.Color);
                if (!_workspace.EditResourceTag(tag.Uid, nameBox.Text, color))
                {
                    SetResourceStatusMessage("资源标签名称不能为空或已存在。");
                    return;
                }

                SetResourceStatusMessage($"已更新资源标签“{nameBox.Text.Trim()}”。");
                _ = RefreshAsync();
            };
            deleteButton.Click += (_, _) =>
            {
                if (_workspace.DeleteResourceTag(tag.Uid))
                {
                    tagList.Children.Remove(row);
                    SetResourceStatusMessage($"已删除资源标签“{tag.Name}”。");
                    _ = RefreshAsync();
                }
            };
            tagList.Children.Add(row);
        }

        foreach (var tag in _workspace.ResourceTags.ToArray())
            AddTagRow(tag);

        newTagButton.Click += (_, _) =>
        {
            var name = newTagName.Text.Trim();
            if (name.Length == 0)
                return;

            try
            {
                var tag = _workspace.CreateResourceTag(name, ColorHelpers.RandomColor());
                AddTagRow(tag);
                newTagName.Text = string.Empty;
                SetResourceStatusMessage($"已创建资源标签“{name}”。");
            }
            catch (InvalidOperationException)
            {
                SetResourceStatusMessage($"资源标签“{name}”已存在。");
            }
        };

        await dialog.ShowAsync();
        await RefreshAsync();
    }

    private static (FrameworkElement Field, TextBox Input) CreateMeasurementField(string label, string value)
    {
        var field = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        field.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var textBox = new TextBox { Text = value, MaxLength = 3, Width = 62 };
        textBox.BeforeTextChanging += (_, args) => args.Cancel = !IsValidMeasurement(args.NewText);
        field.Children.Add(textBox);
        return (field, textBox);
    }

    private static IReadOnlyList<string> GetActorPosterPaths(ResourceBrowserItemViewModel item, ResourceActorDetails details)
    {
        var excludedPosterPaths = details.ExcludedPosterPaths ?? [];
        var paths = new List<string>();
        var mainPosterPath = item.Model.PosterPath;
        if (!string.IsNullOrWhiteSpace(mainPosterPath) &&
            File.Exists(mainPosterPath) &&
            !excludedPosterPaths.Contains(mainPosterPath, StringComparer.OrdinalIgnoreCase))
            paths.Add(mainPosterPath);

        paths.AddRange((details.PosterPaths ?? []).Where(path =>
            !excludedPosterPaths.Contains(path, StringComparer.OrdinalIgnoreCase)));
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsValidMeasurement(string value)
    {
        if (value.Length == 0)
            return true;
        if (value.Any(character => character is < '0' or > '9'))
            return false;
        return value[0] == '1' ? value.Length <= 3 : value.Length <= 2;
    }

    private static bool IsValidNumberInput(string value, bool allowDecimal)
    {
        if (value.Length == 0)
            return true;

        var decimalSeparatorCount = value.Count(character => character == '.');
        return (allowDecimal ? decimalSeparatorCount <= 1 : decimalSeparatorCount == 0) &&
            value.All(character => character is >= '0' and <= '9' or '.');
    }

    private static FrameworkElement CreateUnitField(string label, TextBox textBox, string unit)
    {
        var field = new StackPanel { Spacing = 4 };
        field.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textBox, 0);
        var unitText = new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(unitText, 1);
        row.Children.Add(textBox);
        row.Children.Add(unitText);
        field.Children.Add(row);
        return field;
    }

    private async Task LoadActorWorkCountsAsync(IReadOnlyList<ResourceBrowserItemViewModel> actors, CancellationToken cancellationToken)
    {
        using var concurrencyLimit = new SemaphoreSlim(3);
        var tasks = actors.Select(async actor =>
        {
            try
            {
                await concurrencyLimit.WaitAsync(cancellationToken);
                try
                {
                    var workCount = await CountActorVideosAsync(actor.Path, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => actor.ActorWorkCount = workCount);
                }
                finally
                {
                    concurrencyLimit.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The actor list changed before its background work-count scan completed.
            }
        });

        await Task.WhenAll(tasks);
        if (!cancellationToken.IsCancellationRequested)
            await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(RefreshNativeSelectionAvailability);
    }

    private void RefreshNativeSelectionAvailability()
    {
        var selectedItems = ActiveBrowserList.SelectedItems
            .OfType<ResourceBrowserItemViewModel>()
            .Select(CreateListedItem)
            .ToList();
        SetNativeSelection(selectedItems.Count == 0 ? null : selectedItems);
    }

    private async Task<int> CountActorVideosAsync(string actorFolderPath, CancellationToken cancellationToken = default)
    {
        var videoExtensions = _workspace.Settings.VideoExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await Task.Run(() =>
        {
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = System.IO.FileAttributes.ReparsePoint,
                };
                var count = 0;
                foreach (var path in Directory.EnumerateFiles(actorFolderPath, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (videoExtensions.Contains(Path.GetExtension(path).TrimStart('.')))
                        count++;
                }

                return count;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return 0;
            }
        }, cancellationToken);
    }

    private static bool TryParseRetirementDate(string value, out DateTime date)
    {
        var formats = new[] { "yyyy-MM-dd", "yyyy/M/d", "yyyy/M/dd", "yyyy年M月d日" };
        return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.CurrentCulture, DateTimeStyles.None, out date);
    }

    private Task SaveTagsAsync(string path, IEnumerable<string> selectedTagIds)
    {
        _workspace.SetResourceTagIds(path, selectedTagIds);
        return Task.CompletedTask;
    }

    private string[] ReadTagIds(string path)
        => _workspace.GetResourceTagIds(path).ToArray();

    public async Task ManageHiddenActorsAsync()
    {
        var hiddenPaths = _workspace.HiddenActorFolders;
        if (hiddenPaths.Count == 0)
        {
            SetResourceStatusMessage("目前没有隐藏的演员文件夹。");
            return;
        }

        var list = new ListView
        {
            ItemsSource = hiddenPaths,
            SelectionMode = ListViewSelectionMode.Extended,
            MaxHeight = 380,
        };
        var dialog = new ContentDialog
        {
            Title = "管理隐藏的演员文件夹",
            Content = list,
            PrimaryButtonText = "恢复选中项",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItems.Count == 0)
            return;

        foreach (var path in list.SelectedItems.OfType<string>())
            _workspace.SetActorFolderHidden(path, false);

        await RefreshAsync();
        SetResourceStatusMessage($"已恢复显示 {list.SelectedItems.Count} 个演员文件夹。");
    }

    public Task ImportActorsAsync()
        => ResourceActorImportWorkflow.ImportAsync(_libraryPath, _workspace, XamlRoot, SetResourceStatusMessage,
            RefreshAsync, busy => LoadingRing.IsActive = busy);

    private async Task ChoosePosterAsync(ResourceBrowserItemViewModel item)
    {
        var posterPath = await PickAndImportPosterAsync();
        if (posterPath is null)
            return;

        var previousPoster = _workspace.GetPosterOverride(item.Path);
        _workspace.SetPosterOverride(item.Path, posterPath);
        if (previousPoster is not null)
            _workspace.DeleteImportedPosterIfUnused(previousPoster);
        await RefreshAsync();
        SetResourceStatusMessage($"已设置海报：{item.Name}");
    }

    private async Task<string?> PickAndImportPosterAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        foreach (var extension in _workspace.Settings.ImageExtensions)
            picker.FileTypeFilter.Add($".{extension.TrimStart('.')}");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return null;

        try
        {
            return await _workspace.ImportPosterAsync(file.Path);
        }
        catch (Exception ex)
        {
            App.Logger.LogError(ex, "Unable to import resource poster from {PosterPath}", file.Path);
            SetResourceStatusMessage($"导入海报失败：{ex.Message}");
            return null;
        }
    }

    private static void OpenContainingFolder(string path)
    {
        try
        {
            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder))
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{folder}\"", UseShellExecute = true });
        }
        catch
        {
        }
    }

    private sealed record ResourceBrowserLocation(string Path, ResourceBrowserLocationKind Kind, string Title);
}
