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
    private readonly string[] _videos;
    private readonly Grid _previewOverlay = new() { Visibility = Visibility.Collapsed };
    private readonly Image _previewImage = new() { Stretch = Stretch.Uniform };
    private readonly TextBox _source;
    private readonly string _prefix;
    private readonly StackPanel _rows = new() { Spacing = 12 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _scan = new() { Content = Strings.AnimeImagesScan.GetLocalizedResource() };
    private readonly Button _save = new() { Content = Strings.AnimeImagesConfirm.GetLocalizedResource(), IsEnabled = false };
    private readonly ComboBox _targetVideo = new() { PlaceholderText = Strings.AnimeImagesChooseVideo.GetLocalizedResource(), Width = 260, MaxDropDownHeight = 360 };
    private readonly Button _cancel = new() { Content = Strings.Cancel.GetLocalizedResource(), Visibility = Visibility.Collapsed };
    private readonly Button _close = ResourceDialogPresentation.CreateIconButton("\uE8BB", Strings.Close.GetLocalizedResource());
    private readonly List<(CheckBox Selected, AnimeImageSourceService.PageImage Image)> _choices = [];
    private readonly CheckBox _selectAll = new() { Content = Strings.AnimeImagesSelectSet.GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Center, IsEnabled = false };
    private bool _updatingSelection;
    private bool _allAdded;
    private string? _sourceNumber;
    private CancellationTokenSource? _operation;
    public bool IsBusy { get; private set; }
    public bool HasChanges { get; private set; }
    public event EventHandler? RequestClose;

    public AnimeImageImportDialog(string folder, IResourceWorkspaceService workspace, string? selectedVideo = null)
    {
        _folder = folder; _workspace = workspace;
        _videos = Directory.EnumerateFiles(folder).Where(file => workspace.Settings.VideoExtensions.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file, new EpisodeNameComparer()).ToArray();
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
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) grid.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = Strings.AnimeImagesImport.GetLocalizedResource(), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_close, 1); header.Children.Add(_close); grid.Children.Add(header);
        var input = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left }; foreach (var _ in new[] { 0, 1, 2 }) input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); input.Children.Add(prefixLabel); Grid.SetColumn(_source, 1); input.Children.Add(_source); Grid.SetColumn(_scan, 2); input.Children.Add(_scan); Grid.SetRow(input, 1); grid.Children.Add(input);
        var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 2); grid.Children.Add(scroll);
        Grid.SetRow(_status, 3); grid.Children.Add(_status);
        var footer = new Grid { ColumnSpacing = 12 };
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        footer.Children.Add(_selectAll); Grid.SetColumn(_cancel, 2); footer.Children.Add(_cancel); Grid.SetColumn(_targetVideo, 3); footer.Children.Add(_targetVideo); Grid.SetColumn(_save, 4); footer.Children.Add(_save); Grid.SetRow(footer, 4); grid.Children.Add(footer);
        _previewOverlay.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        _previewOverlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _previewOverlay.RowDefinitions.Add(new RowDefinition());
        var closePreview = ResourceDialogPresentation.CreateIconButton("\uE8BB", Strings.Close.GetLocalizedResource());
        closePreview.HorizontalAlignment = HorizontalAlignment.Right;
        closePreview.Click += (_, _) => { _previewOverlay.Visibility = Visibility.Collapsed; _previewImage.Source = null; };
        _previewOverlay.Children.Add(closePreview); Grid.SetRow(_previewImage, 1); _previewOverlay.Children.Add(_previewImage);
        Grid.SetRowSpan(_previewOverlay, 5); grid.Children.Add(_previewOverlay);
        Content = grid;
        _selectAll.Click += (_, _) =>
        {
            var select = _selectAll.IsChecked == true;
            _updatingSelection = true;
            foreach (var row in _choices.Where(row => row.Selected.IsEnabled)) row.Selected.IsChecked = select;
            _updatingSelection = false;
            UpdateSave();
        };
        _scan.Click += async (_, _) => await ScanAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        _close.Click += (_, _) => { if (!IsBusy) RequestClose?.Invoke(this, EventArgs.Empty); };
        Unloaded += (_, _) => _operation?.Cancel();
    }

    private void Busy(bool busy)
    {
        IsBusy = busy; _source.IsEnabled = _scan.IsEnabled = _close.IsEnabled = _targetVideo.IsEnabled = !busy;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _rows.IsHitTestVisible = !busy; UpdateSave();
    }
    private void UpdateSave()
    {
        if (_updatingSelection) return;
        var remaining = _choices.Where(row => row.Selected.IsEnabled).ToArray();
        _selectAll.IsEnabled = !IsBusy && remaining.Length > 0;
        _selectAll.IsChecked = remaining.Length > 0 && remaining.All(row => row.Selected.IsChecked == true) ? true
            : remaining.Any(row => row.Selected.IsChecked == true) ? null : false;
        _save.IsEnabled = !IsBusy && (_allAdded || (_targetVideo.SelectedIndex >= 0 && remaining.Any(row => row.Selected.IsChecked == true)));
    }
    private async Task ScanAsync()
    {
        _allAdded = false;
        _operation?.Dispose(); _operation = new(); Busy(true); _rows.Children.Clear(); _choices.Clear(); _status.Text = Strings.AnimeImagesLoading.GetLocalizedResource();
        try
        {
            var suffix = _source.Text.Trim();
            _sourceNumber = System.Text.RegularExpressions.Regex.IsMatch(suffix, @"^[0-9]{5}$") ? suffix : null;
            var address = AnimeImageSourceService.ResolvePageAddress(suffix, _prefix);
            var discovered = await _service.InspectPageAsync(address, _operation.Token);
            var images = AnimeImageSourceService.FilterImages(discovered, _workspace.Settings.AnimeImageIncludedNames);
            var nameMatches = images.Count;
            images = await _service.FilterDimensionsAsync(images, _workspace.Settings.AnimeImageMinimumWidth, _workspace.Settings.AnimeImageMinimumHeight, _operation.Token);
            var settings = _workspace.Settings.Clone(); settings.AnimeImageSource = _source.Text.Trim(); _workspace.UpdateSettings(settings);
            images = images.OrderBy(image => AnimeImageSourceService.GetImageFileName(image), new EpisodeNameComparer()).ToArray();
            var imageSets = AnimeImageSourceService.GroupImages(images, settings.AnimeImageGroupByPrefix);
            foreach (var imageSet in imageSets)
            {
                var firstImage = imageSet[0];
                var body = new StackPanel { Spacing = 8 };
                foreach (var image in imageSet)
                {
                    var name = AnimeImageSourceService.GetImageFileName(image);
                    var selected = new CheckBox { Content = name, IsChecked = true };
                    var preview = new Button { Content = Strings.AnimeImagesPreview.GetLocalizedResource() };
                    preview.Click += (_, _) => { _previewImage.Source = new BitmapImage(image.Url) { DecodePixelWidth = 1600 }; _previewOverlay.Visibility = Visibility.Visible; };
                    var imageRow = new Grid { ColumnSpacing = 8 };
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
                    _rows.Children.Add(new Border { Child = body, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] });
            }
            _status.Text = _videos.Length == 0 ? Strings.AnimeImagesNoVideos.GetLocalizedResource() : images.Count == 0 ? (discovered.Count == 0 ? Strings.AnimeImagesEmpty.GetLocalizedResource() : nameMatches == 0 ? Strings.AnimeImagesFilteredNames.GetLocalizedResource() : Strings.AnimeImagesFilteredDimensions.GetLocalizedResource()) : string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesFound.GetLocalizedResource(), images.Count);
        }
        catch (OperationCanceledException) { _status.Text = Strings.AnimeImagesCancelled.GetLocalizedResource(); }
        catch (Exception ex) { _status.Text = Strings.AnimeImagesFailed.GetLocalizedResource() + " " + ex.Message; }
        finally { Busy(false); }
    }
    private async Task SaveAsync()
    {
        if (_allAdded) { RequestClose?.Invoke(this, EventArgs.Empty); return; }
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
}
