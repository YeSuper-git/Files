// Copyright (c) Files Community. Licensed under the MIT License.
using Files.App.Services.ResourceManager;
using Files.App.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Globalization;

namespace Files.App.UserControls.ResourceManager;

public sealed partial class AnimeImageImportDialog : UserControl
{
    private readonly AnimeImageSourceService _service = new();
    private readonly IResourceWorkspaceService _workspace;
    private readonly string _folder;
    private string[] _videos;
    private readonly AnimeVideoSourceService _videoService = new(Ioc.Default.GetRequiredService<Files.App.Services.VideoEditor.VideoProbeService>());
    private readonly ComboBox _mediaMode = new() { Width = 130 };
    private bool VideoMode => _mediaMode.SelectedIndex == 1;
    private readonly List<(CheckBox Selected, AnimeVideoSourceService.PageVideo Video)> _videoChoices = [];
    private readonly Grid _previewOverlay = new() { Visibility = Visibility.Collapsed };
    private readonly Image _previewImage = new() { Stretch = Stretch.Uniform };
    private readonly TextBox _source;
    private readonly string _prefix;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _chromeStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _recognize = new() { Content = Strings.AnimeImagesChromeRecognize.GetLocalizedResource() };
    private bool _readingChrome;
    private readonly Button _scan = new() { Content = Strings.AnimeImagesScan.GetLocalizedResource() };
    private readonly Button _scanAndDownload = new() { Content = Strings.AnimeImagesFindAndDownload.GetLocalizedResource() };
    private readonly Button _save = new() { Content = Strings.AnimeImagesDownload.GetLocalizedResource(), IsEnabled = false, Visibility = Visibility.Collapsed };
    private readonly ComboBox _targetVideo = new() { PlaceholderText = Strings.AnimeImagesChooseVideo.GetLocalizedResource(), Width = 260, MaxDropDownHeight = 360 };
    private readonly Button _cancel = new() { Content = Strings.Cancel.GetLocalizedResource(), Visibility = Visibility.Collapsed };
    private readonly Button _close = ResourceDialogPresentation.CreateIconButton("\uE8BB", Strings.Close.GetLocalizedResource());
    private readonly List<(CheckBox Selected, AnimeImageSourceService.PageImage Image)> _choices = [];
    private readonly CheckBox _selectAll = new() { Content = Strings.AnimeImagesSelectSet.GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Center, IsEnabled = false, Visibility = Visibility.Collapsed };
    private bool _updatingSelection;
    private bool _allAdded;
    private bool _scanSucceeded;
    private int _sourceRevision;
    private bool _sourceInitialized;
    private string? _sourceNumber;
    private CancellationTokenSource? _operation;
    public bool IsBusy { get; private set; }
    public bool HasChanges { get; private set; }
    public event EventHandler? RequestClose;

    public AnimeImageImportDialog(string folder, IResourceWorkspaceService workspace, string? selectedVideo = null)
    {
        _folder = folder; _workspace = workspace;
        _videos = AnimeLibraryService.GetEpisodeTargets(folder, workspace.Settings).ToArray();
        foreach (var video in _videos) _targetVideo.Items.Add(Path.GetFileName(video));
        if (selectedVideo is not null) _targetVideo.SelectedIndex = Array.FindIndex(_videos, video => string.Equals(video, selectedVideo, StringComparison.OrdinalIgnoreCase));
        else if (_videos.Length == 1) _targetVideo.SelectedIndex = 0;
        _targetVideo.SelectionChanged += (_, _) => UpdateSave();
        _prefix = workspace.Settings.AnimeImageWebsitePrefix.Trim().TrimEnd('/') + "/";
        var source = workspace.Settings.AnimeImageSource.Trim();
        if (source.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) source = source[_prefix.Length..];
        else if (Uri.TryCreate(source, UriKind.Absolute, out _)) source = string.Empty;
        _source = new TextBox { Text = source, PlaceholderText = Strings.AnimeImagesSuffix.GetLocalizedResource(), Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        var prefixLabel = new TextBlock { Text = _prefix, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 350, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
        ToolTipService.SetToolTip(prefixLabel, _prefix);
        var grid = new Grid { RowSpacing = 12 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = Strings.AnimeImagesImport.GetLocalizedResource(), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
        _close.ClearValue(FrameworkElement.StyleProperty);
        _close.ClearValue(Control.BackgroundProperty); _close.ClearValue(Control.ForegroundProperty);
        _close.ClearValue(Control.BorderThicknessProperty);
        _close.CornerRadius = new CornerRadius(6);
        Grid.SetColumn(_close, 1); header.Children.Add(_close); grid.Children.Add(header);
        var input = new Grid { ColumnSpacing = 8 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) input.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        prefixLabel.MaxWidth = 220;
        input.Children.Add(prefixLabel); Grid.SetColumn(_source, 1); input.Children.Add(_source);
        Grid.SetColumn(_recognize, 2); input.Children.Add(_recognize);
        _chromeStatus.VerticalAlignment = VerticalAlignment.Center;
        _chromeStatus.FontSize = 12;
        _chromeStatus.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        Grid.SetColumn(_chromeStatus, 3); input.Children.Add(_chromeStatus);
        Grid.SetRow(input, 1); grid.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _mediaMode.Items.Add(Strings.AnimePreviewImages.GetLocalizedResource());
        _mediaMode.Items.Add(Strings.AnimeVideosFiles.GetLocalizedResource());
        _mediaMode.SelectedIndex = 0;
        actions.Children.Add(_mediaMode);
        actions.Children.Add(_targetVideo); actions.Children.Add(_scan); actions.Children.Add(_scanAndDownload); actions.Children.Add(_save);
        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        Grid.SetRow(actions, 2); grid.Children.Add(actions);
        Grid.SetRow(_selectAll, 3); grid.Children.Add(_selectAll);
        var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 4); grid.Children.Add(scroll);
        var statusRow = new Grid { ColumnSpacing = 12 };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition()); statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.VerticalAlignment = VerticalAlignment.Center;
        statusRow.Children.Add(_status); Grid.SetColumn(_cancel, 1); statusRow.Children.Add(_cancel);
        Grid.SetRow(statusRow, 5); grid.Children.Add(statusRow);
        _previewOverlay.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        _previewOverlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _previewOverlay.RowDefinitions.Add(new RowDefinition());
        var closePreview = ResourceDialogPresentation.CreateIconButton("\uE8BB", Strings.Close.GetLocalizedResource());
        closePreview.HorizontalAlignment = HorizontalAlignment.Right;
        closePreview.Click += (_, _) => { _previewOverlay.Visibility = Visibility.Collapsed; _previewImage.Source = null; };
        _previewOverlay.Children.Add(closePreview); Grid.SetRow(_previewImage, 1); _previewOverlay.Children.Add(_previewImage);
        Grid.SetRowSpan(_previewOverlay, 6); grid.Children.Add(_previewOverlay);
        Content = grid;
        _selectAll.Click += (_, _) =>
        {
            var select = _selectAll.IsChecked == true;
            _updatingSelection = true;
            foreach (var selected in SelectionBoxes().Where(box => box.IsEnabled)) selected.IsChecked = select;
            _updatingSelection = false;
            UpdateSave();
        };
        _source.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            if (!IsBusy && _scan.IsEnabled) await ScanAsync();
        };
        _mediaMode.SelectionChanged += (_, _) =>
        {
            _rows.Children.Clear(); _choices.Clear(); _videoChoices.Clear(); _scanSucceeded = false; _allAdded = false;
            _targetVideo.Visibility = VideoMode ? Visibility.Collapsed : Visibility.Visible;
            ReloadTargets(); _status.Text = string.Empty; UpdateSave();
        };
        _recognize.Click += async (_, _) => await RecognizeChromeAsync();
        _scan.Click += async (_, _) => await ScanAsync();
        _scanAndDownload.Click += async (_, _) =>
        {
            if (!VideoMode && _targetVideo.SelectedIndex < 0) { _status.Text = Strings.AnimeImagesChooseVideo.GetLocalizedResource(); return; }
            await ScanAsync();
            if (_scanSucceeded && !IsBusy && SelectionBoxes().Any(box => box.IsEnabled && box.IsChecked == true)) await SaveAsync();
        };
        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        _close.Click += (_, _) => { if (!IsBusy) RequestClose?.Invoke(this, EventArgs.Empty); };
        Unloaded += (_, _) => _operation?.Cancel();
    }

    public async Task InitializeSourceAsync()
    {
        if (_sourceInitialized) return;
        _sourceInitialized = true;
        var revision = _sourceRevision;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded || IsBusy || revision != _sourceRevision) return;
            _source.Focus(FocusState.Programmatic);
            _source.SelectAll();
        });
        await RecognizeChromeAsync();
    }

    private async Task RecognizeChromeAsync()
    {
        if (IsBusy || _readingChrome) return;
        _readingChrome = true;
        _recognize.IsEnabled = false;
        var sourceBeforeReading = _source.Text;
        var revision = _sourceRevision;
        try
        {
            _chromeStatus.Text = Strings.AnimeImagesChromeReading.GetLocalizedResource();
            var result = await ChromeImageSourceService.ReadSourceSuffixAsync();
            if (!IsLoaded) return;
            if (IsBusy || revision != _sourceRevision || !string.Equals(sourceBeforeReading, _source.Text, StringComparison.Ordinal))
            {
                _chromeStatus.Text = Strings.AnimeImagesChromeSkipped.GetLocalizedResource();
                return;
            }
            _chromeStatus.Text = result.Status switch
            {
                ChromeImageSourceService.ReadStatus.Success => string.Format(Strings.AnimeImagesChromeSuccess.GetLocalizedResource(), result.Suffix),
                ChromeImageSourceService.ReadStatus.NoChrome => Strings.AnimeImagesChromeNotOpen.GetLocalizedResource(),
                ChromeImageSourceService.ReadStatus.NoSuffix => Strings.AnimeImagesChromeNoNumber.GetLocalizedResource(),
                ChromeImageSourceService.ReadStatus.TimedOut => Strings.AnimeImagesChromeTimedOut.GetLocalizedResource(),
                _ => Strings.AnimeImagesChromeFailed.GetLocalizedResource(),
            };
            if (result.Suffix is null) return;
            _source.Text = result.Suffix;
            _source.Focus(FocusState.Programmatic);
            _source.SelectAll();
        }
        finally
        {
            _readingChrome = false;
            _recognize.IsEnabled = !IsBusy;
        }
    }

    private void Busy(bool busy)
    {
        _recognize.IsEnabled = !busy && !_readingChrome;
        _mediaMode.IsEnabled = !busy;
        IsBusy = busy; _source.IsEnabled = _scan.IsEnabled = _scanAndDownload.IsEnabled = _close.IsEnabled = _targetVideo.IsEnabled = !busy;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _rows.IsHitTestVisible = !busy; UpdateSave();
    }
    private IEnumerable<CheckBox> SelectionBoxes() => VideoMode ? _videoChoices.Select(row => row.Selected) : _choices.Select(row => row.Selected);
    private void ReloadTargets()
    {
        var previous = _targetVideo.SelectedIndex >= 0 && _targetVideo.SelectedIndex < _videos.Length ? _videos[_targetVideo.SelectedIndex] : null;
        try { _videos = AnimeLibraryService.GetEpisodeTargets(_folder, _workspace.Settings).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _videos = []; }
        _targetVideo.Items.Clear();
        foreach (var video in _videos) _targetVideo.Items.Add(Path.GetFileName(video));
        _targetVideo.SelectedIndex = previous is null ? (_videos.Length == 1 ? 0 : -1) : Array.FindIndex(_videos, path => string.Equals(path, previous, StringComparison.OrdinalIgnoreCase));
    }
    private void UpdateSave()
    {
        if (_updatingSelection) return;
        _selectAll.Visibility = SelectionBoxes().Any() ? Visibility.Visible : Visibility.Collapsed;
        var remaining = SelectionBoxes().Where(box => box.IsEnabled).ToArray();
        _selectAll.IsEnabled = !IsBusy && remaining.Length > 0;
        _selectAll.IsChecked = remaining.Length > 0 && remaining.All(box => box.IsChecked == true) ? true
            : remaining.Any(box => box.IsChecked == true) ? null : false;
        var hasSelectedImages = _scanSucceeded && remaining.Any(box => box.IsChecked == true);
        _save.Visibility = hasSelectedImages ? Visibility.Visible : Visibility.Collapsed;
        _save.IsEnabled = !IsBusy && (VideoMode || _targetVideo.SelectedIndex >= 0) && hasSelectedImages;
    }
    private async Task ScanAsync()
    {
        if (VideoMode) { await ScanVideosAsync(); return; }
        _sourceRevision++;
        _allAdded = false;
        _scanSucceeded = false;
        _selectAll.Visibility = Visibility.Collapsed;
        _operation?.Dispose(); _operation = new(); _rows.Children.Clear(); _choices.Clear(); Busy(true); _status.Text = Strings.AnimeImagesLoading.GetLocalizedResource();
        try
        {
            var suffix = _source.Text.Trim();
            _sourceNumber = System.Text.RegularExpressions.Regex.IsMatch(suffix, @"^[0-9]{5}$") ? suffix : null;
            var address = AnimeImageSourceService.ResolvePageAddress(suffix, _prefix);
            var discovered = await _service.InspectPageAsync(address, _operation.Token);
            var images = AnimeImageSourceService.FilterImages(discovered, _workspace.Settings.AnimeImageIncludedNames, _workspace.Settings.AnimeImageMatchAllNames);
            var nameMatches = images.Count;
            images = await _service.FilterDimensionsAsync(images, _workspace.Settings.AnimeImageMinimumWidth, _workspace.Settings.AnimeImageMinimumHeight, _operation.Token);
            var settings = _workspace.Settings.Clone(); settings.AnimeImageSource = _source.Text.Trim(); _workspace.UpdateSettings(settings);
            images = images.OrderBy(image => AnimeImageSourceService.GetImageFileName(image), new EpisodeNameComparer()).ToArray();
            var imageSets = AnimeImageSourceService.GroupImages(images, settings.AnimeImageGroupByPrefix);
            foreach (var imageSet in imageSets)
            {
                var firstImage = imageSet[0];
                var body = new StackPanel { Spacing = 0 };
                foreach (var image in imageSet)
                {
                    var name = AnimeImageSourceService.GetImageFileName(image);
                    var selected = new CheckBox { Content = name, IsChecked = true };
                    var preview = new Button { Content = Strings.AnimeImagesPreview.GetLocalizedResource(), MinWidth = 64 };
                    preview.Click += (_, _) => { _previewImage.Source = new BitmapImage(image.Url) { DecodePixelWidth = 1600 }; _previewOverlay.Visibility = Visibility.Visible; };
                    var imageRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 6, 12, 6) };
                    imageRow.ColumnDefinitions.Add(new ColumnDefinition());
                    imageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    selected.VerticalAlignment = VerticalAlignment.Center;
                    ToolTipService.SetToolTip(selected, image.Url.AbsoluteUri);
                    imageRow.Children.Add(selected); Grid.SetColumn(preview, 1); imageRow.Children.Add(preview);
                    body.Children.Add(imageRow);
                    selected.Checked += (_, _) => UpdateSave(); selected.Unchecked += (_, _) => UpdateSave(); _choices.Add((selected, image));
                }
                if (settings.AnimeImageGroupByPrefix)
                {
                    var setHeader = new Grid { ColumnSpacing = 12 };
                    setHeader.ColumnDefinitions.Add(new ColumnDefinition());
                    setHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    setHeader.Children.Add(new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesSetTitle.GetLocalizedResource(), AnimeImageSourceService.GetImageSetKey(firstImage), imageSet.Count), VerticalAlignment = VerticalAlignment.Center });
                    _rows.Children.Add(new Expander { Header = setHeader, Content = body, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
                else
                    _rows.Children.Add(new Border { Child = body, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] });
            }
            _status.Text = _videos.Length == 0 ? Strings.AnimeImagesNoVideos.GetLocalizedResource() : images.Count == 0 ? (discovered.Count == 0 ? Strings.AnimeImagesEmpty.GetLocalizedResource() : nameMatches == 0 ? Strings.AnimeImagesFilteredNames.GetLocalizedResource() : Strings.AnimeImagesFilteredDimensions.GetLocalizedResource()) : string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesFound.GetLocalizedResource(), images.Count);
            _scanSucceeded = true;
        }
        catch (OperationCanceledException) { _status.Text = Strings.AnimeImagesCancelled.GetLocalizedResource(); }
        catch (Exception ex) { _status.Text = Strings.AnimeImagesFailed.GetLocalizedResource() + " " + ex.Message; }
        finally { Busy(false); }
    }
    private async Task SaveAsync()
    {
        if (VideoMode) { await SaveVideosAsync(); return; }
        if (_allAdded) return;
        if (_targetVideo.SelectedIndex < 0) return;
        var file = _videos[_targetVideo.SelectedIndex];
        var number = AnimeLibraryService.GetIllustrationNumber(file, _videos);
        var selected = _choices.Where(row => row.Selected.IsEnabled && row.Selected.IsChecked == true).ToArray();
        _operation?.Dispose(); _operation = new(); Busy(true); var saved = 0; var skipped = 0; var failed = 0;
        try
        {
            foreach (var row in selected)
            {
                _operation.Token.ThrowIfCancellationRequested();
                try
                {
                    var image = new AnimeSourceImage("illustration", row.Image.Url, number, row.Image.Label);
                    var path = await _service.ImportAsync(image, _workspace.LibraryPath, _folder, file, number, _operation.Token, _sourceNumber);
                    if (path is null) skipped++; else { saved++; HasChanges = true; }
                    row.Selected.IsChecked = false;
                    row.Selected.IsEnabled = false;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed++; ToolTipService.SetToolTip(row.Selected, ex.Message); }
                _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesResult.GetLocalizedResource(), saved, skipped, failed);
            }
            _allAdded = _choices.Count > 0 && _choices.All(row => !row.Selected.IsEnabled);
            if (_allAdded) _status.Text = Strings.AnimeImagesAllAdded.GetLocalizedResource();
        }
        catch (OperationCanceledException) { _status.Text = Strings.AnimeImagesCancelled.GetLocalizedResource() + " " + string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesResult.GetLocalizedResource(), saved, skipped, failed); }
        finally { Busy(false); }
    }
    private async Task ScanVideosAsync()
    {
        _sourceRevision++; _allAdded = false; _scanSucceeded = false;
        _operation?.Dispose(); _operation = new(); _rows.Children.Clear(); _videoChoices.Clear(); Busy(true);
        _status.Text = Strings.AnimeVideosLoading.GetLocalizedResource();
        try
        {
            var address = AnimeImageSourceService.ResolvePageAddress(_source.Text.Trim(), _prefix);
            var result = await _videoService.InspectPageAsync(address, _workspace.Settings.AnimeVideoMinimumSeconds, _operation.Token);
            var settings = _workspace.Settings.Clone(); settings.AnimeImageSource = _source.Text.Trim(); _workspace.UpdateSettings(settings);
            foreach (var video in result.Videos)
            {
                var selected = new CheckBox { Content = video.Name, IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(selected, video.Url.AbsoluteUri);
                var duration = new TextBlock { Text = video.DurationSeconds is double seconds ? string.Format(CultureInfo.CurrentCulture, Strings.AnimeVideosDuration.GetLocalizedResource(), seconds) : Strings.AnimeVideosUnknownDuration.GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Center };
                var row = new Grid { Padding = new Thickness(12, 8, 12, 8), ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                selected.MaxWidth = 500; row.Children.Add(selected); Grid.SetColumn(duration, 1); row.Children.Add(duration);
                _rows.Children.Add(row); _videoChoices.Add((selected, video));
                selected.Checked += (_, _) => UpdateSave(); selected.Unchecked += (_, _) => UpdateSave();
            }
            _scanSucceeded = true;
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeVideosFound.GetLocalizedResource(), result.Videos.Count, result.TooShort, result.UnknownDuration);
        }
        catch (OperationCanceledException) { _status.Text = Strings.AnimeImagesCancelled.GetLocalizedResource(); }
        catch (Exception ex) { _status.Text = Strings.AnimeImagesFailed.GetLocalizedResource() + " " + ex.Message; }
        finally { Busy(false); }
    }

    private async Task SaveVideosAsync()
    {
        var selected = _videoChoices.Where(row => row.Selected.IsEnabled && row.Selected.IsChecked == true).ToArray();
        if (selected.Length == 0) return;
        _operation?.Dispose(); _operation = new(); Busy(true); var saved = 0; var failed = 0;
        try
        {
            var referer = new Uri(AnimeImageSourceService.ResolvePageAddress(_source.Text.Trim(), _prefix));
            foreach (var row in selected)
            {
                _operation.Token.ThrowIfCancellationRequested();
                try
                {
                    var progress = new Progress<long>(bytes => { if (IsBusy && row.Selected.IsEnabled) _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeVideosDownloading.GetLocalizedResource(), row.Video.Name, bytes / 1048576d); });
                    await _videoService.DownloadAsync(row.Video, _workspace.LibraryPath, _folder, referer, _workspace.Settings.AnimeVideoMinimumSeconds, progress, _operation.Token);
                    saved++; HasChanges = true; row.Selected.IsChecked = false; row.Selected.IsEnabled = false;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed++; ToolTipService.SetToolTip(row.Selected, ex.Message); }
                _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeVideosResult.GetLocalizedResource(), saved, failed);
            }
            _allAdded = _videoChoices.Count > 0 && _videoChoices.All(row => !row.Selected.IsEnabled);
            if (_allAdded) _status.Text = Strings.AnimeVideosAllDownloaded.GetLocalizedResource();
        }
        catch (OperationCanceledException) { _status.Text = Strings.AnimeImagesCancelled.GetLocalizedResource(); }
        catch (Exception ex) { _status.Text = Strings.AnimeImagesFailed.GetLocalizedResource() + " " + ex.Message; }
        finally { ReloadTargets(); Busy(false); }
    }

}
