// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.VideoEditor;
using Files.App.ViewModels.VideoEditor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Collections.Specialized;
using System.IO;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Files.App.Views.VideoEditor;

public sealed partial class VideoEditorPage : Page
{
	private static readonly string[] SupportedVideoExtensions = [".mp4", ".mkv", ".mov", ".avi", ".m4v", ".ts", ".webm", ".wmv", ".mxf", ".mts", ".m2ts", ".flv", ".vob", ".mpg", ".mpeg", ".3gp", ".ogv"];
	private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
	private readonly List<Rectangle> _keyframeMarkers = [];
	private MediaSource? _mediaSource;
	private bool _isSeeking;
	private bool _seekTrackClick;
	private bool _isUpdatingControls;
	private double _pendingTrimStart;
	private double _pendingTrimEnd;

	public VideoEditorViewModel ViewModel { get; }

	public VideoEditorPage()
	{
		ViewModel = Ioc.Default.GetRequiredService<VideoEditorViewModel>();
		InitializeComponent();
		SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
		SeekSlider.AddHandler(PointerMovedEvent, new PointerEventHandler(SeekSlider_PointerMoved), true);
		SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
		DataContext = ViewModel;
		_playbackTimer.Tick += PlaybackTimer_Tick;
		SelectQueueTab("Processing");
		UpdateEmptyStates();
	}

	private async void OpenVideo_Click(object sender, RoutedEventArgs e)
	{
		var picker = new FileOpenPicker();
		foreach (var extension in SupportedVideoExtensions)
			picker.FileTypeFilter.Add(extension);
		InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);

		await LoadVideoFileAsync(await picker.PickSingleFileAsync());
	}

	private void VideoDropTarget_DragOver(object sender, DragEventArgs e)
	{
		e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
			? DataPackageOperation.Copy
			: DataPackageOperation.None;
	}

	private async void VideoDropTarget_Drop(object sender, DragEventArgs e)
	{
		if (!e.DataView.Contains(StandardDataFormats.StorageItems))
			return;

		var file = (await e.DataView.GetStorageItemsAsync())
			.OfType<StorageFile>()
			.FirstOrDefault(item => SupportedVideoExtensions.Contains(System.IO.Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase));
		if (file is null)
		{
			ViewModel.SetStatusMessage(Strings.VideoEditorDroppedFileUnsupported.GetLocalizedResource());
			return;
		}

		await LoadVideoFileAsync(file);
	}

	private async Task LoadVideoFileAsync(StorageFile? file)
	{
		if (file is null)
			return;
		await ViewModel.LoadVideoAsync(file.Path);
		if (!string.Equals(ViewModel.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase))
		{
			if (!ViewModel.HasVideo)
				ReleasePlayerSource();
			return;
		}

		await AttachPreviewAsync(file.Path);
	}

	private async Task AttachPreviewAsync(string path)
	{
		try
		{
			var file = await StorageFile.GetFileFromPathAsync(path);
			if (!string.Equals(ViewModel.SourcePath, path, StringComparison.OrdinalIgnoreCase))
				return;
			ReleasePlayerSource();
			_mediaSource = MediaSource.CreateFromStorageFile(file);
			Player.Source = _mediaSource;
			SeekSlider.Maximum = Math.Max(0.1, ViewModel.DurationSeconds);
			SeekTo(ViewModel.CurrentPositionSeconds);
			_playbackTimer.Start();
			UpdateTimelineVisuals();
		}
		catch (Exception ex)
		{
			ViewModel.SetStatusMessage(string.Format(Strings.VideoEditorPreviewFailed.GetLocalizedResource(), ex.Message));
		}
	}

	private async void Page_Loaded(object sender, RoutedEventArgs e)
	{
		ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
		ViewModel.PropertyChanged += ViewModel_PropertyChanged;
		ViewModel.KeyframeMarkers.CollectionChanged -= KeyframeMarkers_CollectionChanged;
		ViewModel.KeyframeMarkers.CollectionChanged += KeyframeMarkers_CollectionChanged;
		ViewModel.SourceReserved -= ViewModel_SourceReserved;
		ViewModel.SourceReserved += ViewModel_SourceReserved;
		ViewModel.SourceReservationReleased -= ViewModel_SourceReservationReleased;
		ViewModel.SourceReservationReleased += ViewModel_SourceReservationReleased;
		ViewModel.ProcessingJobs.CollectionChanged -= Jobs_CollectionChanged;
		ViewModel.ProcessingJobs.CollectionChanged += Jobs_CollectionChanged;
		ViewModel.CompletedJobs.CollectionChanged -= Jobs_CollectionChanged;
		ViewModel.CompletedJobs.CollectionChanged += Jobs_CollectionChanged;
		ViewModel.Presets.CollectionChanged -= Presets_CollectionChanged;
		ViewModel.Presets.CollectionChanged += Presets_CollectionChanged;
		ViewModel.CompletedJobs.CollectionChanged -= CompletedJobs_CollectionChanged;
		ViewModel.CompletedJobs.CollectionChanged += CompletedJobs_CollectionChanged;
		ViewModel.NotifySourceReservationChanged();
		foreach (var job in ViewModel.ProcessingJobs)
		{
			job.PropertyChanged -= Job_PropertyChanged;
			job.PropertyChanged += Job_PropertyChanged;
		}
		KeyframeMarkers_CollectionChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		_playbackTimer.Start();
		UpdateTimelineVisuals();
		foreach (var completedJob in ViewModel.CompletedJobs)
		{
			if (ViewModel.InvalidateAfterOutput(completedJob))
			{
				ReleasePlayerSource();
				break;
			}
		}
		if (ViewModel.HasVideo)
			await AttachPreviewAsync(ViewModel.SourcePath!);
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e)
	{
		_playbackTimer.Stop();
		ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
		ViewModel.KeyframeMarkers.CollectionChanged -= KeyframeMarkers_CollectionChanged;
		foreach (var job in ViewModel.ProcessingJobs)
			job.PropertyChanged -= Job_PropertyChanged;
		ViewModel.SourceReserved -= ViewModel_SourceReserved;
		ViewModel.SourceReservationReleased -= ViewModel_SourceReservationReleased;
		ViewModel.CompletedJobs.CollectionChanged -= CompletedJobs_CollectionChanged;
		ViewModel.ProcessingJobs.CollectionChanged -= Jobs_CollectionChanged;
		ViewModel.CompletedJobs.CollectionChanged -= Jobs_CollectionChanged;
		ViewModel.Presets.CollectionChanged -= Presets_CollectionChanged;
		ReleasePlayerSource();
	}

	private void ViewModel_SourceReserved(string path)
	{
		if (!string.Equals(ViewModel.SourcePath, path, StringComparison.OrdinalIgnoreCase))
			return;
		ReleasePlayerSource();
		ViewModel.NotifySourceReservationChanged();
	}

	private async void ViewModel_SourceReservationReleased(string path)
	{
		ViewModel.NotifySourceReservationChanged();
		if (string.Equals(ViewModel.SourcePath, path, StringComparison.OrdinalIgnoreCase) && ViewModel.HasVideo && !ViewModel.IsSourceReserved(path))
			await AttachPreviewAsync(path);
	}

	private void CompletedJobs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		UpdateEmptyStates();
		if (e.NewItems is null)
			return;
		foreach (var job in e.NewItems.OfType<VideoCutJob>())
		{
			if (!job.ReplaceOriginal && string.Equals(ViewModel.SourcePath, job.SourcePath, StringComparison.OrdinalIgnoreCase) && job.OutputPath is { } outputPath)
				ViewModel.SetStatusMessage(string.Format(Strings.VideoEditorExportComplete.GetLocalizedResource(), outputPath));
			if (ViewModel.InvalidateAfterOutput(job))
			{
				ReleasePlayerSource();
				UpdateTimelineVisuals();
				break;
			}
		}
	}

	private void Jobs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (e.OldItems is not null)
			foreach (var job in e.OldItems.OfType<VideoCutJob>())
				job.PropertyChanged -= Job_PropertyChanged;
		if (e.NewItems is not null)
			foreach (var job in e.NewItems.OfType<VideoCutJob>())
				job.PropertyChanged += Job_PropertyChanged;
		UpdateEmptyStates();
	}

	private void Job_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(VideoCutJob.Status))
			UpdateEmptyStates();
	}

	private void Presets_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyStates();

	private void UpdateEmptyStates()
	{
		if (ProcessingEmptyState is null)
			return;

		ProcessingEmptyState.Visibility = ViewModel.ProcessingJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		CompletedEmptyState.Visibility = ViewModel.CompletedJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		ProcessingCount.Text = $"({ViewModel.ProcessingJobs.Count})";
		CompletedCount.Text = $"({ViewModel.CompletedJobs.Count})";
		AllQueueButton.IsEnabled = ViewModel.ProcessingJobs.Any(job => job.Status is VideoCutJobStatus.Waiting or VideoCutJobStatus.Processing or VideoCutJobStatus.Paused);
		AllQueueButton.Content = ViewModel.ProcessingJobs.Any(job => job.Status is VideoCutJobStatus.Waiting or VideoCutJobStatus.Processing)
			? Strings.VideoEditorPauseAll.GetLocalizedResource()
			: Strings.VideoEditorStartAll.GetLocalizedResource();
		QueueSummary.Text = string.Format(
			Strings.VideoEditorQueueSummary.GetLocalizedResource(),
			ViewModel.ProcessingJobs.Count,
			VideoCutJob.FormatTime(ViewModel.ProcessingJobs.Sum(job => job.EndSeconds - job.StartSeconds)));
		EmptyVideoState.Visibility = ViewModel.HasVideo ? Visibility.Collapsed : Visibility.Visible;
		VideoPlayerContainer.Visibility = ViewModel.HasVideo ? Visibility.Visible : Visibility.Collapsed;
		VideoEditorSurface.Visibility = Visibility.Visible;
		VideoLoadingIndicator.IsActive = ViewModel.IsLoading;
		VideoLoadingIndicator.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
	}

	private void QueueTab_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.Tag is string tabName)
			SelectQueueTab(tabName);
	}

	private void SelectQueueTab(string tabName)
	{
		var showProcessing = tabName == "Processing";
		var showCompleted = tabName == "Completed";
		ProcessingTabButton.IsChecked = showProcessing;
		CompletedTabButton.IsChecked = showCompleted;
		ProcessingPanel.Visibility = showProcessing ? Visibility.Visible : Visibility.Collapsed;
		CompletedPanel.Visibility = showCompleted ? Visibility.Visible : Visibility.Collapsed;
	}

	private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(VideoEditorViewModel.HasVideo))
			UpdateEmptyStates();
		if (e.PropertyName == nameof(VideoEditorViewModel.IsLoading))
			UpdateEmptyStates();
		if (e.PropertyName is nameof(VideoEditorViewModel.TrimStartSeconds) or nameof(VideoEditorViewModel.TrimEndSeconds) or nameof(VideoEditorViewModel.DurationSeconds))
			UpdateTimelineVisuals();
		if (e.PropertyName == nameof(VideoEditorViewModel.DurationSeconds))
			SeekSlider.Maximum = Math.Max(0.1, ViewModel.DurationSeconds);
	}

	private void KeyframeMarkers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		foreach (var marker in _keyframeMarkers)
			TimelineCanvas.Children.Remove(marker);
		_keyframeMarkers.Clear();

		var markerBrush = Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var brush)
			? brush as Brush
			: null;
		if (markerBrush is null)
			return;

		foreach (var keyframe in ViewModel.KeyframeMarkers)
		{
			var marker = new Rectangle
			{
				Width = 1,
				Height = 6,
				Fill = markerBrush,
				Opacity = 0.7,
				IsHitTestVisible = false
			};
			_keyframeMarkers.Add(marker);
			TimelineCanvas.Children.Insert(1, marker);
		}
		UpdateTimelineVisuals();
	}

	private void PlaybackTimer_Tick(object? sender, object e)
	{
		if (Player.MediaPlayer is null || !ViewModel.HasVideo)
			return;

		var session = Player.MediaPlayer.PlaybackSession;
		ViewModel.CurrentPositionSeconds = session.Position.TotalSeconds;
		if (!_isSeeking)
		{
			_isUpdatingControls = true;
			SeekSlider.Value = Math.Clamp(session.Position.TotalSeconds, SeekSlider.Minimum, SeekSlider.Maximum);
			_isUpdatingControls = false;
		}
		PlayButton.Content = session.PlaybackState == MediaPlaybackState.Playing
			? Strings.VideoEditorPause.GetLocalizedResource()
			: Strings.VideoEditorPlay.GetLocalizedResource();
		UpdateTimelineVisuals();
	}

	private void PlayPause_Click(object sender, RoutedEventArgs e)
	{
		if (!ViewModel.HasVideo)
			return;

		if (Player.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
			Player.MediaPlayer.Pause();
		else
			Player.MediaPlayer.Play();
	}

	private void Rewind_Click(object sender, RoutedEventArgs e) => SeekTo(ViewModel.CurrentPositionSeconds - 5);

	private void Forward_Click(object sender, RoutedEventArgs e) => SeekTo(ViewModel.CurrentPositionSeconds + 5);

	private void PreviousKeyframe_Click(object sender, RoutedEventArgs e)
		=> SeekTo(ViewModel.FindPreviousKeyframe(ViewModel.CurrentPositionSeconds + 0.001));

	private void NextKeyframe_Click(object sender, RoutedEventArgs e)
		=> SeekTo(ViewModel.FindNextKeyframe(ViewModel.CurrentPositionSeconds));

	private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (!ViewModel.HasVideo)
			return;
		_isSeeking = true;
		_seekTrackClick = !IsWithinSliderThumb(e.OriginalSource as DependencyObject);
		if (_seekTrackClick)
		{
			SeekTo(GetSeekTimeAtPointer(e));
			e.Handled = true;
		}
	}

	private void SeekSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (_isSeeking && _seekTrackClick && e.GetCurrentPoint(SeekSlider).Properties.IsLeftButtonPressed)
			SeekTo(GetSeekTimeAtPointer(e));
	}

	private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
	{
		if (!_isSeeking)
			return;
		var seconds = _seekTrackClick ? GetSeekTimeAtPointer(e) : SeekSlider.Value;
		_isSeeking = false;
		_seekTrackClick = false;
		SeekTo(seconds);
	}

	private double GetSeekTimeAtPointer(PointerRoutedEventArgs e)
	{
		var usableWidth = Math.Max(1, SeekSlider.ActualWidth - 24);
		var ratio = Math.Clamp((e.GetCurrentPoint(SeekSlider).Position.X - 12) / usableWidth, 0, 1);
		return ratio * ViewModel.DurationSeconds;
	}

	private bool IsWithinSliderThumb(DependencyObject? source)
	{
		while (source is not null && !ReferenceEquals(source, SeekSlider))
		{
			if (source is Thumb)
				return true;
			source = VisualTreeHelper.GetParent(source);
		}
		return false;
	}

	private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		if (_isUpdatingControls || Player.MediaPlayer is null || !ViewModel.HasVideo)
			return;
		SeekTo(e.NewValue);
	}

	private void SetTrimStart_Click(object sender, RoutedEventArgs e)
	{
		ViewModel.SetTrimStart(ViewModel.CurrentPositionSeconds);
		UpdateTimelineVisuals();
	}

	private void SetTrimEnd_Click(object sender, RoutedEventArgs e)
	{
		ViewModel.SetTrimEnd(ViewModel.CurrentPositionSeconds);
		UpdateTimelineVisuals();
	}

	private void RestoreTrim_Click(object sender, RoutedEventArgs e)
	{
		ViewModel.RestoreTrim();
		UpdateTimelineVisuals();
	}

	private void CloseVideo_Click(object sender, RoutedEventArgs e)
	{
		ReleasePlayerSource();
		ViewModel.CloseVideo();
		SeekSlider.Maximum = 1;
		SeekSlider.Value = 0;
		UpdateEmptyStates();
		UpdateTimelineVisuals();
	}

	private void AllQueue_Click(object sender, RoutedEventArgs e)
	{
		if (ViewModel.ProcessingJobs.Any(job => job.Status is VideoCutJobStatus.Waiting or VideoCutJobStatus.Processing))
			ViewModel.PauseAll();
		else
			ViewModel.ResumeAll();
		UpdateEmptyStates();
	}

	private void ShowHelp_Click(object sender, RoutedEventArgs e)
		=> ViewModel.SetStatusMessage(Strings.VideoEditorTimelineHint.GetLocalizedResource());

	private void ExportNow_Click(object sender, RoutedEventArgs e)
	{
		ViewModel.ExportCurrent();
	}

	private void ExportLater_Click(object sender, RoutedEventArgs e) => ViewModel.AddCurrentToQueue();

	private void PauseResumeJob_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not VideoCutJob job)
			return;
		if (job.Status == VideoCutJobStatus.Paused)
			ViewModel.Resume(job);
		else
			ViewModel.Pause(job);
		UpdateEmptyStates();
	}

	private async void ReeditJob_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not VideoCutJob job || !await ViewModel.CancelForReeditAsync(job))
			return;
		await ViewModel.LoadVideoAsync(job.SourcePath);
		if (string.Equals(ViewModel.SourcePath, job.SourcePath, StringComparison.OrdinalIgnoreCase))
		{
			ViewModel.ApplyJobRange(job);
			await AttachPreviewAsync(job.SourcePath);
			SeekTo(job.StartSeconds);
		}
	}

	private void CancelJob_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is VideoCutJob job)
			ViewModel.CancelOrRemove(job);
	}

	private void RetryJob_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is VideoCutJob job)
			ViewModel.Retry(job);
	}

	private void ApplyPreset_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not VideoCutPreset preset)
			return;
		ViewModel.ApplyPreset(preset);
		SeekTo(ViewModel.CurrentPositionSeconds);
		UpdateTimelineVisuals();
	}

	private async void DeletePreset_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is VideoCutPreset preset)
			await ViewModel.DeletePresetAsync(preset);
	}

	private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineVisuals();

	private void TimelineCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (!ViewModel.HasVideo || TimelineCanvas.ActualWidth <= 0)
			return;
		var point = e.GetCurrentPoint(TimelineCanvas).Position;
		var seconds = Math.Clamp(point.X / TimelineCanvas.ActualWidth, 0, 1) * ViewModel.DurationSeconds;
		SeekTo(seconds);
	}

	private void TrimStartThumb_DragDelta(object sender, DragDeltaEventArgs e)
	{
		_pendingTrimStart = MoveThumb(TrimStartThumb, e.HorizontalChange);
		UpdateSelectedTrack();
	}

	private void TrimEndThumb_DragDelta(object sender, DragDeltaEventArgs e)
	{
		_pendingTrimEnd = MoveThumb(TrimEndThumb, e.HorizontalChange);
		UpdateSelectedTrack();
	}

	private void TrimStartThumb_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		ViewModel.SetTrimStart(_pendingTrimStart);
		UpdateTimelineVisuals();
	}

	private void TrimEndThumb_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		ViewModel.SetTrimEnd(_pendingTrimEnd);
		UpdateTimelineVisuals();
	}

	private double MoveThumb(Thumb thumb, double horizontalChange)
	{
		var width = TimelineCanvas.ActualWidth;
		if (width <= 0 || ViewModel.DurationSeconds <= 0)
			return 0;
		var left = Math.Clamp(Canvas.GetLeft(thumb) + horizontalChange, 0, Math.Max(0, width - thumb.Width));
		Canvas.SetLeft(thumb, left);
		return Math.Clamp((left + thumb.Width / 2) / width, 0, 1) * ViewModel.DurationSeconds;
	}

	private void UpdateTimelineVisuals()
	{
		if (TimelineCanvas is null)
			return;
		var width = TimelineCanvas.ActualWidth;
		var duration = ViewModel.DurationSeconds;
		TimelineTrack.Width = width;
		if (width <= 0 || duration <= 0)
		{
			SelectedRangeTrack.Width = 0;
			Canvas.SetLeft(TrimStartThumb, 0);
			Canvas.SetLeft(TrimEndThumb, 0);
			Canvas.SetLeft(PlayheadLine, 0);
			return;
		}

		foreach (var pair in _keyframeMarkers.Zip(ViewModel.KeyframeMarkers))
			Canvas.SetLeft(pair.First, Math.Clamp(pair.Second.Seconds / duration * width, 0, width));
		Canvas.SetLeft(TrimStartThumb, Math.Clamp(ViewModel.TrimStartSeconds / duration * width - TrimStartThumb.Width / 2, 0, width - TrimStartThumb.Width));
		Canvas.SetLeft(TrimEndThumb, Math.Clamp(ViewModel.TrimEndSeconds / duration * width - TrimEndThumb.Width / 2, 0, width - TrimEndThumb.Width));
		Canvas.SetLeft(PlayheadLine, Math.Clamp(ViewModel.CurrentPositionSeconds / duration * width, 0, width - PlayheadLine.Width));
		UpdateSelectedTrack();
	}

	private void UpdateSelectedTrack()
	{
		if (ViewModel.DurationSeconds <= 0 || TimelineCanvas.ActualWidth <= 0)
		{
			SelectedRangeTrack.Width = 0;
			return;
		}
		var start = Canvas.GetLeft(TrimStartThumb) + TrimStartThumb.Width / 2;
		var end = Canvas.GetLeft(TrimEndThumb) + TrimEndThumb.Width / 2;
		Canvas.SetLeft(SelectedRangeTrack, start);
		SelectedRangeTrack.Width = Math.Max(0, end - start);
	}

	private void SeekTo(double seconds)
	{
		seconds = Math.Clamp(seconds, 0, ViewModel.DurationSeconds);
		ViewModel.CurrentPositionSeconds = seconds;
		if (Player.MediaPlayer is not null && ViewModel.HasVideo)
			Player.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
		if (!_isSeeking || _seekTrackClick)
		{
			_isUpdatingControls = true;
			SeekSlider.Value = Math.Clamp(seconds, SeekSlider.Minimum, SeekSlider.Maximum);
			_isUpdatingControls = false;
		}
		UpdateTimelineVisuals();
	}

	private void ReleasePlayerSource()
	{
		if (Player.MediaPlayer is not null)
			Player.MediaPlayer.Pause();
		Player.Source = null;
		_mediaSource?.Dispose();
		_mediaSource = null;
	}
}
