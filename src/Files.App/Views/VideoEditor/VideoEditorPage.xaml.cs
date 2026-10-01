// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.VideoEditor;
using Files.App.ViewModels.VideoEditor;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
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
	private readonly VideoFrameStripService _frameStripService = new(Ioc.Default.GetRequiredService<VideoToolchain>());
	private MediaSource? _mediaSource;
	private CancellationTokenSource? _storyboardCancellation;
	private string? _storyboardPath;
	private int _storyboardFrameCount;
	private double _pendingTrimStart;
	private double _pendingTrimEnd;
	private bool _draggingTrimStart;
	private bool _draggingTrimEnd;
	private bool _isSeeking;
	private bool _seekTrackClick;
	private bool _isUpdatingControls;

	public VideoEditorViewModel ViewModel { get; }
	public ObservableCollection<VideoCutJob> PendingJobs { get; } = [];
	public ObservableCollection<VideoCutJob> ExportingJobs { get; } = [];

	public VideoEditorPage()
	{
		ViewModel = Ioc.Default.GetRequiredService<VideoEditorViewModel>();
		InitializeComponent();
		SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
		SeekSlider.AddHandler(PointerMovedEvent, new PointerEventHandler(SeekSlider_PointerMoved), true);
		SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
		DataContext = ViewModel;
		_playbackTimer.Tick += PlaybackTimer_Tick;
		SelectQueueTab("Pending");
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
			{
				StopStoryboard();
				ReleasePlayerSource();
			}
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
			StartStoryboardLoad(path);
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
		_playbackTimer.Start();
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
		UpdateStoryboardVisuals();
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e)
	{
		_playbackTimer.Stop();
		ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
		foreach (var job in ViewModel.ProcessingJobs)
			job.PropertyChanged -= Job_PropertyChanged;
		ViewModel.SourceReserved -= ViewModel_SourceReserved;
		ViewModel.SourceReservationReleased -= ViewModel_SourceReservationReleased;
		ViewModel.CompletedJobs.CollectionChanged -= CompletedJobs_CollectionChanged;
		ViewModel.ProcessingJobs.CollectionChanged -= Jobs_CollectionChanged;
		ViewModel.CompletedJobs.CollectionChanged -= Jobs_CollectionChanged;
		ViewModel.Presets.CollectionChanged -= Presets_CollectionChanged;
		StopStoryboard();
		ReleasePlayerSource();
	}

	private void ViewModel_SourceReserved(string path)
	{
		if (!string.Equals(ViewModel.SourcePath, path, StringComparison.OrdinalIgnoreCase))
			return;
		StopStoryboard();
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
				StopStoryboard();
				ReleasePlayerSource();
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
		if (PendingEmptyState is null)
			return;

		SyncQueueGroups();
		PendingEmptyState.Visibility = PendingJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		ProcessingEmptyState.Visibility = ExportingJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		CompletedEmptyState.Visibility = ViewModel.CompletedJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		PendingCount.Text = $"({PendingJobs.Count})";
		ProcessingCount.Text = $"({ExportingJobs.Count})";
		CompletedCount.Text = $"({ViewModel.CompletedJobs.Count})";
		AllQueueButton.IsEnabled = ViewModel.ProcessingJobs.Any(job => job.Status is VideoCutJobStatus.Waiting or VideoCutJobStatus.Processing or VideoCutJobStatus.Paused);
		var canPause = ViewModel.ProcessingJobs.Any(job => job.Status is VideoCutJobStatus.Waiting or VideoCutJobStatus.Processing);
		var queueAction = canPause
			? Strings.VideoEditorPauseAll.GetLocalizedResource()
			: Strings.VideoEditorStartAll.GetLocalizedResource();
		AllQueueIcon.Glyph = canPause ? "\uE769" : "\uE768";
		ToolTipService.SetToolTip(AllQueueButton, queueAction);
		AutomationProperties.SetName(AllQueueButton, queueAction);
		EmptyVideoState.Visibility = ViewModel.HasVideo ? Visibility.Collapsed : Visibility.Visible;
		VideoPlayerContainer.Visibility = ViewModel.HasVideo ? Visibility.Visible : Visibility.Collapsed;
		StoryboardSurface.Visibility = ViewModel.HasVideo ? Visibility.Visible : Visibility.Collapsed;
		VideoEditorSurface.Visibility = Visibility.Visible;
		VideoLoadingIndicator.IsActive = ViewModel.IsLoading;
		VideoLoadingIndicator.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
	}

	private void SyncQueueGroups()
	{
		SyncGroup(PendingJobs, ViewModel.ProcessingJobs.Where(job => job.Status is VideoCutJobStatus.Waiting or VideoCutJobStatus.Paused or VideoCutJobStatus.Failed or VideoCutJobStatus.Cancelled));
		SyncGroup(ExportingJobs, ViewModel.ProcessingJobs.Where(job => job.Status is VideoCutJobStatus.Processing or VideoCutJobStatus.Replacing));
	}

	private static void SyncGroup(ObservableCollection<VideoCutJob> target, IEnumerable<VideoCutJob> source)
	{
		var wanted = source.ToArray();
		foreach (var job in target.Where(job => !wanted.Contains(job)).ToArray())
			target.Remove(job);
		for (var index = 0; index < wanted.Length; index++)
		{
			if (index < target.Count && ReferenceEquals(target[index], wanted[index]))
				continue;
			if (target.Contains(wanted[index]))
				target.Move(target.IndexOf(wanted[index]), index);
			else
				target.Insert(index, wanted[index]);
		}
	}

	private void QueueTab_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.Tag is string tabName)
			SelectQueueTab(tabName);
	}

	private void SelectQueueTab(string tabName)
	{
		var showPending = tabName == "Pending";
		var showProcessing = tabName == "Processing";
		var showCompleted = tabName == "Completed";
		PendingTabButton.FontWeight = showPending ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
		ProcessingTabButton.FontWeight = showProcessing ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
		CompletedTabButton.FontWeight = showCompleted ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
		PendingTabUnderline.Visibility = showPending ? Visibility.Visible : Visibility.Collapsed;
		ProcessingTabUnderline.Visibility = showProcessing ? Visibility.Visible : Visibility.Collapsed;
		CompletedTabUnderline.Visibility = showCompleted ? Visibility.Visible : Visibility.Collapsed;
		PendingPanel.Visibility = showPending ? Visibility.Visible : Visibility.Collapsed;
		ProcessingPanel.Visibility = showProcessing ? Visibility.Visible : Visibility.Collapsed;
		CompletedPanel.Visibility = showCompleted ? Visibility.Visible : Visibility.Collapsed;
	}

	private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(VideoEditorViewModel.HasVideo))
			UpdateEmptyStates();
		if (e.PropertyName == nameof(VideoEditorViewModel.IsLoading))
			UpdateEmptyStates();
		if (e.PropertyName == nameof(VideoEditorViewModel.DurationSeconds))
			SeekSlider.Maximum = Math.Max(0.1, ViewModel.DurationSeconds);
		if (e.PropertyName is nameof(VideoEditorViewModel.TrimStartSeconds) or nameof(VideoEditorViewModel.TrimEndSeconds) or nameof(VideoEditorViewModel.CurrentPositionSeconds) or nameof(VideoEditorViewModel.DurationSeconds))
			UpdateStoryboardVisuals();
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
	}

	private void SetTrimEnd_Click(object sender, RoutedEventArgs e)
	{
		ViewModel.SetTrimEnd(ViewModel.CurrentPositionSeconds);
	}

	private void RestoreTrim_Click(object sender, RoutedEventArgs e)
	{
		ViewModel.RestoreTrim();
	}

	private void CloseVideo_Click(object sender, RoutedEventArgs e)
	{
		StopStoryboard();
		ReleasePlayerSource();
		ViewModel.CloseVideo();
		SeekSlider.Maximum = 1;
		SeekSlider.Value = 0;
		UpdateEmptyStates();
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

	private void ExportOnly_Click(object sender, RoutedEventArgs e)
	{
		ViewModel.ExportCurrent(replaceOriginal: false);
		SelectQueueTab("Processing");
	}

	private void ReplaceAndExport_Click(object sender, RoutedEventArgs e)
	{
		ViewModel.ExportCurrent(replaceOriginal: true);
		SelectQueueTab("Processing");
	}

	private void PauseResumeJob_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not VideoCutJob job)
			return;
		if (job.Status == VideoCutJobStatus.Paused)
			ViewModel.Resume(job);
		else if (job.Status is VideoCutJobStatus.Failed or VideoCutJobStatus.Cancelled)
			ViewModel.Retry(job);
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
	}

	private async void DeletePreset_Click(object sender, RoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is VideoCutPreset preset)
			await ViewModel.DeletePresetAsync(preset);
	}

	private void Storyboard_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (_storyboardFrameCount > 0)
			foreach (var image in StoryboardStrip.Children.OfType<Image>())
				image.Width = StoryboardOverlay.ActualWidth / _storyboardFrameCount;
		UpdateStoryboardVisuals();
		if (ViewModel.SourcePath is { } path && StoryboardOverlay.ActualWidth > 0)
			StartStoryboardLoad(path);
	}

	private void StartStoryboardLoad(string path)
	{
		var width = StoryboardOverlay.ActualWidth;
		if (width <= 0 || ViewModel.DurationSeconds <= 0)
			return;
		var frameCount = Math.Clamp((int)Math.Ceiling(width / 90), 8, 24);
		if (string.Equals(_storyboardPath, path, StringComparison.OrdinalIgnoreCase) && _storyboardFrameCount == frameCount)
			return;

		StopStoryboard();
		_storyboardPath = path;
		_storyboardFrameCount = frameCount;
		_storyboardCancellation = new CancellationTokenSource();
		var frames = new Image[frameCount];
		for (var index = 0; index < frameCount; index++)
		{
			frames[index] = new Image
			{
				Width = width / frameCount,
				Height = 64,
				Stretch = Stretch.UniformToFill
			};
			StoryboardStrip.Children.Add(frames[index]);
		}
		_ = LoadStoryboardFramesAsync(path, ViewModel.DurationSeconds, frames, _storyboardCancellation.Token);
		UpdateStoryboardVisuals();
	}

	private async Task LoadStoryboardFramesAsync(string path, double duration, Image[] frames, CancellationToken cancellationToken)
	{
		try
		{
			for (var index = 0; index < frames.Length; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var seconds = duration * (index + 0.5) / frames.Length;
				var framePath = await _frameStripService.GetFrameAsync(path, seconds, index, cancellationToken);
				if (framePath is not null && !cancellationToken.IsCancellationRequested)
					frames[index].Source = new BitmapImage(new Uri(framePath));
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception ex)
		{
			App.Logger.LogWarning(ex, "Unable to load storyboard frames for {Path}", path);
		}
	}

	private void StopStoryboard()
	{
		_storyboardCancellation?.Cancel();
		_storyboardCancellation?.Dispose();
		_storyboardCancellation = null;
		_storyboardPath = null;
		_storyboardFrameCount = 0;
		_draggingTrimStart = false;
		_draggingTrimEnd = false;
		StoryboardStrip.Children.Clear();
	}

	private void UpdateStoryboardVisuals(double? startOverride = null, double? endOverride = null)
	{
		if (StoryboardOverlay is null)
			return;
		var width = StoryboardOverlay.ActualWidth;
		var duration = ViewModel.DurationSeconds;
		if (width <= 0 || duration <= 0)
		{
			TrimLeftMask.Width = 0;
			TrimRightMask.Width = 0;
			TrimSelectionOutline.Width = 0;
			return;
		}

		var startSeconds = startOverride ?? (_draggingTrimStart ? _pendingTrimStart : ViewModel.TrimStartSeconds);
		var endSeconds = endOverride ?? (_draggingTrimEnd ? _pendingTrimEnd : ViewModel.TrimEndSeconds);
		var start = Math.Clamp(startSeconds / duration * width, 0, width);
		var end = Math.Clamp(endSeconds / duration * width, start, width);
		TrimLeftMask.Width = start;
		Canvas.SetLeft(TrimLeftMask, 0);
		TrimRightMask.Width = width - end;
		Canvas.SetLeft(TrimRightMask, end);
		TrimSelectionOutline.Width = Math.Max(0, end - start);
		Canvas.SetLeft(TrimSelectionOutline, start);
		Canvas.SetLeft(TrimStartThumb, start - TrimStartThumb.Width / 2);
		Canvas.SetLeft(TrimEndThumb, end - TrimEndThumb.Width / 2);
		Canvas.SetLeft(StoryboardPlayhead, Math.Clamp(ViewModel.CurrentPositionSeconds / duration * width, 0, width) - 1);
	}

	private void Storyboard_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (!ViewModel.HasVideo || StoryboardOverlay.ActualWidth <= 0)
			return;
		for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, StoryboardOverlay); element = VisualTreeHelper.GetParent(element))
			if (element is Thumb)
				return;
		var ratio = Math.Clamp(e.GetCurrentPoint(StoryboardOverlay).Position.X / StoryboardOverlay.ActualWidth, 0, 1);
		SeekTo(ratio * ViewModel.DurationSeconds);
	}

	private void TrimStartThumb_DragDelta(object sender, DragDeltaEventArgs e)
	{
		_draggingTrimStart = true;
		_pendingTrimStart = MoveStoryboardThumb(TrimStartThumb, e.HorizontalChange);
		UpdateStoryboardVisuals(startOverride: _pendingTrimStart);
	}

	private void TrimEndThumb_DragDelta(object sender, DragDeltaEventArgs e)
	{
		_draggingTrimEnd = true;
		_pendingTrimEnd = MoveStoryboardThumb(TrimEndThumb, e.HorizontalChange);
		UpdateStoryboardVisuals(endOverride: _pendingTrimEnd);
	}

	private void TrimStartThumb_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		if (!_draggingTrimStart)
			return;
		_draggingTrimStart = false;
		ViewModel.SetTrimStart(_pendingTrimStart);
		UpdateStoryboardVisuals();
	}

	private void TrimEndThumb_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		if (!_draggingTrimEnd)
			return;
		_draggingTrimEnd = false;
		ViewModel.SetTrimEnd(_pendingTrimEnd);
		UpdateStoryboardVisuals();
	}

	private double MoveStoryboardThumb(Thumb thumb, double horizontalChange)
	{
		var width = StoryboardOverlay.ActualWidth;
		if (width <= 0 || ViewModel.DurationSeconds <= 0)
			return 0;
		var position = Math.Clamp(Canvas.GetLeft(thumb) + thumb.Width / 2 + horizontalChange, 0, width);
		return position / width * ViewModel.DurationSeconds;
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
