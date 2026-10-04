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
    private readonly StackPanel _rows = new() { Spacing = 12 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _scan = new() { Content = Strings.AnimeImagesScan.GetLocalizedResource() };
    private readonly Button _save = new() { Content = Strings.AnimeImagesSave.GetLocalizedResource(), IsEnabled = false };
    private readonly Button _cancel = new() { Content = Strings.Cancel.GetLocalizedResource(), Visibility = Visibility.Collapsed };
    private readonly Button _close = ResourceDialogPresentation.CreateIconButton("\uE8BB", Strings.Close.GetLocalizedResource());
    private readonly List<(CheckBox Selected, ComboBox Kind, ComboBox Video, AnimeImageSourceService.PageImage Image)> _choices = [];
    private CancellationTokenSource? _operation;
    public bool IsBusy { get; private set; }
    public bool HasChanges { get; private set; }
    public event EventHandler? RequestClose;

    public AnimeImageImportDialog(string folder, IResourceWorkspaceService workspace)
    {
        _folder = folder; _workspace = workspace;
        _videos = Directory.EnumerateFiles(folder).Where(file => workspace.Settings.VideoExtensions.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file, new EpisodeNameComparer()).ToArray();
        _source = new TextBox { Text = workspace.Settings.AnimeImageSource, PlaceholderText = "https://example.org/anime", HorizontalAlignment = HorizontalAlignment.Stretch };
        var grid = new Grid { RowSpacing = 12 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) grid.RowDefinitions.Add(new RowDefinition { Height = height });
        var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = Strings.AnimeImagesImport.GetLocalizedResource(), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_close, 1); header.Children.Add(_close); grid.Children.Add(header);
        var input = new Grid { ColumnSpacing = 8 }; input.ColumnDefinitions.Add(new ColumnDefinition()); input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); input.Children.Add(_source); Grid.SetColumn(_scan, 1); input.Children.Add(_scan); Grid.SetRow(input, 1); grid.Children.Add(input);
        var help = new TextBlock { Text = Strings.AnimeImagesHelp.GetLocalizedResource(), TextWrapping = TextWrapping.Wrap }; Grid.SetRow(help, 2); grid.Children.Add(help);
        var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 3); grid.Children.Add(scroll);
        var footer = new Grid { ColumnSpacing = 12 }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(_status); Grid.SetColumn(_cancel, 1); footer.Children.Add(_cancel); Grid.SetColumn(_save, 2); footer.Children.Add(_save); Grid.SetRow(footer, 4); grid.Children.Add(footer);
        _previewOverlay.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        _previewOverlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _previewOverlay.RowDefinitions.Add(new RowDefinition());
        var closePreview = ResourceDialogPresentation.CreateIconButton("\uE8BB", Strings.Close.GetLocalizedResource());
        closePreview.HorizontalAlignment = HorizontalAlignment.Right;
        closePreview.Click += (_, _) => { _previewOverlay.Visibility = Visibility.Collapsed; _previewImage.Source = null; };
        _previewOverlay.Children.Add(closePreview); Grid.SetRow(_previewImage, 1); _previewOverlay.Children.Add(_previewImage);
        Grid.SetRowSpan(_previewOverlay, 5); grid.Children.Add(_previewOverlay);
        Content = grid;
        _scan.Click += async (_, _) => await ScanAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) => _operation?.Cancel();
        _close.Click += (_, _) => { if (!IsBusy) RequestClose?.Invoke(this, EventArgs.Empty); };
        Unloaded += (_, _) => _operation?.Cancel();
    }

    private void Busy(bool busy)
    {
        IsBusy = busy; _source.IsEnabled = _scan.IsEnabled = _close.IsEnabled = !busy;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _rows.IsHitTestVisible = !busy; UpdateSave();
    }
    private void UpdateSave() => _save.IsEnabled = !IsBusy && _videos.Length > 0 && _choices.Any(row => row.Selected.IsChecked == true) && _choices.Where(row => row.Selected.IsChecked == true).All(row => row.Video.SelectedIndex >= 0);
    private async Task ScanAsync()
    {
        _operation?.Dispose(); _operation = new(); Busy(true); _rows.Children.Clear(); _choices.Clear(); _status.Text = Strings.AnimeImagesLoading.GetLocalizedResource();
        try
        {
            var address = AnimeImageSourceService.ResolvePageAddress(_source.Text, _workspace.Settings.AnimeImageWebsitePrefix);
            var discovered = await _service.InspectPageAsync(address, _operation.Token);
            var images = AnimeImageSourceService.FilterImages(discovered, _workspace.Settings.AnimeImageIncludedNames);
            var settings = _workspace.Settings.Clone(); settings.AnimeImageSource = _source.Text.Trim(); _workspace.UpdateSettings(settings);
            foreach (var image in images)
            {
                var selected = new CheckBox { Content = string.IsNullOrWhiteSpace(image.Label) ? Path.GetFileName(image.Url.AbsolutePath) : image.Label };
                var quality = new TextBlock { Text = image.Evidence switch { "declared" => Strings.AnimeImagesDeclared.GetLocalizedResource(), "largest" => Strings.AnimeImagesLargest.GetLocalizedResource(), _ => Strings.AnimeImagesUnknown.GetLocalizedResource() }, TextWrapping = TextWrapping.Wrap };
                var imageAddress = new TextBlock { Text = image.Url.AbsoluteUri, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true }; ToolTipService.SetToolTip(imageAddress, image.Url.AbsoluteUri);
                var kind = new ComboBox { Width = 140 }; kind.Items.Add(Strings.AnimeImagesPoster.GetLocalizedResource()); kind.Items.Add(Strings.AnimeImagesIllustration.GetLocalizedResource()); kind.SelectedIndex = 0;
                var series = new ComboBox { PlaceholderText = Strings.AnimeImagesChooseSeries.GetLocalizedResource(), HorizontalAlignment = HorizontalAlignment.Stretch };
                var groups = _videos.GroupBy(AnimeLibraryService.GetVideoSeriesName).ToArray();
                foreach (var group in groups) series.Items.Add(group.Key);
                var video = new ComboBox { PlaceholderText = Strings.AnimeImagesChooseVideo.GetLocalizedResource(), HorizontalAlignment = HorizontalAlignment.Stretch };
                string[] candidates = [];
                void FillVideos(int index)
                {
                    candidates = index >= 0 ? groups[index].ToArray() : _videos;
                    video.Items.Clear(); foreach (var file in candidates) video.Items.Add(Path.GetFileName(file));
                    var number = AnimeLibraryService.GetEpisodeNumber(image.Label);
                    var matching = candidates.Select((file, position) => (file, position)).Where(entry => AnimeLibraryService.GetEpisodeNumber(entry.file) == number && number is not null).ToArray();
                    video.SelectedIndex = candidates.Length == 1 ? 0 : matching.Length == 1 ? matching[0].position : -1;
                }
                series.SelectionChanged += (_, _) => FillVideos(series.SelectedIndex);
                if (groups.Length == 1) series.SelectedIndex = 0; else FillVideos(-1);
                var assignedVideo = new ComboBox(); foreach (var file in _videos) assignedVideo.Items.Add(file);
                void UpdateAssignment()
                {
                    assignedVideo.SelectedIndex = video.SelectedIndex >= 0 ? Array.IndexOf(_videos, candidates[video.SelectedIndex]) : -1;
                    UpdateSave();
                }
                video.SelectionChanged += (_, _) => UpdateAssignment(); UpdateAssignment();
                var preview = new Button { Content = Strings.AnimeImagesPreview.GetLocalizedResource() };
                preview.Click += (_, _) => { _previewImage.Source = new BitmapImage(image.Url) { DecodePixelWidth = 1600 }; _previewOverlay.Visibility = Visibility.Visible; };
                var actions = new Grid { ColumnSpacing = 8 }; actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                actions.Children.Add(kind); Grid.SetColumn(series, 1); actions.Children.Add(series); Grid.SetColumn(preview, 2); actions.Children.Add(preview);
                var body = new StackPanel { Spacing = 6 }; body.Children.Add(selected); body.Children.Add(quality); body.Children.Add(imageAddress); body.Children.Add(actions); body.Children.Add(video);
                _rows.Children.Add(new Border { Child = body, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] });
                selected.Checked += (_, _) => UpdateSave(); selected.Unchecked += (_, _) => UpdateSave(); _choices.Add((selected, kind, assignedVideo, image));
            }
            _status.Text = _videos.Length == 0 ? Strings.AnimeImagesNoVideos.GetLocalizedResource() : images.Count == 0 ? Strings.AnimeImagesEmpty.GetLocalizedResource() : string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesFound.GetLocalizedResource(), images.Count);
        }
        catch (OperationCanceledException) { _status.Text = Strings.AnimeImagesCancelled.GetLocalizedResource(); }
        catch (Exception ex) { _status.Text = Strings.AnimeImagesFailed.GetLocalizedResource() + " " + ex.Message; }
        finally { Busy(false); }
    }
    private async Task SaveAsync()
    {
        var selected = _choices.Where(row => row.Selected.IsChecked == true && row.Video.SelectedIndex >= 0).ToArray();
        _operation?.Dispose(); _operation = new(); Busy(true); var saved = 0; var skipped = 0; var failed = 0;
        try
        {
            foreach (var row in selected)
            {
                _operation.Token.ThrowIfCancellationRequested();
                var file = _videos[row.Video.SelectedIndex];
                var number = AnimeLibraryService.GetIllustrationNumber(file, _videos);
                try
                {
                    var image = new AnimeSourceImage(row.Kind.SelectedIndex == 0 ? "poster" : "illustration", row.Image.Url, number, row.Image.Label);
                    var path = await _service.ImportAsync(image, _workspace.LibraryPath, _folder, file, number, _operation.Token);
                    if (path is null) skipped++; else { saved++; HasChanges = true; }
                    row.Selected.IsChecked = false;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed++; ToolTipService.SetToolTip(row.Selected, ex.Message); }
                _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesResult.GetLocalizedResource(), saved, skipped, failed);
            }
        }
        catch (OperationCanceledException) { _status.Text = Strings.AnimeImagesCancelled.GetLocalizedResource() + " " + string.Format(CultureInfo.CurrentCulture, Strings.AnimeImagesResult.GetLocalizedResource(), saved, skipped, failed); }
        finally { Busy(false); }
    }
}
