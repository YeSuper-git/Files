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
using WinRT;

namespace Files.App.Views.ResourceManager;

public sealed partial class ResourceLibraryPage : Page
{
    private readonly IResourceBrowserService _browser = Ioc.Default.GetRequiredService<IResourceBrowserService>();
    private readonly IResourceWorkspaceService _workspace = Ioc.Default.GetRequiredService<IResourceWorkspaceService>();
    private readonly IResourceTitleTranslationService _machineTranslation = Ioc.Default.GetRequiredService<IResourceTitleTranslationService>();
    private readonly IBailianQwenMtTitleTranslationService _bailianTranslation = Ioc.Default.GetRequiredService<IBailianQwenMtTitleTranslationService>();
    private readonly IAppSettingsService _appSettings = Ioc.Default.GetRequiredService<IAppSettingsService>();
    private readonly VideoAssistantSearchService _videoAssistantSearch = Ioc.Default.GetRequiredService<VideoAssistantSearchService>();
    private readonly IContentPageContext _contentPageContext = Ioc.Default.GetRequiredService<IContentPageContext>();
    private readonly List<ResourceBrowserLocation> _locations = [];
    private readonly List<ActorVideoGroup> _actorVideoGroups = [];
    private readonly Dictionary<string, (ListedItem Item, ResourceBrowserItemViewModel ViewModel)> _selectedResourceItems = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loadCancellation;
    private VideoAssistantChatView? _assistantChatView;
    private Storyboard? _assistantButtonScaleAnimation;
    private string _libraryPath = string.Empty;
    private int _selectedActorGroupIndex;
    private string? _selectedActorGroupName;
    private string? _selectedActorGroupActorPath;
    private readonly HashSet<string> _titleTranslationsInProgress = new(StringComparer.OrdinalIgnoreCase);

    public System.Collections.ObjectModel.ObservableCollection<ResourceBrowserItemViewModel> BrowserItems { get; } = [];
    public string CurrentPath => _locations.Count > 0 ? _locations[^1].Path : _libraryPath;

    private sealed record ActorVideoGroup(string Name, IReadOnlyList<ResourceBrowserItem> Items);

    public ResourceLibraryPage()
    {
        InitializeComponent();
        DataContext = this;
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
            _libraryPath = Path.GetFullPath(args?.ResourceLibraryPath ?? _workspace.LibraryPath);

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

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _loadCancellation?.Cancel();
        AssistantFloatingPanel.Visibility = Visibility.Collapsed;
        ClearSelectedResourceItems();
        if (GetNativeResourceStatusBarViewModel() is { } statusBarViewModel)
            statusBarViewModel.DirectoryItemCount = null;
    }

    public async Task RefreshAsync()
    {
        if (_locations.Count > 0)
            await LoadLocationAsync(_locations[^1]);
    }

    public void SelectAllItems()
        => BrowserGrid.SelectAll();

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
        if (BrowserGrid.SelectedItems.Count != 1 ||
            BrowserGrid.SelectedItems.OfType<ResourceBrowserItemViewModel>().FirstOrDefault() is not { } viewModel)
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
            BrowserItems.Clear();
            EmptyText.Visibility = Visibility.Collapsed;
            ActorGroupButtons.Children.Clear();
            ActorGroupSelector.Visibility = Visibility.Collapsed;
            _actorVideoGroups.Clear();
            SetResourceStatusMessage("正在加载资源……");

            var items = await _browser.GetChildrenAsync(location.Path, location.Kind, _workspace.Settings, cancellationToken);
            if (location.Kind == ResourceBrowserLocationKind.ActorFolder)
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

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var viewModel = new ResourceBrowserItemViewModel(item);
                if (initialGridLayout is { } initialLayout &&
                    item.Kind is ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder)
                    viewModel.SetAdaptiveCardWidth(initialLayout.CardWidth);

                viewModel.Poster = await LoadPosterAsync(item.PosterPath, cancellationToken);
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

            EmptyText.Visibility = BrowserItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetResourceStatusMessage(string.Empty);
            UpdateActorGridLayout();
            UpdateNativeResourceStatus();

            if (location.Kind == ResourceBrowserLocationKind.LibraryRoot && BrowserItems.Count > 0)
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

    private void BrowserGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateActorGridLayout();

    private void UpdateActorGridLayout()
    {
        if (BrowserGrid.ItemsPanelRoot is not ItemsWrapGrid itemsPanel)
            return;

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

        const double minimumCardWidth = 250;
        const double itemHorizontalChrome = 26;
        const double maximumCardWidth = 340;
        var minimumItemWidth = minimumCardWidth + itemHorizontalChrome;
        var columnCount = Math.Max(1, (int)Math.Floor(availableWidth / minimumItemWidth));
        var itemWidth = availableWidth / columnCount;
        var cardWidth = Math.Clamp(itemWidth - itemHorizontalChrome, 180, maximumCardWidth);
        return (itemWidth, cardWidth);
    }

    private static async Task<BitmapImage?> LoadPosterAsync(string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private void OnBrowserSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClearSelectedResourceItems();
        var selectedItems = new List<ListedItem>();
        foreach (var viewModel in BrowserGrid.SelectedItems.OfType<ResourceBrowserItemViewModel>())
        {
            var listedItem = CreateListedItem(viewModel);
            _selectedResourceItems[listedItem.ItemPath ?? viewModel.Path] = (listedItem, viewModel);
            selectedItems.Add(listedItem);
        }

        SetNativeSelection(selectedItems.Count == 0 ? null : selectedItems);
    }

    private void ClearSelectedResourceItems()
    {
        _selectedResourceItems.Clear();
    }

    private void SetNativeSelection(List<ListedItem>? items)
    {
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
                PosterPath = item.Model.PosterPath,
                DisplayTitle = item.Model.Name,
            };
            listedItem = videoFolderListedItem;
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
                var posterPath = await PickPosterPathAsync();
                if (posterPath is null)
                    return null;

                var updatedDetails = _workspace.GetActorDetails(item.Path);
                updatedDetails.ExcludedPosterPaths.RemoveAll(path =>
                    string.Equals(path, posterPath, StringComparison.OrdinalIgnoreCase));
                if (!updatedDetails.PosterPaths.Contains(posterPath, StringComparer.OrdinalIgnoreCase))
                    updatedDetails.PosterPaths.Add(posterPath);
                _workspace.SetActorDetails(item.Path, updatedDetails);
                _workspace.SetPosterOverride(item.Path, posterPath);
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

        var locationKind = item.Kind switch
        {
            ResourceBrowserItemKind.ActorFolder => ResourceBrowserLocationKind.ActorFolder,
            ResourceBrowserItemKind.VideoFolder => ResourceBrowserLocationKind.VideoFolder,
            _ => ResourceBrowserLocationKind.CategoryFolder,
        };

        if (_locations.Count > 0 &&
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
        var arguments = new NavigationArguments
        {
            NavPathParam = "ResourceManager",
            IsResourceLibraryPage = true,
            IsResourceManagerMode = false,
            ResourceLibraryPath = _libraryPath,
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

    public void ShowFormatOptimization(FrameworkElement anchor)
    {
        if (_locations.Count == 0 || LoadingRing.IsActive)
        {
            SetResourceStatusMessage("请等待当前资源窗口加载完成后再进行格式优化。");
            return;
        }

        var currentLocation = _locations[^1];
        var toolsDialog = new ResourceToolsDialog(
            _libraryPath,
            currentLocation.Path,
            currentLocation.Kind,
            BrowserItems.Select(item => item.Model).ToArray());
        var allowFlyoutClose = false;
        var flyout = new Flyout
        {
            Content = toolsDialog,
            Placement = FlyoutPlacementMode.Bottom,
        };
        toolsDialog.RequestClose += (_, _) =>
        {
            allowFlyoutClose = true;
            flyout.Hide();
        };
        flyout.Closing += (_, args) =>
        {
            if (toolsDialog.HasChanges && !allowFlyoutClose)
                args.Cancel = true;
        };
        flyout.Closed += async (_, _) =>
        {
            if (toolsDialog.HasChanges && _locations.Count > 0)
                await LoadLocationAsync(_locations[^1]);
        };

        flyout.ShowAt(anchor);
        toolsDialog.StartFormatOptimizationPreview();
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void OnBrowserItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ResourceBrowserItemViewModel item)
            return;

        var pointerPosition = e.GetPosition(element);
        e.Handled = true;
        var flyout = new MenuFlyout();
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
        => _contentPageContext.ShellPage?.NavigateToSettings("ResourceManagerPage");

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

            if (hasVideos)
            {
                var relativeName = string.Join(" / ", Path.GetRelativePath(actorLocation.Path, category.Path)
                    .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries));
                categoryGroups.Add(new ActorVideoGroup(relativeName, directVideos));
            }

            foreach (var childCategory in children.Where(child => child.Kind == ResourceBrowserItemKind.CategoryFolder))
                hasVideos |= await CollectCategoryGroupsAsync(childCategory, depth + 1);

            if (hasVideos)
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
            var viewModel = new ResourceBrowserItemViewModel(item);
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

        var count = BrowserItems.Count;
        statusBarViewModel.DirectoryItemCount = string.IsNullOrWhiteSpace(message)
            ? $"{count} {Strings.Items.GetLocalizedFormatResource(count)}"
            : message;
    }

    public void UpdateNativeResourceStatus(string? description = null)
    {
        if (GetNativeResourceStatusBarViewModel() is not { } statusBarViewModel)
            return;

        var count = BrowserItems.Count;
        statusBarViewModel.DirectoryItemCount = string.IsNullOrWhiteSpace(description)
            ? $"{count} {Strings.Items.GetLocalizedFormatResource(count)}"
            : description;
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
        var selectedItems = BrowserGrid.SelectedItems
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

    public async Task ImportActorsAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        LoadingRing.IsActive = true;
        SetResourceStatusMessage("正在读取演员资料包并匹配文件夹……");
        try
        {
            var targets = Directory.EnumerateDirectories(_libraryPath, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path))
                .Where(ChangLiActorImportService.IsImportActorFolder)
                .Select(directory =>
                {
                    var details = _workspace.GetActorDetails(directory.FullName);
                    return new ChangLiActorImportTarget(
                        directory.FullName,
                        directory.Name,
                        details.Name,
                        details.Aliases);
                })
                .ToArray();
            var plan = await ChangLiActorImportService.CreatePlanAsync(file.Path, targets);
            LoadingRing.IsActive = false;

            if (plan.Matches.Count == 0)
            {
                SetResourceStatusMessage($"没有找到可导入的匹配演员。未匹配 {plan.UnmatchedCount} 位，重名冲突 {plan.AmbiguousCount} 位。");
                return;
            }

            var overwriteCheckBox = new CheckBox
            {
                Content = "覆盖 Files 中已有资料和主海报；不勾选时只补空字段",
                IsChecked = false,
            };
            var summary = new StackPanel { Spacing = 10 };
            summary.Children.Add(new TextBlock
            {
                Text = $"导出文件包含 {plan.SourceActorCount} 位演员；按姓名、别名或日文名精确匹配到 {plan.Matches.Count} 个文件夹。未匹配 {plan.UnmatchedCount} 位，重名冲突 {plan.AmbiguousCount} 位。",
                TextWrapping = TextWrapping.Wrap,
            });
            summary.Children.Add(new TextBlock
            {
                Text = "导入会合并演员海报，并写入简介、生日、身高、体重、数值和罩杯。",
                TextWrapping = TextWrapping.Wrap,
            });
            summary.Children.Add(overwriteCheckBox);

            var dialog = new ContentDialog
            {
                Title = "导入演员资料",
                Content = summary,
                PrimaryButtonText = $"导入 {plan.Matches.Count} 位演员",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                SetResourceStatusMessage("已取消导入。");
                return;
            }

            LoadingRing.IsActive = true;
            SetResourceStatusMessage("正在导入演员资料和海报……");
            var result = await ChangLiActorImportService.ApplyAsync(
                plan,
                _workspace,
                overwriteCheckBox.IsChecked == true);
            await RefreshAsync();
            SetResourceStatusMessage($"已导入 {result.ImportedActors} 位演员，登记 {result.ImportedPhotos} 张海报；无法读取 {result.SkippedPhotos} 张。未匹配 {plan.UnmatchedCount} 位，重名冲突 {plan.AmbiguousCount} 位。");
        }
        catch (Exception ex)
        {
            App.Logger.LogError(ex, "Unable to import ChangLi actor data from {PackagePath}", file.Path);
            SetResourceStatusMessage($"导入演员失败：{ex.Message}");
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private async Task ChoosePosterAsync(ResourceBrowserItemViewModel item)
    {
        var posterPath = await PickPosterPathAsync();
        if (posterPath is null)
            return;

        _workspace.SetPosterOverride(item.Path, posterPath);
        await RefreshAsync();
        SetResourceStatusMessage($"已设置海报：{item.Name}");
    }

    private async Task<string?> PickPosterPathAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        foreach (var extension in _workspace.Settings.ImageExtensions)
            picker.FileTypeFilter.Add($".{extension.TrimStart('.')}");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
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
