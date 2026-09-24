// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Items.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Windows.Storage;

namespace Files.App.UserControls.FilePreviews;

public sealed partial class ActorPosterPreview : UserControl
{
    private readonly ResourceActorListedItem _actor;
    private readonly List<string> _posterPaths;
    private int _currentIndex;

    public ActorPosterPreview(ResourceActorListedItem actor)
    {
        _actor = actor;
        _posterPaths = actor.ActorPosterPaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mainPosterPath = actor.MainPosterPath;
        if (!string.IsNullOrWhiteSpace(mainPosterPath) &&
            File.Exists(mainPosterPath) &&
            !_posterPaths.Contains(mainPosterPath, StringComparer.OrdinalIgnoreCase))
        {
            _posterPaths.Insert(0, mainPosterPath);
        }

        InitializeComponent();
        _currentIndex = Math.Max(0, _posterPaths.FindIndex(path =>
            string.Equals(path, actor.MainPosterPath, StringComparison.OrdinalIgnoreCase)));
        UpdateControls();
    }

    public Task LoadAsync() => LoadCurrentPosterAsync();

    private async Task LoadCurrentPosterAsync()
    {
        if (_posterPaths.Count == 0)
        {
            PosterImage.Source = null;
            EmptyPosterText.Visibility = Visibility.Visible;
            UpdateControls();
            return;
        }

        var path = _posterPaths[_currentIndex];
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var image = new BitmapImage { DecodePixelWidth = 960 };
            await image.SetSourceAsync(stream);
            PosterImage.Source = image;
            EmptyPosterText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            PosterImage.Source = null;
            EmptyPosterText.Text = "海报无法读取";
            EmptyPosterText.Visibility = Visibility.Visible;
        }

        UpdateControls();
    }

    private void UpdateControls()
    {
        var hasMultiplePosters = _posterPaths.Count > 1;
        PreviousButton.Visibility = hasMultiplePosters ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = hasMultiplePosters ? Visibility.Visible : Visibility.Collapsed;
        SetMainPosterButton.IsEnabled = _posterPaths.Count > 0 &&
            !string.Equals(_posterPaths.ElementAtOrDefault(_currentIndex), _actor.MainPosterPath, StringComparison.OrdinalIgnoreCase);
        SetMainPosterButton.Content = SetMainPosterButton.IsEnabled ? "设为主海报" : "当前主海报";
        PosterCountText.Text = _posterPaths.Count == 0 ? "0 / 0" : $"{_currentIndex + 1} / {_posterPaths.Count}";
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_posterPaths.Count < 2)
            return;

        _currentIndex = (_currentIndex - 1 + _posterPaths.Count) % _posterPaths.Count;
        await LoadCurrentPosterAsync();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_posterPaths.Count < 2)
            return;

        _currentIndex = (_currentIndex + 1) % _posterPaths.Count;
        await LoadCurrentPosterAsync();
    }

    private async void AddPosterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_actor.AddActorPosterAsync is not { } addActorPosterAsync)
            return;

        var path = await addActorPosterAsync();
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!_posterPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            _posterPaths.Add(path);

        _currentIndex = _posterPaths.FindIndex(candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));
        await LoadCurrentPosterAsync();
    }

    private async void SetMainPosterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_actor.SetActorMainPosterAsync is not { } setMainPosterAsync || _posterPaths.Count == 0)
            return;

        var path = _posterPaths[_currentIndex];
        await setMainPosterAsync(path);
        _actor.MainPosterPath = path;
        UpdateControls();
    }
}
