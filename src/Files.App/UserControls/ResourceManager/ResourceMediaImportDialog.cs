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

public sealed partial class ResourceMediaImportDialog : UserControl
{
    private readonly MediaImageSourceService _service = new();
    private readonly IResourceWorkspaceService _workspace;
    private readonly string _folder;
    private readonly bool _animeLibrary;
    private string[] _videos;
    private readonly MediaVideoSourceService _videoService = new(Ioc.Default.GetRequiredService<Files.App.Services.VideoEditor.VideoProbeService>());
    private readonly List<(CheckBox Selected, MediaVideoSourceService.PageVideo Video)> _videoChoices = [];
    private readonly Grid _previewOverlay = new() { Visibility = Visibility.Collapsed };
    private readonly Image _previewImage = new() { Stretch = Stretch.Uniform };
    private readonly TextBox _source;
    private string _prefix;
    private readonly ComboBox _prefixSelector = new() { Width = 240, MaxDropDownHeight = 300 };
    private readonly StackPanel _rows = new() { Spacing = 6 };
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
    private readonly List<(CheckBox Selected, MediaImageSourceService.PageImage Image)> _choices = [];
    private readonly CheckBox _selectAll = new() { Content = Strings.AnimeImagesSelectSet.GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Center, IsEnabled = false, Visibility = Visibility.Collapsed };
    private readonly List<(CheckBox Selected, IReadOnlyList<CheckBox> Images)> _setChoices = [];
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

    public ResourceMediaImportDialog(string folder, IResourceWorkspaceService workspace, string? selectedVideo = null, bool animeLibrary = false)
    {
        _folder = folder; _workspace = workspace; _animeLibrary = animeLibrary;
        _videos = MediaRecognitionModule.GetTargets(folder, workspace.Settings, _animeLibrary).ToArray();
        foreach (var video in _videos) _targetVideo.Items.Add(Path.GetFileName(video));
        if (selectedVideo is not null) _targetVideo.SelectedIndex = Array.FindIndex(_videos, video => string.Equals(video, selectedVideo, StringComparison.OrdinalIgnoreCase));
        else if (_videos.Length == 1) _targetVideo.SelectedIndex = 0;
        _targetVideo.SelectionChanged += (_, _) => UpdateSave();
        _prefix = workspace.Settings.AnimeImageWebsitePrefix.Trim().TrimEnd('/') + "/";
        var source = workspace.Settings.AnimeImageSource.Trim();
        if (source.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) source = source[_prefix.Length..];
        else if (Uri.TryCreate(source, UriKind.Absolute, out _)) source = string.Empty;
        _source = new TextBox { Text = source, PlaceholderText = Strings.AnimeImagesSuffix.GetLocalizedResource(), Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var prefix in workspace.Settings.MediaWebsitePrefixes) _prefixSelector.Items.Add(prefix);
        if (_prefixSelector.Items.Count == 0 && !string.IsNullOrWhiteSpace(workspace.Settings.AnimeImageWebsitePrefix)) _prefixSelector.Items.Add(_prefix);
        _prefixSelector.SelectedItem = _prefix;
        if (_prefixSelector.SelectedIndex < 0 && _prefixSelector.Items.Count > 0) _prefixSelector.SelectedIndex = 0;
        _prefixSelector.PlaceholderText = Strings.MediaChoosePrefix.GetLocalizedResource();
        _prefixSelector.BorderThickness = _source.BorderThickness = new Thickness(0);
        _prefixSelector.Background = _source.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var addressFields = new Grid();
        addressFields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addressFields.ColumnDefinitions.Add(new ColumnDefinition());
        addressFields.Children.Add(_prefixSelector); Grid.SetColumn(_source, 1); addressFields.Children.Add(_source);
        var addressBox = new Border { Child = addressFields, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"] };
        _prefixSelector.SelectionChanged += (_, _) =>
        {
            if (_prefixSelector.SelectedItem is not string value) return;
            _prefix = value;
            _sourceRevision++;
            var settings = _workspace.Settings.Clone(); settings.AnimeImageWebsitePrefix = value; _workspace.UpdateSettings(settings);
            _rows.Children.Clear(); _choices.Clear(); _videoChoices.Clear(); _setChoices.Clear(); _scanSucceeded = false; _allAdded = false; _status.Text = string.Empty;
            UpdateSave();
        };
        var grid = new Grid { RowSpacing = 8 };
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
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) input.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        input.Children.Add(addressBox);
        Grid.SetColumn(_recognize, 1); input.Children.Add(_recognize);
        _chromeStatus.VerticalAlignment = VerticalAlignment.Center;
        _chromeStatus.FontSize = 12;
        _chromeStatus.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        Grid.SetColumn(_chromeStatus, 2); input.Children.Add(_chromeStatus);
        Grid.SetRow(input, 1); grid.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_targetVideo); actions.Children.Add(_scan); actions.Children.Add(_scanAndDownload); actions.Children.Add(_save);
        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        Grid.SetRow(actions, 2); grid.Children.Add(actions);
        Grid.SetRow(_selectAll, 3); grid.Children.Add(_selectAll);
        var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 4); grid.Children.Add(scroll);
        var statusRow = new Grid { ColumnSpacing = 12 };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition()); statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.FontSize = 12;
        _status.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
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
            foreach (var selected in SelectAllBoxes().Where(box => box.IsEnabled)) selected.IsChecked = select;
            _updatingSelection = false;
            UpdateSave();
        };
        _source.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            if (!IsBusy && _scan.IsEnabled) await ScanAsync();
        };
        _recognize.Click += async (_, _) => await RecognizeChromeAsync();
        _scan.Click += async (_, _) => await ScanAsync();
        _scanAndDownload.Click += async (_, _) =>
        {
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
        _prefixSelector.IsEnabled = !busy;
        IsBusy = busy; _source.IsEnabled = _scan.IsEnabled = _scanAndDownload.IsEnabled = _close.IsEnabled = _targetVideo.IsEnabled = !busy;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _rows.IsHitTestVisible = !busy; UpdateSave();
    }
    private IEnumerable<CheckBox> SelectionBoxes() => _choices.Select(row => row.Selected).Concat(_videoChoices.Select(row => row.Selected));
    private IEnumerable<CheckBox> SelectAllBoxes() => SelectionBoxes();
    private void ReloadTargets()
    {
        var previous = _targetVideo.SelectedIndex >= 0 && _targetVideo.SelectedIndex < _videos.Length ? _videos[_targetVideo.SelectedIndex] : null;
        try { _videos = MediaRecognitionModule.GetTargets(_folder, _workspace.Settings, _animeLibrary).ToArray(); }
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
        var allBoxes = SelectAllBoxes().Where(box => box.IsEnabled).ToArray();
        _selectAll.IsEnabled = !IsBusy && allBoxes.Length > 0;
        _selectAll.IsChecked = allBoxes.Length > 0 && allBoxes.All(box => box.IsChecked == true) ? true
            : allBoxes.Any(box => box.IsChecked == true) ? null : false;
        foreach (var set in _setChoices)
        {
            var enabled = set.Images.Where(box => box.IsEnabled).ToArray();
            set.Selected.IsEnabled = !IsBusy && enabled.Length > 0;
            set.Selected.IsChecked = enabled.Length > 0 && enabled.All(box => box.IsChecked == true) ? true
                : enabled.Any(box => box.IsChecked == true) ? null : false;
        }
        var hasSelectedImages = _scanSucceeded && remaining.Any(box => box.IsChecked == true);
        _save.Visibility = hasSelectedImages ? Visibility.Visible : Visibility.Collapsed;
        _save.IsEnabled = !IsBusy && hasSelectedImages;
    }
    private async Task ScanAsync()
    {
        _sourceRevision++;
        _allAdded = false;
        _scanSucceeded = false;
        _selectAll.Visibility = Visibility.Collapsed;
        _operation?.Dispose(); _operation = new(); _rows.Children.Clear(); _choices.Clear(); _videoChoices.Clear(); _setChoices.Clear(); Busy(true); _status.Text = Strings.AnimeImagesLoading.GetLocalizedResource();
        Task<MediaVideoSourceService.ScanResult>? videoScan = null;
        try
        {
            var suffix = _source.Text.Trim();
            _sourceNumber = suffix;
            var address = MediaImageSourceService.ResolvePageAddress(suffix, _prefix);
            videoScan = _videoService.InspectPageAsync(address, _workspace.Settings.EffectiveVideoMinimumSeconds, _operation.Token);
            var discovered = await _service.InspectPageAsync(address, _operation.Token);
            var dimensionMatches = await _service.FilterDimensionsAsync(discovered, _workspace.Settings.EffectiveImageMinimumWidth, _workspace.Settings.EffectiveImageMinimumHeight, _operation.Token);
            var images = _workspace.Settings.ImageKeywordRecognitionEnabled == true && _workspace.Settings.AnimeImageIncludedNames.Any(value => !string.IsNullOrWhiteSpace(value))
                ? MediaImageSourceService.FilterImages(dimensionMatches, _workspace.Settings.AnimeImageIncludedNames)
                : dimensionMatches;
            var settings = _workspace.Settings.Clone(); settings.AnimeImageSource = _source.Text.Trim(); _workspace.UpdateSettings(settings);
            images = images.OrderBy(image => MediaImageSourceService.GetImageFileName(image), new EpisodeNameComparer()).ToArray();
            var bestPoster = await _service.SelectLargestPosterAsync(dimensionMatches.Where(MediaImageSourceService.IsPosterImage), _operation.Token);
            var posters = bestPoster is null ? Array.Empty<MediaImageSourceService.PageImage>() : new[] { bestPoster };
            images = images.Where(image => !MediaImageSourceService.IsPosterImage(image)).Concat(posters).ToArray();
            var imageSets = posters.Select(image => (IReadOnlyList<MediaImageSourceService.PageImage>)new[] { image })
                .Concat(MediaImageSourceService.GroupImages(images.Where(image => !MediaImageSourceService.IsPosterImage(image))));
            foreach (var imageSet in imageSets)
            {
                var firstImage = imageSet[0];
                var body = new StackPanel { Spacing = 0 };
                var setBoxes = new List<CheckBox>();
                foreach (var image in imageSet)
                {
                    var name = MediaImageSourceService.GetImageFileName(image);
                    var isPoster = MediaImageSourceService.IsPosterImage(image);
                    var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                    if (isPoster)
                        label.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Strings.MediaPosterImage.GetLocalizedResource() + " · ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    label.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = name });
                    if (isPoster && image.PixelWidth > 0)
                        label.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"  {image.PixelWidth} × {image.PixelHeight}", Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
                    var selected = new CheckBox { Content = label, IsChecked = !isPoster };
                    var preview = new Button { Content = Strings.AnimeImagesPreview.GetLocalizedResource(), MinWidth = 52, Padding = new Thickness(10, 4, 10, 4) };
                    preview.Click += async (_, _) =>
                    {
                        preview.IsEnabled = false;
                        try
                        {
                            var bytes = await _service.ReadPreviewAsync(image.Url, CancellationToken.None);
                            using var stream = new System.IO.MemoryStream(bytes);
                            using var randomAccess = stream.AsRandomAccessStream();
                            var bitmap = new BitmapImage { DecodePixelWidth = 1600 };
                            await bitmap.SetSourceAsync(randomAccess);
                            _previewImage.Source = bitmap; _previewOverlay.Visibility = Visibility.Visible;
                        }
                        catch (Exception ex) { _status.Text = Strings.AnimeImagesFailed.GetLocalizedResource() + " " + ex.Message; }
                        finally { preview.IsEnabled = true; }
                    };
                    var imageRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 6, 12, 6) };
                    imageRow.ColumnDefinitions.Add(new ColumnDefinition());
                    imageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    selected.VerticalAlignment = VerticalAlignment.Center;
                    ToolTipService.SetToolTip(selected, image.Url.AbsoluteUri);
                    imageRow.Children.Add(selected); Grid.SetColumn(preview, 1); imageRow.Children.Add(preview);
                    body.Children.Add(imageRow); setBoxes.Add(selected);
                    selected.Checked += (_, _) => UpdateSave(); selected.Unchecked += (_, _) => UpdateSave(); _choices.Add((selected, image));
                }
                if (imageSet.Count > 1)
                {
                    var setHeader = new Grid { ColumnSpacing = 12 };
                    setHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    setHeader.ColumnDefinitions.Add(new ColumnDefinition());
                    var title = new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesSetTitle.GetLocalizedResource(), MediaImageSourceService.GetImageSetKey(firstImage), imageSet.Count),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(title, 1); setHeader.Children.Add(title);
                    var setSelectAll = new CheckBox { MinWidth = 0, Padding = new Thickness(0), IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(setSelectAll, Strings.AnimeImagesSelectSet.GetLocalizedResource());
                    setHeader.Children.Add(setSelectAll);
                    _setChoices.Add((setSelectAll, setBoxes));
                    setSelectAll.Click += (_, _) =>
                    {
                        _updatingSelection = true;
                        foreach (var box in setBoxes.Where(box => box.IsEnabled)) box.IsChecked = setSelectAll.IsChecked == true;
                        _updatingSelection = false;
                        UpdateSave();
                    };
                    _rows.Children.Add(new Expander { Header = setHeader, Content = body, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
                else
                    _rows.Children.Add(new Border { Child = body, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] });
            }
            _status.Text = _videos.Length == 0 ? Strings.AnimeImagesNoVideos.GetLocalizedResource() : images.Count == 0 ? (discovered.Count == 0 ? Strings.AnimeImagesEmpty.GetLocalizedResource() : dimensionMatches.Count == 0 ? Strings.AnimeImagesFilteredDimensions.GetLocalizedResource() : Strings.AnimeImagesFilteredNames.GetLocalizedResource()) : string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesFound.GetLocalizedResource(), images.Count);
            _scanSucceeded = true;
        }
        catch (OperationCanceledException) { _status.Text = Strings.AnimeImagesCancelled.GetLocalizedResource(); }
        catch (Exception ex) { _status.Text = Strings.AnimeImagesFailed.GetLocalizedResource() + " " + ex.Message; }
        finally
        {
            if (videoScan is not null)
            {
                var videoStatus = await ShowVideosAsync(videoScan);
                _status.Text += "\n" + videoStatus;
                if (!_operation.Token.IsCancellationRequested) _scanSucceeded = true;
            }
            Busy(false);
        }
    }
    private async Task SaveAsync()
    {
        if (_allAdded) return;
        _operation?.Dispose(); _operation = new(); Busy(true);
        var saved = 0; var skipped = 0; var failed = 0;
        try
        {
            var videoResult = await SaveVideosAsync(_operation.Token);
            saved += videoResult.Saved; failed += videoResult.Failed;
            ReloadTargets();
            var selected = _choices.Where(row => row.Selected.IsEnabled && row.Selected.IsChecked == true).ToArray();
            if (selected.Length > 0 && _targetVideo.SelectedIndex < 0)
            {
                _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesResult.GetLocalizedResource(), saved, skipped, failed)
                    + "\n" + Strings.AnimeImagesChooseVideo.GetLocalizedResource();
                return;
            }
            if (selected.Length > 0)
            {
                var file = _videos[_targetVideo.SelectedIndex];
                var number = MediaRecognitionModule.GetImageNumber(file, _videos, _animeLibrary);
                foreach (var row in selected)
                {
                    _operation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var isPoster = MediaImageSourceService.IsPosterImage(row.Image);
                        var image = new AnimeSourceImage(isPoster ? "poster" : "illustration", row.Image.Url, number, row.Image.Label);
                        var path = await _service.ImportAsync(image, _workspace.LibraryPath, _folder, file, number, _operation.Token, _sourceNumber);
                        if (path is null) skipped++; else { if (isPoster) _workspace.SetPosterOverride(file, path); saved++; HasChanges = true; }
                        row.Selected.IsChecked = false; row.Selected.IsEnabled = false;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failed++; ToolTipService.SetToolTip(row.Selected, ex.Message); }
                    _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesResult.GetLocalizedResource(), saved, skipped, failed);
                }
            }
            _allAdded = SelectionBoxes().Any() && SelectionBoxes().All(box => !box.IsEnabled);
            _status.Text = _allAdded ? Strings.AnimeImagesAllAdded.GetLocalizedResource()
                : string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesResult.GetLocalizedResource(), saved, skipped, failed);
        }
        catch (OperationCanceledException) { _status.Text = Strings.AnimeImagesCancelled.GetLocalizedResource() + " " + string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesResult.GetLocalizedResource(), saved, skipped, failed); }
        catch (Exception ex) { _status.Text = Strings.AnimeImagesFailed.GetLocalizedResource() + " " + ex.Message; }
        finally { ReloadTargets(); Busy(false); }
    }
    private async Task<string> ShowVideosAsync(Task<MediaVideoSourceService.ScanResult> scan)
    {
        try
        {
            var result = await scan;
            var insertIndex = Math.Min(_choices.Count(row => MediaImageSourceService.IsPosterImage(row.Image)), _rows.Children.Count);
            foreach (var video in result.Videos)
            {
                var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                label.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Strings.AnimeVideosFiles.GetLocalizedResource() + " · ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                label.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = video.Name });
                var selected = new CheckBox { Content = label, IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(selected, video.Url.AbsoluteUri);
                var duration = new TextBlock { Text = video.DurationSeconds is double seconds ? string.Format(CultureInfo.CurrentCulture, Strings.AnimeVideosDuration.GetLocalizedResource(), seconds) : Strings.AnimeVideosUnknownDuration.GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Center };
                var row = new Grid { Padding = new Thickness(12, 8, 12, 8), ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                selected.MaxWidth = 500; row.Children.Add(selected); if (video.DurationError is not null) ToolTipService.SetToolTip(duration, video.DurationError);
                Grid.SetColumn(duration, 1); row.Children.Add(duration);
                _rows.Children.Insert(insertIndex++, row); _videoChoices.Add((selected, video));
                selected.Checked += (_, _) => UpdateSave(); selected.Unchecked += (_, _) => UpdateSave();
            }
            return string.Format(CultureInfo.CurrentCulture, Strings.AnimeVideosFound.GetLocalizedResource(), result.Videos.Count, result.TooShort, result.UnknownDuration);
        }
        catch (OperationCanceledException) { return Strings.AnimeImagesCancelled.GetLocalizedResource(); }
        catch (Exception ex) { return Strings.AnimeImagesFailed.GetLocalizedResource() + " " + ex.Message; }
    }

    private async Task<(int Saved, int Failed)> SaveVideosAsync(CancellationToken token)
    {
        var selected = _videoChoices.Where(row => row.Selected.IsEnabled && row.Selected.IsChecked == true).ToArray();
        if (selected.Length == 0) return (0, 0);
        var saved = 0; var failed = 0;
        try
        {
            var referer = new Uri(MediaImageSourceService.ResolvePageAddress(_source.Text.Trim(), _prefix));
            foreach (var row in selected)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var progress = new Progress<long>(bytes => { if (IsBusy && row.Selected.IsEnabled) _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeVideosDownloading.GetLocalizedResource(), row.Video.Name, bytes / 1048576d); });
                    await _videoService.DownloadAsync(row.Video, _workspace.LibraryPath, _folder, referer, _workspace.Settings.EffectiveVideoMinimumSeconds, progress, token);
                    saved++; HasChanges = true; row.Selected.IsChecked = false; row.Selected.IsEnabled = false;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed++; ToolTipService.SetToolTip(row.Selected, ex.Message); }
                _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeVideosResult.GetLocalizedResource(), saved, failed);
            }
            return (saved, failed);
        }
        catch (OperationCanceledException) { throw; }
    }

}
