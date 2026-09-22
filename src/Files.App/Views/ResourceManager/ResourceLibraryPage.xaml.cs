// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.EventArguments;
using Files.App.Data.Models.ResourceManager;
using Files.App.Services.ResourceManager;
using Files.App.ViewModels.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Diagnostics;
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
        _libraryPath = args?.ResourceLibraryPath ?? _workspace.LibraryPath;
        if (string.IsNullOrWhiteSpace(_libraryPath) || !Directory.Exists(_libraryPath))
        {
            StatusText.Text = "资源库路径不可用，请重新选择路径。";
            return;
        }

        _locations.Clear();
        _locations.Add(new ResourceBrowserLocation(_libraryPath, ResourceBrowserLocationKind.LibraryRoot, "资源库"));
        await LoadLocationAsync(_locations[^1]);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => _loadCancellation?.Cancel();

    private async Task LoadLocationAsync(ResourceBrowserLocation location)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;

        LoadingRing.IsActive = true;
        BrowserItems.Clear();
        EmptyText.Visibility = Visibility.Collapsed;
        BackButton.IsEnabled = _locations.Count > 1;
        LibraryPathText.Text = _libraryPath;
        BreadcrumbText.Text = string.Join("  /  ", _locations.Select(x => x.Title));
        StatusText.Text = "正在加载资源……";

        try
        {
            var items = await _browser.GetChildrenAsync(location.Path, location.Kind, _workspace.Settings, cancellationToken);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var viewModel = new ResourceBrowserItemViewModel(item);
                viewModel.Poster = await LoadPosterAsync(item.PosterPath, cancellationToken);
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

    private async void OnBrowserItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ResourceBrowserItemViewModel item)
            return;

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
        _locations.Add(new ResourceBrowserLocation(item.Path, locationKind, item.Name));
        await LoadLocationAsync(_locations[^1]);
    }

    private async void OnBack(object sender, RoutedEventArgs e)
    {
        if (_locations.Count <= 1)
            return;

        _locations.RemoveAt(_locations.Count - 1);
        await LoadLocationAsync(_locations[^1]);
    }

    private async void OnRoot(object sender, RoutedEventArgs e)
    {
        if (_locations.Count == 1)
            return;

        _locations.RemoveRange(1, _locations.Count - 1);
        await LoadLocationAsync(_locations[0]);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadLocationAsync(_locations[^1]);

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
    private async void OnBrowserItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ResourceBrowserItemViewModel item)
            return;

        var flyout = new MenuFlyout();
        if (item.CanSetPoster)
        {
            var setPoster = new MenuFlyoutItem { Text = "设置海报" };
            setPoster.Click += async (_, _) => await ChoosePosterAsync(item);
            flyout.Items.Add(setPoster);
        }

        var openFolder = new MenuFlyoutItem { Text = "在文件夹中打开" };
        openFolder.Click += (_, _) => OpenContainingFolder(item.Path);
        flyout.Items.Add(openFolder);
        flyout.ShowAt(element);
        e.Handled = true;
    }

    private async Task ChoosePosterAsync(ResourceBrowserItemViewModel item)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
        foreach (var extension in _workspace.Settings.ImageExtensions)
            picker.FileTypeFilter.Add($".{extension.TrimStart('.')}");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        _workspace.SetPosterOverride(item.Path, file.Path);
        await LoadLocationAsync(_locations[^1]);
        StatusText.Text = $"已设置海报：{item.Name}";
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
