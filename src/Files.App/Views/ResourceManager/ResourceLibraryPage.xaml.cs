// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.EventArguments;
using Files.App.Data.Models;
using Files.App.Data.Models.ResourceManager;
using Files.App.Helpers;
using Files.App.Services.ResourceManager;
using Files.App.Utils;
using Files.App.Utils.FileTags;
using Files.App.ViewModels.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private readonly IContentPageContext _contentPageContext = Ioc.Default.GetRequiredService<IContentPageContext>();
    private readonly List<ResourceBrowserLocation> _locations = [];
    private readonly Dictionary<string, (ListedItem Item, ResourceBrowserItemViewModel ViewModel)> _selectedResourceItems = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loadCancellation;
    private string _libraryPath = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<ResourceBrowserItemViewModel> BrowserItems { get; } = [];

    public ResourceLibraryPage()
    {
        InitializeComponent();
        Unloaded += OnPageUnloaded;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var args = e.Parameter as NavigationArguments;
        try
        {
            _libraryPath = Path.GetFullPath(args?.ResourceLibraryPath ?? _workspace.LibraryPath);
        }
        catch
        {
            _libraryPath = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(_libraryPath) || !Directory.Exists(_libraryPath))
        {
            StatusText.Text = "资源库路径不可用，请重新选择路径。";
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
        ClearSelectedResourceItems();
    }

    public async Task RefreshAsync()
    {
        if (_locations.Count > 0)
            await LoadLocationAsync(_locations[^1]);
    }

    public void NavigateToParentLocation()
    {
        if (_locations.Count > 1)
            NavigateToLocation(_locations.Take(_locations.Count - 1).ToArray());
    }

    private async Task LoadLocationAsync(ResourceBrowserLocation location)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;

        ClearSelectedResourceItems();
        SetNativeSelection(null);
        BrowserGrid.SelectedItems.Clear();
        LoadingRing.IsActive = true;
        BrowserItems.Clear();
        EmptyText.Visibility = Visibility.Collapsed;
        LibraryPathText.Text = _libraryPath;
        StatusText.Text = "正在加载资源……";

        try
        {
            var items = await _browser.GetChildrenAsync(location.Path, location.Kind, _workspace.Settings, cancellationToken);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var viewModel = new ResourceBrowserItemViewModel(item)
                {
                    Poster = await LoadPosterAsync(item.PosterPath, cancellationToken),
                };
                BrowserItems.Add(viewModel);
            }

            EmptyText.Visibility = BrowserItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"当前显示 {BrowserItems.Count} 项；仅展示资源目录和视频文件。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText.Text = "已停止加载。";
        }
        catch (Exception ex)
        {
            EmptyText.Visibility = Visibility.Visible;
            StatusText.Text = $"资源加载失败：{ex.Message}";
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
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

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private async void OnEditActorInfo(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element &&
            element.DataContext is ResourceBrowserItemViewModel { Kind: ResourceBrowserItemKind.ActorFolder } actor)
        {
            await EditActorDetailsAsync(actor);
        }
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

    private static ListedItem CreateListedItem(ResourceBrowserItemViewModel item)
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

        var listedItem = new ListedItem
        {
            ItemPath = item.Path,
            ItemNameRaw = Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            PrimaryItemAttribute = isFile ? StorageItemTypes.File : StorageItemTypes.Folder,
            ItemType = isFile ? $"视频文件（{Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant()}）" : "文件夹",
            FileExtension = isFile ? Path.GetExtension(item.Path) : null,
            ItemDateModifiedReal = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            ItemDateCreatedReal = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            ItemDateAccessedReal = new DateTimeOffset(info.LastAccessTimeUtc, TimeSpan.Zero),
            FileTags = fileTags,
            FileFRN = fileReference,
        };

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
    private void OnBrowserItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
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
                StatusText.Text = $"无法打开视频：{ex.Message}";
            }

            return;
        }

        var locationKind = item.Kind switch
        {
            ResourceBrowserItemKind.ActorFolder => ResourceBrowserLocationKind.ActorFolder,
            ResourceBrowserItemKind.VideoFolder => ResourceBrowserLocationKind.VideoFolder,
            _ => ResourceBrowserLocationKind.CategoryFolder,
        };
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
                                StatusText.Text = $"无法打开视频：{ex.Message}";
                            }
                            return true;
                        }
                    }

                    StatusText.Text = "该路径不属于资源管理可展示的演员、分类或视频内容。";
                    return true;
                }

                if (child.Kind == ResourceBrowserItemKind.VideoFile)
                {
                    if (index != targetSegments.Length - 1)
                    {
                        StatusText.Text = "视频文件不能作为路径层级继续展开。";
                        return true;
                    }

                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = child.Path, UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        StatusText.Text = $"无法打开视频：{ex.Message}";
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
            StatusText.Text = $"无法打开资源路径：{ex.Message}";
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

    private async void OnChooseLibrary(object sender, RoutedEventArgs e)
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

    private void OnOpenTools(object sender, RoutedEventArgs e)
        => _contentPageContext.ShellPage?.NavigateToResourceManagerTools();

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void OnBrowserItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ResourceBrowserItemViewModel item)
            return;

        var flyout = new MenuFlyout();
        if (item.Kind == ResourceBrowserItemKind.ActorFolder)
        {
            var hide = new MenuFlyoutItem { Text = "在资源管理中隐藏" };
            hide.Click += async (_, _) =>
            {
                _workspace.SetActorFolderHidden(item.Path, true);
                await RefreshAsync();
                StatusText.Text = $"已在资源管理中隐藏“{item.Name}”；本地文件夹属性未更改。";
            };
            flyout.Items.Add(hide);
        }

        if (item.Kind == ResourceBrowserItemKind.ActorFolder)
        {
            var editDetails = new MenuFlyoutItem { Text = "编辑演员信息" };
            editDetails.Click += async (_, _) => await EditActorDetailsAsync(item);
            flyout.Items.Add(editDetails);
        }

        var editResourceTags = new MenuFlyoutItem { Text = "编辑资源管理标签" };
        editResourceTags.Click += async (_, _) => await EditResourceTagsAsync(item);
        flyout.Items.Add(editResourceTags);

        if (item.CanSetPoster)
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
                    StatusText.Text = $"已恢复“{item.Name}”的自动匹配海报。";
                };
                flyout.Items.Add(restorePoster);
            }
        }

        var openFolder = new MenuFlyoutItem { Text = "在文件夹中打开" };
        openFolder.Click += (_, _) => OpenContainingFolder(item.Path);
        flyout.Items.Add(openFolder);
        flyout.ShowAt(element);
        e.Handled = true;
    }

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
        StatusText.Text = $"已保存“{item.Name}”的资源管理标签。";
    }

    [DynamicWindowsRuntimeCast(typeof(Microsoft.UI.Xaml.Media.Brush))]
    private async Task EditActorDetailsAsync(ResourceBrowserItemViewModel item)
    {
        var details = _workspace.GetActorDetails(item.Path);
        var selectedTagIds = ReadTagIds(item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var content = new StackPanel { Spacing = 12 };

        var posterImage = new Image
        {
            Width = 112,
            Height = 150,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            Source = await LoadPosterAsync(_workspace.GetPosterOverride(item.Path) ?? item.Model.PosterPath, CancellationToken.None),
        };
        var posterBorder = new Border
        {
            Width = 112,
            Height = 150,
            CornerRadius = new CornerRadius(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = posterImage,
        };
        var choosePoster = new Button { Content = "更换海报", HorizontalAlignment = HorizontalAlignment.Left };
        string? selectedPosterPath = null;
        choosePoster.Click += async (_, _) =>
        {
            selectedPosterPath = await PickPosterPathAsync();
            if (selectedPosterPath is not null)
                posterImage.Source = await LoadPosterAsync(selectedPosterPath, CancellationToken.None);
        };
        var posterArea = new StackPanel { Spacing = 8 };
        posterArea.Children.Add(new TextBlock { Text = "海报", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        posterArea.Children.Add(posterBorder);
        posterArea.Children.Add(choosePoster);

        var nameBox = new TextBox { Header = "姓名", Text = string.IsNullOrWhiteSpace(details.Name) ? item.Model.Name : details.Name };
        var aliasesBox = new TextBox { Header = "别名", Text = details.Aliases, PlaceholderText = "可填写多个别名" };
        var heightBox = new TextBox { Text = details.HeightCm, PlaceholderText = "身高" };
        var weightBox = new TextBox { Text = details.WeightKg, PlaceholderText = "体重" };
        heightBox.BeforeTextChanging += (_, args) => args.Cancel = !IsValidNumberInput(args.NewText, allowDecimal: false);
        weightBox.BeforeTextChanging += (_, args) => args.Cancel = !IsValidNumberInput(args.NewText, allowDecimal: true);
        var bustBox = CreateMeasurementBox("B", details.Bust);
        var waistBox = CreateMeasurementBox("W", details.Waist);
        var hipBox = CreateMeasurementBox("H", details.Hip);
        var cupBox = new AutoSuggestBox
        {
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

        var bodyGrid = new Grid { ColumnSpacing = 12 };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var fields = new StackPanel { Spacing = 10 };
        var physicalGrid = new Grid { ColumnSpacing = 12 };
        for (var index = 0; index < 2; index++)
            physicalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var heightField = CreateUnitField("身高", heightBox, "cm");
        var weightField = CreateUnitField("体重", weightBox, "kg");
        Grid.SetColumn(weightField, 1);
        physicalGrid.Children.Add(heightField);
        physicalGrid.Children.Add(weightField);

        var measurementGrid = new Grid { ColumnSpacing = 8 };
        for (var index = 0; index < 4; index++)
            measurementGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(waistBox, 1);
        Grid.SetColumn(hipBox, 2);
        Grid.SetColumn(cupBox, 3);
        measurementGrid.Children.Add(bustBox);
        measurementGrid.Children.Add(waistBox);
        measurementGrid.Children.Add(hipBox);
        measurementGrid.Children.Add(cupBox);

        var retirementBox = new TextBox
        {
            Text = details.CareerRetirementDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            PlaceholderText = "退役日期（留空表示在役）",
        };
        var careerStatus = new TextBlock
        {
            Text = details.CareerRetirementDate is { } retiredDate ? $"退役：{retiredDate:yyyy-MM-dd}" : "在役",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        retirementBox.TextChanged += (_, _) =>
        {
            careerStatus.Text = string.IsNullOrWhiteSpace(retirementBox.Text)
                ? "在役"
                : TryParseRetirementDate(retirementBox.Text, out var date)
                    ? $"退役：{date:yyyy-MM-dd}"
                    : "请输入有效日期；留空表示在役。";
        };

        var videoCountText = new TextBlock
        {
            Text = "作品数量：正在统计…",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var validationText = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        _ = UpdateActorVideoCountAsync(videoCountText, item.Path);

        fields.Children.Add(physicalGrid);
        fields.Children.Add(new TextBlock { Text = "数值", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, -4) });
        fields.Children.Add(measurementGrid);
        fields.Children.Add(new TextBlock { Text = "生涯", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, -4) });
        fields.Children.Add(retirementBox);
        fields.Children.Add(careerStatus);
        fields.Children.Add(videoCountText);
        fields.Children.Add(validationText);
        Grid.SetColumn(fields, 1);
        bodyGrid.Children.Add(posterArea);
        bodyGrid.Children.Add(fields);

        content.Children.Add(nameGrid);
        content.Children.Add(bodyGrid);
        content.Children.Add(new TextBlock { Text = "资源管理标签", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, -6) });
        ContentDialog? actorDialog = null;
        var tagEditor = CreateTagEditor(selectedTagIds, () =>
        {
            actorDialog?.Hide();
            _ = ManageResourceTagsAsync();
        }).Panel;
        content.Children.Add(tagEditor);

        actorDialog = new ContentDialog
        {
            Title = "编辑演员信息",
            Content = new ScrollViewer { Content = content, MaxHeight = 620, MaxWidth = 680 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        actorDialog.PrimaryButtonClick += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(retirementBox.Text) &&
                !TryParseRetirementDate(retirementBox.Text, out var ignoredRetirementDate))
            {
                args.Cancel = true;
                retirementBox.Focus(FocusState.Programmatic);
                careerStatus.Text = "请输入有效日期；留空表示在役。";
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

        DateTime? retirementDate = null;
        if (!string.IsNullOrWhiteSpace(retirementBox.Text) &&
            TryParseRetirementDate(retirementBox.Text, out var parsedDate))
            retirementDate = parsedDate.Date;

        _workspace.SetActorDetails(item.Path, new ResourceActorDetails
        {
            Name = nameBox.Text.Trim(),
            Aliases = aliasesBox.Text.Trim(),
            HeightCm = heightBox.Text.Trim(),
            WeightKg = weightBox.Text.Trim(),
            Bust = bustBox.Text,
            Waist = waistBox.Text,
            Hip = hipBox.Text,
            CupSize = cupBox.Text,
            CareerRetirementDate = retirementDate,
        });

        if (selectedPosterPath is not null)
            _workspace.SetPosterOverride(item.Path, selectedPosterPath);

        await SaveTagsAsync(item.Path, selectedTagIds);
        await RefreshAsync();
        StatusText.Text = $"已保存“{nameBox.Text.Trim()}”的演员信息。";
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
                StatusText.Text = $"资源标签“{name}”已存在。";
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
                    StatusText.Text = "资源标签名称不能为空或已存在。";
                    return;
                }

                StatusText.Text = $"已更新资源标签“{nameBox.Text.Trim()}”。";
                _ = RefreshAsync();
            };
            deleteButton.Click += (_, _) =>
            {
                if (_workspace.DeleteResourceTag(tag.Uid))
                {
                    tagList.Children.Remove(row);
                    StatusText.Text = $"已删除资源标签“{tag.Name}”。";
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
                StatusText.Text = $"已创建资源标签“{name}”。";
            }
            catch (InvalidOperationException)
            {
                StatusText.Text = $"资源标签“{name}”已存在。";
            }
        };

        await dialog.ShowAsync();
        await RefreshAsync();
    }

    private static TextBox CreateMeasurementBox(string label, string value)
    {
        var textBox = new TextBox { Header = label, Text = value, MaxLength = 3 };
        textBox.BeforeTextChanging += (_, args) => args.Cancel = !IsValidMeasurement(args.NewText);
        return textBox;
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

    private async Task<int> CountActorVideosAsync(string actorFolderPath)
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
                return Directory.EnumerateFiles(actorFolderPath, "*", options)
                    .Count(path => videoExtensions.Contains(Path.GetExtension(path).TrimStart('.')));
            }
            catch
            {
                return 0;
            }
        });
    }

    private async Task UpdateActorVideoCountAsync(TextBlock target, string actorFolderPath)
    {
        var videoCount = await CountActorVideosAsync(actorFolderPath);
        target.Text = $"作品数量：{videoCount}";
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

    private async void OnManageHiddenActors(object sender, RoutedEventArgs e)
    {
        var hiddenPaths = _workspace.HiddenActorFolders;
        if (hiddenPaths.Count == 0)
        {
            StatusText.Text = "目前没有隐藏的演员文件夹。";
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
        StatusText.Text = $"已恢复显示 {list.SelectedItems.Count} 个演员文件夹。";
    }

    private async Task ChoosePosterAsync(ResourceBrowserItemViewModel item)
    {
        var posterPath = await PickPosterPathAsync();
        if (posterPath is null)
            return;

        _workspace.SetPosterOverride(item.Path, posterPath);
        await RefreshAsync();
        StatusText.Text = $"已设置海报：{item.Name}";
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
