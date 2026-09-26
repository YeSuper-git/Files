// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Items.ResourceManager;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Files.App.UserControls.FilePreviews;

public sealed partial class ActorPosterPreview : UserControl
{
    private readonly ResourceActorListedItem _actor;
    private readonly List<string> _posterPaths;
    public Visibility PosterActionsVisibility { get; }
    private int _currentIndex;
    private bool _isPointerOverPoster;
    private Storyboard? _posterTransition;

    public ActorPosterPreview(ResourceActorListedItem actor, bool showPosterActions = true)
    {
        _actor = actor;
        PosterActionsVisibility = showPosterActions ? Visibility.Visible : Visibility.Collapsed;
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

    private async Task LoadCurrentPosterAsync(int transitionOffset = 0)
    {
        if (_posterPaths.Count == 0)
        {
            PosterImage.Source = null;
            PosterTranslateTransform.X = 0;
            PosterImage.Opacity = 1;
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
            if (transitionOffset != 0)
                AnimatePosterChange(transitionOffset);
            else
            {
                _posterTransition?.Stop();
                PosterTranslateTransform.X = 0;
                PosterImage.Opacity = 1;
            }
            EmptyPosterText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            _posterTransition?.Stop();
            PosterTranslateTransform.X = 0;
            PosterImage.Opacity = 1;
            PosterImage.Source = null;
            EmptyPosterText.Text = "海报无法读取";
            EmptyPosterText.Visibility = Visibility.Visible;
        }

        UpdateControls();
    }

    private void UpdateControls()
    {
        var hasMultiplePosters = _posterPaths.Count > 1;
        var navigationVisibility = hasMultiplePosters && _isPointerOverPoster ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.Visibility = navigationVisibility;
        NextButton.Visibility = navigationVisibility;
        var hasCurrentPoster = _posterPaths.Count > 0;
        AddPosterMenuItem.IsEnabled = _actor.AddActorPosterAsync is not null;
        var currentPosterIsMain = hasCurrentPoster &&
            string.Equals(_posterPaths.ElementAtOrDefault(_currentIndex), _actor.MainPosterPath, StringComparison.OrdinalIgnoreCase);
        SetMainPosterMenuItem.IsEnabled = hasCurrentPoster && !currentPosterIsMain && _actor.SetActorMainPosterAsync is not null;
        DeletePosterMenuItem.IsEnabled = hasCurrentPoster && _actor.DeleteActorPosterAsync is not null;
        SaveOriginalMenuItem.IsEnabled = hasCurrentPoster;
        PosterCountText.Text = _posterPaths.Count == 0 ? "0 / 0" : $"{_currentIndex + 1} / {_posterPaths.Count}";
    }

    private void PosterViewport_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPoster = true;
        UpdateControls();
    }

    private void AnimatePosterChange(int transitionOffset)
    {
        _posterTransition?.Stop();

        PosterTranslateTransform.X = transitionOffset;
        PosterImage.Opacity = 0.78;

        var slide = new DoubleAnimation
        {
            From = transitionOffset,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, PosterTranslateTransform);
        Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.X));

        var fade = new DoubleAnimation
        {
            From = 0.78,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, PosterImage);
        Storyboard.SetTargetProperty(fade, nameof(Opacity));

        _posterTransition = new Storyboard();
        _posterTransition.Children.Add(slide);
        _posterTransition.Children.Add(fade);
        _posterTransition.Completed += (_, _) =>
        {
            PosterTranslateTransform.X = 0;
            PosterImage.Opacity = 1;
        };
        _posterTransition.Begin();
    }

    private void PosterViewport_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverPoster = false;
        UpdateControls();
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_posterPaths.Count < 2)
            return;

        _currentIndex = (_currentIndex - 1 + _posterPaths.Count) % _posterPaths.Count;
        await LoadCurrentPosterAsync(transitionOffset: -36);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_posterPaths.Count < 2)
            return;

        _currentIndex = (_currentIndex + 1) % _posterPaths.Count;
        await LoadCurrentPosterAsync(transitionOffset: 36);
    }

    private async void AddPosterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_actor.AddActorPosterAsync is not { } addActorPosterAsync)
            return;

        var path = await addActorPosterAsync();
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!_posterPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            _posterPaths.Add(path);

        _currentIndex = _posterPaths.FindIndex(candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));
        await LoadCurrentPosterAsync(transitionOffset: -28);
    }

    private async void SetMainPosterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_actor.SetActorMainPosterAsync is not { } setMainPosterAsync || _posterPaths.Count == 0)
            return;

        var path = _posterPaths[_currentIndex];
        if (string.Equals(path, _actor.MainPosterPath, StringComparison.OrdinalIgnoreCase))
            return;

        await setMainPosterAsync(path);
        _actor.MainPosterPath = path;
        UpdateControls();
    }

    private async void DeletePosterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_actor.DeleteActorPosterAsync is not { } deleteActorPosterAsync || _posterPaths.Count == 0)
            return;

        var deletedIndex = _currentIndex;
        var deletedPath = _posterPaths[deletedIndex];
        await deleteActorPosterAsync(deletedPath);
        _posterPaths.RemoveAt(deletedIndex);
        _actor.ActorPosterPaths = _actor.ActorPosterPaths
            .Where(path => !string.Equals(path, deletedPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _currentIndex = _posterPaths.Count == 0 ? 0 : Math.Min(deletedIndex, _posterPaths.Count - 1);
        await LoadCurrentPosterAsync(transitionOffset: 28);
    }

    private async void SaveOriginalMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_posterPaths.Count == 0)
            return;

        var sourcePath = _posterPaths[_currentIndex];
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
            return;

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = Path.GetFileName(sourcePath),
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeChoices.Add("图片", [extension]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);

            var destination = await picker.PickSaveFileAsync();
            if (destination is null || string.Equals(destination.Path, sourcePath, StringComparison.OrdinalIgnoreCase))
                return;

            var source = await StorageFile.GetFileFromPathAsync(sourcePath);
            await source.CopyAndReplaceAsync(destination);
        }
        catch (Exception ex)
        {
            App.Logger.LogWarning(ex, "Unable to save original actor poster from {PosterPath}", sourcePath);
        }
    }
}
