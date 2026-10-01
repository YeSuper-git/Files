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
using System.Diagnostics;
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
	private bool _isSeeking;
	private double _timelineZoom = 1;
	private bool _playButtonHovered;
	private string _playGraphicName = "Play";
	private double _storyboardSampleInterval;
	private readonly Dictionary<double, BitmapImage> _storyboardImages = [];
	private VideoSegment[] _storyboardSegments = [];
	private string? _storyboardImagePath;
	private readonly Dictionary<(double Start, double End), Image> _storyboardTiles = [];

	public VideoEditorViewModel ViewModel { get; }
	private readonly SemaphoreSlim _videoLoadGate = new(1, 1);
	public ObservableCollection<VideoCutJob> ExportingJobs { get; } = [];

	public VideoEditorPage()
	{
		ViewModel = Ioc.Default.GetRequiredService<VideoEditorViewModel>();
		InitializeComponent();
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

		await AddVideoFilesAsync(await picker.PickMultipleFilesAsync());
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

		var deferral = e.GetDeferral();
		try
		{
			var files = (await e.DataView.GetStorageItemsAsync())
				.OfType<StorageFile>()
				.Where(item => SupportedVideoExtensions.Contains(System.IO.Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase)).ToArray();
			if (files.Length == 0)
			{
				ViewModel.SetStatusMessage(Strings.VideoEditorDroppedFileUnsupported.GetLocalizedResource());
				return;
			}

			await AddVideoFilesAsync(files);
		}
		finally { deferral.Complete(); }
	}

	private async Task AddVideoFilesAsync(IEnumerable<StorageFile> files)
	{
		var videos = files.ToArray();
		foreach (var file in videos)
			ViewModel.AddPendingVideo(file.Path);
		SelectQueueTab("Pending");
		UpdateEmptyStates();
		if (!ViewModel.HasVideo && videos.Length > 0)
			await LoadVideoFileAsync(videos[0]);
	}

	private async void PendingVideo_Click(object sender, ItemClickEventArgs e)
	{
		if (e.ClickedItem is not PendingVideo video)
			return;
		await _videoLoadGate.WaitAsync();
		try
		{
			if (!string.Equals(ViewModel.SourcePath, video.SourcePath, StringComparison.OrdinalIgnoreCase))
			{
				await ViewModel.LoadVideoAsync(video.SourcePath);
				if (string.Equals(ViewModel.SourcePath, video.SourcePath, StringComparison.OrdinalIgnoreCase))
					await AttachPreviewAsync(video.SourcePath);
			}
		}
		finally { _videoLoadGate.Release(); }
	}

	private async Task LoadVideoFileAsync(StorageFile? file)
	{
		if (file is null)
			return;
		await _videoLoadGate.WaitAsync();
		try
		{
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
		finally { _videoLoadGate.Release(); }
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
			SeekTo(ViewModel.CurrentPositionSeconds);
			_playbackTimer.Start();
			_storyboardSampleInterval = 0;
			SetTimelineZoom(TimelineViewport.ActualWidth / Math.Max(1, ViewModel.DurationSeconds * 120));
			TimelineViewport.ChangeView(0, null, null, disableAnimation: true);
			StartStoryboardLoad(path);
		}
		catch (Exception ex)
		{
			ViewModel.SetStatusMessage(string.Format(Strings.VideoEditorPreviewFailed.GetLocalizedResource(), ex.Message));
		}
	}

	private async void Page_Loaded(object sender, RoutedEventArgs e)
	{
		ViewModel.PendingVideos.CollectionChanged -= Jobs_CollectionChanged;
		ViewModel.PendingVideos.CollectionChanged += Jobs_CollectionChanged;
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
		ViewModel.SavePendingDraft();
		ViewModel.PendingVideos.CollectionChanged -= Jobs_CollectionChanged;
		_playbackTimer.Stop();
		ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
		foreach (var job in ViewModel.ProcessingJobs)
			job.PropertyChanged -= Job_PropertyChanged;
		ViewModel.SourceReserved -= ViewModel_SourceReserved;
		ViewModel.SourceReservationReleased -= ViewModel_SourceReservationReleased;
		ViewModel.CompletedJobs.CollectionChanged -= CompletedJobs_CollectionChanged;
		ViewModel.ProcessingJobs.CollectionChanged -= Jobs_CollectionChanged;
		ViewModel.CompletedJobs.CollectionChanged -= Jobs_CollectionChanged;
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

	private void UpdateEmptyStates()
	{
		if (PendingEmptyState is null)
			return;

		SyncQueueGroups();
		PendingEmptyState.Visibility = ViewModel.PendingVideos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		ProcessingEmptyState.Visibility = ExportingJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		CompletedEmptyState.Visibility = ViewModel.CompletedJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		UpdateCount(PendingCount, PendingBadge, ViewModel.PendingVideos.Count);
		UpdateCount(ProcessingCount, ProcessingBadge, ExportingJobs.Count);
		UpdateCount(CompletedCount, CompletedBadge, ViewModel.CompletedJobs.Count);
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
		SyncGroup(ExportingJobs, ViewModel.ProcessingJobs);
	}

	private static void UpdateCount(TextBlock label, Border badge, int count)
	{
		label.Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
		badge.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
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

	private static IEnumerable<VideoSegment> MergeAdjacentSegments(IEnumerable<VideoSegment> segments)
	{
		VideoSegment? merged = null;
		foreach (var segment in segments)
		{
			if (merged is not null && merged.EndSeconds == segment.StartSeconds)
				merged = new VideoSegment(merged.StartSeconds, segment.EndSeconds);
			else
			{
				if (merged is not null) yield return merged;
				merged = segment;
			}
		}
		if (merged is not null) yield return merged;
	}
	private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(VideoEditorViewModel.Segments))
		{
			if (_storyboardPath == ViewModel.SourcePath && MergeAdjacentSegments(_storyboardSegments).SequenceEqual(MergeAdjacentSegments(ViewModel.Segments)))
			{
				_storyboardSegments = ViewModel.Segments.ToArray();
				UpdateStoryboardVisuals();
				return;
			}
			StopStoryboard(clearCache: false);
			SetTimelineZoom(_timelineZoom);
			if (ViewModel.SourcePath is { } path) StartStoryboardLoad(path);
			DrawTimelineRuler();
			UpdateStoryboardVisuals();
		}
		if (e.PropertyName == nameof(VideoEditorViewModel.HasVideo))
			UpdateEmptyStates();
		if (e.PropertyName == nameof(VideoEditorViewModel.IsLoading))
			UpdateEmptyStates();
		if (e.PropertyName == nameof(VideoEditorViewModel.DurationSeconds))
		{
			SetTimelineZoom(_timelineZoom);
			DrawTimelineRuler();
		}
		if (e.PropertyName == nameof(VideoEditorViewModel.CurrentPositionSeconds))
			UpdatePlayhead();
	}

	private void PlaybackTimer_Tick(object? sender, object e)
	{
		if (Player.MediaPlayer is null || !ViewModel.HasVideo)
			return;

		var session = Player.MediaPlayer.PlaybackSession;
		if (!_isSeeking && session.PlaybackState == MediaPlaybackState.Playing)
		{
			var timelinePosition = ViewModel.SourceToTimeline(session.Position.TotalSeconds);
			var sourcePosition = ViewModel.TimelineToSource(timelinePosition);
			if (Math.Abs(sourcePosition - session.Position.TotalSeconds) > 0.08)
				session.Position = TimeSpan.FromSeconds(sourcePosition);
			if (timelinePosition >= ViewModel.DurationSeconds - 0.02) Player.MediaPlayer.Pause();
		}
		if (!_isSeeking)
			ViewModel.CurrentPositionSeconds = ViewModel.SourceToTimeline(session.Position.TotalSeconds);
		var playing = session.PlaybackState == MediaPlaybackState.Playing;
		UpdatePlayGraphic();
		var playLabel = playing ? Strings.VideoEditorPause.GetLocalizedResource() : Strings.VideoEditorPlay.GetLocalizedResource();
		ToolTipService.SetToolTip(PlayButton, playLabel);
		AutomationProperties.SetName(PlayButton, playLabel);
	}

	private void PlayButton_PointerEntered(object sender, PointerRoutedEventArgs e)
	{
		_playButtonHovered = true;
		UpdatePlayGraphic();
	}

	private void PlayButton_PointerExited(object sender, PointerRoutedEventArgs e)
	{
		_playButtonHovered = false;
		UpdatePlayGraphic();
	}

	private void UpdatePlayGraphic()
	{
		var playing = Player.MediaPlayer?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
		var name = (playing ? "Pause" : "Play") + (_playButtonHovered ? "Hover" : string.Empty);
		if (_playGraphicName == name) return;
		_playGraphicName = name;
		PlayGraphicSource.UriSource = new Uri($"ms-appx:///Assets/VideoEditor/{name}.svg");
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

	private void RestoreTrim_Click(object sender, RoutedEventArgs e)
	{
		ViewModel.RestoreTrim();
		SeekTo(0);
	}

	private void SplitVideo_Click(object sender, RoutedEventArgs e)
	{
		Player.MediaPlayer?.Pause();
		ViewModel.SplitAtPlayhead();
		SeekTo(ViewModel.CurrentPositionSeconds);
	}

	private void Workspace_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
	{
		if (!ViewModel.HasVideo || sender is not FrameworkElement target) return;
		var menu = new MenuFlyout();
		AddMenuAction(menu, Strings.VideoEditorCloseVideo.GetLocalizedResource(), "\uE8BB", () => CloseVideo_Click(target, new RoutedEventArgs()));
		AddMenuAction(menu, Strings.VideoEditorChangeVideo.GetLocalizedResource(), "\uE8E5", async () =>
		{
			var picker = new FileOpenPicker();
			foreach (var extension in SupportedVideoExtensions) picker.FileTypeFilter.Add(extension);
			InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
			var file = await picker.PickSingleFileAsync();
			if (file is null) return;
			var previous = ViewModel.PendingVideos.FirstOrDefault(video => string.Equals(video.SourcePath, ViewModel.SourcePath, StringComparison.OrdinalIgnoreCase));
			await LoadVideoFileAsync(file);
			if (previous is not null && string.Equals(ViewModel.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase) && !string.Equals(previous.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase))
				ViewModel.PendingVideos.Remove(previous);
		});
		ShowContextMenu(menu, target, e);
	}

	private void Segment_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
	{
		if (sender is not FrameworkElement target || target.Tag is not VideoSegment segment) return;
		var menu = new MenuFlyout();
		void Apply(bool keepOnly)
		{
			Player.MediaPlayer?.Pause();
			var sourcePosition = ViewModel.TimelineToSource(ViewModel.CurrentPositionSeconds);
			if (keepOnly) ViewModel.KeepOnlySegment(segment); else ViewModel.RemoveSegment(segment);
			SeekTo(ViewModel.SourceToTimeline(sourcePosition));
		}
		AddMenuAction(menu, Strings.VideoEditorRemoveSegment.GetLocalizedResource(), "\uE74D", () => Apply(false));
		AddMenuAction(menu, Strings.VideoEditorKeepOnlySegment.GetLocalizedResource(), "\uE73E", () => Apply(true));
		ShowContextMenu(menu, target, e);
	}

	private void CloseVideo_Click(object sender, RoutedEventArgs e)
	{
		var pending = ViewModel.PendingVideos.FirstOrDefault(video => string.Equals(video.SourcePath, ViewModel.SourcePath, StringComparison.OrdinalIgnoreCase));
		StopStoryboard();
		ReleasePlayerSource();
		ViewModel.CloseVideo();
		if (pending is not null) ViewModel.PendingVideos.Remove(pending);
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
		if (sender is not FrameworkElement target || !ViewModel.CanSave)
			return;
		var path = ViewModel.SourcePath;
		var segments = ViewModel.Segments.ToArray();
		var flyout = new Flyout { Placement = FlyoutPlacementMode.Top };
		var content = new StackPanel { Spacing = 12, MaxWidth = 300 };
		content.Children.Add(new TextBlock { Text = Strings.VideoEditorReplaceConfirmation.GetLocalizedResource(), TextWrapping = TextWrapping.Wrap });
		var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
		var cancel = new Button { Content = Strings.VideoEditorCancelExport.GetLocalizedResource() };
		cancel.Click += (_, _) => flyout.Hide();
		var confirm = new Button { Content = Strings.VideoEditorReplaceAndSave.GetLocalizedResource(), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
		confirm.Click += (_, _) =>
		{
			flyout.Hide();
			if (ViewModel.CanSave && ViewModel.SourcePath == path && ViewModel.Segments.SequenceEqual(segments))
			{
				ViewModel.ExportCurrent(replaceOriginal: true);
				SelectQueueTab("Processing");
			}
		};
		actions.Children.Add(cancel);
		actions.Children.Add(confirm);
		content.Children.Add(actions);
		flyout.Content = content;
		DispatcherQueue.TryEnqueue(() => flyout.ShowAt(ExportButton));
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
			SeekTo(0);
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

	private int _storyboardFirstIndex = -1;
	private double _storyboardSecondsPerTile;
	private void Storyboard_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		DrawTimelineRuler();
		UpdateStoryboardVisuals();
		if (ViewModel.SourcePath is { } path)
			StartStoryboardLoad(path);
	}

	private void TimelineViewport_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
	{
		DrawTimelineRuler();
		if (ViewModel.SourcePath is { } path)
			StartStoryboardLoad(path);
	}

	private void StartStoryboardLoad(string path)
	{
		// Width is set before the next layout pass; ActualWidth can still describe the previous zoom.
		var width = StoryboardSurface.Width;
		if (!double.IsFinite(width)) width = StoryboardOverlay.ActualWidth;
		var duration = ViewModel.DurationSeconds;
		if (duration <= 0) { StoryboardStrip.Children.Clear(); _storyboardTiles.Clear(); return; }
		if (width <= 0) return;
		if (!string.Equals(_storyboardImagePath, path, StringComparison.OrdinalIgnoreCase))
		{
			StoryboardStrip.Children.Clear();
			_storyboardTiles.Clear();
			_storyboardImages.Clear();
			_storyboardSampleInterval = 0;
			_storyboardImagePath = path;
		}
		var tileWidth = 54 * ViewModel.VideoAspectRatio;
		var pixelsPerSecond = width / duration;
		if (_storyboardSampleInterval <= 0 || _storyboardSampleInterval * pixelsPerSecond < tileWidth / 2)
			_storyboardSampleInterval = tileWidth / pixelsPerSecond;
		var firstIndex = (int)(TimelineViewport.HorizontalOffset / tileWidth);
		var frameCount = (int)Math.Ceiling(TimelineViewport.ActualWidth / tileWidth);
		if (_storyboardPath == path && _storyboardFirstIndex == firstIndex && _storyboardFrameCount == frameCount
			&& Math.Abs(_storyboardSecondsPerTile - pixelsPerSecond) < 0.00001 && _storyboardSegments.SequenceEqual(ViewModel.Segments)) return;
		StopStoryboard(clearCache: false);
		_storyboardPath = path;
		_storyboardFirstIndex = firstIndex;
		_storyboardFrameCount = frameCount;
		_storyboardSecondsPerTile = pixelsPerSecond;
		_storyboardSegments = ViewModel.Segments.ToArray();
		_storyboardCancellation = new CancellationTokenSource();
		var missing = new List<(Image Image, double Seconds)>();
		var wanted = new HashSet<(double Start, double End)>();
		var visibleStart = Math.Max(0, (TimelineViewport.HorizontalOffset - tileWidth * 2) / pixelsPerSecond);
		var visibleEnd = (TimelineViewport.HorizontalOffset + TimelineViewport.ActualWidth + tileWidth * 2) / pixelsPerSecond;
		double offset = 0;
		foreach (var segment in _storyboardSegments)
		{
			var sourceStart = segment.StartSeconds + Math.Max(0, visibleStart - offset);
			var sourceEnd = Math.Min(segment.EndSeconds, segment.StartSeconds + visibleEnd - offset);
			for (var cell = Math.Floor(sourceStart / _storyboardSampleInterval); cell * _storyboardSampleInterval < sourceEnd; cell++)
			{
				var start = Math.Max(segment.StartSeconds, cell * _storyboardSampleInterval);
				var end = Math.Min(segment.EndSeconds, (cell + 1) * _storyboardSampleInterval);
				if (end <= start) continue;
				var seconds = Math.Round(Math.Min((cell + 0.5) * _storyboardSampleInterval, Math.Max(0, ViewModel.SourceDurationSeconds - 0.001)), 6);
				var key = (start, end);
				wanted.Add(key);
				if (!_storyboardTiles.TryGetValue(key, out var image))
				{
					image = new Image { Height = 54, Stretch = Stretch.UniformToFill };
					_storyboardTiles[key] = image;
				}
				image.Width = (end - start) * pixelsPerSecond;
				image.Stretch = image.Width > tileWidth + 0.5 ? Stretch.Uniform : Stretch.UniformToFill;
				Canvas.SetLeft(image, (offset + start - segment.StartSeconds) * pixelsPerSecond);
				if (_storyboardImages.TryGetValue(seconds, out var cached))
				{
					if (!ReferenceEquals(image.Source, cached)) image.Source = cached;
				}
				else
				{
					if (_storyboardImages.Count > 0)
						image.Source = _storyboardImages.MinBy(frame => Math.Abs(frame.Key - seconds)).Value;
					missing.Add((image, seconds));
				}
				if (!StoryboardStrip.Children.Contains(image)) StoryboardStrip.Children.Add(image);
			}
			offset += segment.DurationSeconds;
		}
		foreach (var key in _storyboardTiles.Keys.Where(key => !wanted.Contains(key)).ToArray())
		{
			StoryboardStrip.Children.Remove(_storyboardTiles[key]);
			_storyboardTiles.Remove(key);
		}
		_ = LoadStoryboardFramesAsync(path, missing, _storyboardCancellation.Token);
		UpdateStoryboardVisuals();
	}

	private async Task LoadStoryboardFramesAsync(string path, List<(Image Image, double Seconds)> frames, CancellationToken cancellationToken)
	{
		if (frames.Count == 0) { StoryboardStrip.Opacity = 1; return; }
		var initial = _storyboardImages.Count == 0;
		if (initial) StoryboardStrip.Opacity = 0;
		try
		{
			using var gate = new SemaphoreSlim(4);
			var results = await Task.WhenAll(frames.Select(async frame =>
			{
				await gate.WaitAsync(cancellationToken);
				try { return (frame.Image, frame.Seconds, Path: await _frameStripService.GetFrameAsync(path, frame.Seconds, 0, cancellationToken)); }
				finally { gate.Release(); }
			}));
			cancellationToken.ThrowIfCancellationRequested();
			var prepared = await Task.WhenAll(results.Where(result => result.Path is not null).Select(async result =>
			{
				var file = await StorageFile.GetFileFromPathAsync(result.Path!);
				using var stream = await file.OpenReadAsync();
				var bitmap = new BitmapImage();
				await bitmap.SetSourceAsync(stream);
				return (result.Image, result.Seconds, Bitmap: bitmap);
			}));
			cancellationToken.ThrowIfCancellationRequested();
			foreach (var frame in prepared)
			{
				_storyboardImages[frame.Seconds] = frame.Bitmap;
				frame.Image.Source = frame.Bitmap;
			}			StoryboardStrip.Opacity = 1;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception ex)
		{
			if (!cancellationToken.IsCancellationRequested) StoryboardStrip.Opacity = 1;
			App.Logger.LogWarning(ex, "Unable to load storyboard frames for {Path}", path);
		}
	}
	private void StopStoryboard(bool clearCache = true)
	{
		_storyboardCancellation?.Cancel();
		_storyboardCancellation?.Dispose();
		_storyboardCancellation = null;
		_storyboardPath = null;
		_storyboardFrameCount = 0;
		if (clearCache)
		{
			StoryboardStrip.Children.Clear();
			StoryboardStrip.Opacity = 1;
			_storyboardTiles.Clear();
			_storyboardImages.Clear();
			_storyboardImagePath = null;
			_storyboardSampleInterval = 0;
		}
	}
	private void UpdateStoryboardVisuals()
	{
		if (StoryboardOverlay is null) return;
		SegmentOverlay.Children.Clear();
		var width = StoryboardOverlay.ActualWidth;
		var duration = ViewModel.DurationSeconds;
		StoryboardPlayhead.Visibility = duration > 0 ? Visibility.Visible : Visibility.Collapsed;
		if (duration <= 0 || width <= 0) return;
		double offset = 0;
		foreach (var segment in ViewModel.Segments)
		{
			var clip = new Border { Width = Math.Max(1, segment.DurationSeconds / duration * width - 2), Height = 54, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Tag = segment };
			clip.ContextRequested += Segment_ContextRequested;
			Canvas.SetLeft(clip, offset / duration * width);
			Canvas.SetTop(clip, 86);
			SegmentOverlay.Children.Add(clip);
			offset += segment.DurationSeconds;
		}
		UpdatePlayhead();
	}

	private void UpdatePlayhead()
	{
		var duration = ViewModel.DurationSeconds;
		var width = StoryboardOverlay.ActualWidth;
		if (duration > 0 && width > 0)
			Canvas.SetLeft(StoryboardPlayhead, Math.Clamp(ViewModel.CurrentPositionSeconds / duration * width, 0, width) - 8);
	}
	private void Storyboard_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (!ViewModel.HasVideo || StoryboardOverlay.ActualWidth <= 0 || !e.GetCurrentPoint(StoryboardOverlay).Properties.IsLeftButtonPressed)
			return;
		for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, StoryboardOverlay); element = VisualTreeHelper.GetParent(element))
			if (element is Thumb)
				return;
		var ratio = Math.Clamp(e.GetCurrentPoint(StoryboardOverlay).Position.X / StoryboardOverlay.ActualWidth, 0, 1);
		_isSeeking = true;
		StoryboardOverlay.CapturePointer(e.Pointer);
		SeekTo(ratio * ViewModel.DurationSeconds);
		e.Handled = true;
	}

	private async void CompletedVideo_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not VideoCutJob job || job.OutputPath is not { } path)
			return;
		e.Handled = true;
		await OpenCompletedVideoAsync(path);
	}

	private async Task OpenCompletedVideoAsync(string path)
	{
		try { await LoadVideoFileAsync(await StorageFile.GetFileFromPathAsync(path)); }
		catch (Exception ex) { ShowFileActionError(ex); }
	}

	private void PendingVideo_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
	{
		if (sender is not FrameworkElement target || target.DataContext is not PendingVideo video)
			return;
		var menu = new MenuFlyout();
		AddMenuAction(menu, Strings.VideoEditorCancelExport.GetLocalizedResource(), "\uE8BB", () =>
		{
			if (string.Equals(ViewModel.SourcePath, video.SourcePath, StringComparison.OrdinalIgnoreCase))
				CloseVideo_Click(target, new RoutedEventArgs());
			ViewModel.PendingVideos.Remove(video);
		});
		AddMenuAction(menu, Strings.VideoEditorOpenFolder.GetLocalizedResource(), "\uE838", () => OpenVideoFolder(video.SourcePath));
		ShowContextMenu(menu, target, e);
	}

	private void CompletedVideo_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
	{
		if (sender is not FrameworkElement target || target.DataContext is not VideoCutJob job || job.OutputPath is not { } path)
			return;
		var menu = new MenuFlyout();
		AddMenuAction(menu, Strings.VideoEditorPlay.GetLocalizedResource(), "\uE768", () =>
		{
			try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
			catch (Exception ex) { ShowFileActionError(ex); }
		});
		AddMenuAction(menu, Strings.VideoEditorOpenFolder.GetLocalizedResource(), "\uE838", () => OpenVideoFolder(path));
		AddMenuAction(menu, Strings.Rename.GetLocalizedResource(), "\uE8AC", async () => await RenameCompletedVideoAsync(job));
		menu.Items.Add(new MenuFlyoutSeparator());
		AddMenuAction(menu, Strings.VideoEditorDeleteRecord.GetLocalizedResource(), "\uE74D", () => ViewModel.CompletedJobs.Remove(job));
		ShowContextMenu(menu, target, e);
	}

	private static void AddMenuAction(MenuFlyout menu, string label, string glyph, Action action)
	{
		var item = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph } };
		item.Click += (_, _) => action();
		menu.Items.Add(item);
	}

	private static void ShowContextMenu(MenuFlyout menu, FrameworkElement target, ContextRequestedEventArgs e)
	{
		if (e.TryGetPosition(target, out var point))
			menu.ShowAt(target, point);
		else
			menu.ShowAt(target);
		e.Handled = true;
	}

	private void OpenVideoFolder(string path)
	{
		try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = true }); }
		catch (Exception ex) { ShowFileActionError(ex); }
	}

	private void ShowFileActionError(Exception ex)
		=> ViewModel.SetStatusMessage(string.Format(Strings.VideoEditorFileActionFailed.GetLocalizedResource(), ex.Message));

	private async Task RenameCompletedVideoAsync(VideoCutJob job)
	{
		if (job.OutputPath is not { } path)
			return;
		var nameBox = new TextBox { Text = Path.GetFileNameWithoutExtension(path), MinWidth = 260 };
		var dialog = new ContentDialog
		{
			XamlRoot = XamlRoot,
			Title = Strings.Rename.GetLocalizedResource(),
			Content = nameBox,
			PrimaryButtonText = Strings.Rename.GetLocalizedResource(),
			CloseButtonText = Strings.VideoEditorCancelExport.GetLocalizedResource(),
			DefaultButton = ContentDialogButton.Primary
		};
		try
		{
			if (await dialog.ShowAsync() != ContentDialogResult.Primary)
				return;
			var stem = nameBox.Text.Trim();
			if (stem.Length == 0 || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || stem is "." or "..")
				throw new IOException(Strings.RenameErrorNameInvalidText.GetLocalizedResource());
			if (ViewModel.IsSourceReserved(path))
				throw new IOException(Strings.VideoEditorDuplicateVideo.GetLocalizedResource());
			var wasOpen = string.Equals(ViewModel.SourcePath, path, StringComparison.OrdinalIgnoreCase);
			ViewModel.SavePendingDraft();
			if (wasOpen)
			{
				StopStoryboard();
				ReleasePlayerSource();
			}
			var file = await StorageFile.GetFileFromPathAsync(path);
			try { await file.RenameAsync(stem + Path.GetExtension(path), NameCollisionOption.FailIfExists); }
			catch
			{
				if (wasOpen) await AttachPreviewAsync(path);
				throw;
			}
			foreach (var record in ViewModel.CompletedJobs.Where(record => string.Equals(record.OutputPath, path, StringComparison.OrdinalIgnoreCase)))
				record.OutputPath = file.Path;
			var pending = ViewModel.PendingVideos.FirstOrDefault(video => string.Equals(video.SourcePath, path, StringComparison.OrdinalIgnoreCase));
			if (pending is not null)
			{
				var renamed = ViewModel.AddPendingVideo(file.Path);
				renamed.TrimStartSeconds = pending.TrimStartSeconds;
				renamed.TrimEndSeconds = pending.TrimEndSeconds;
				renamed.CurrentPositionSeconds = pending.CurrentPositionSeconds;
				renamed.Segments = pending.Segments;
				ViewModel.PendingVideos.Remove(pending);
			}
			if (wasOpen) await LoadVideoFileAsync(file);
		}
		catch (Exception ex) { ShowFileActionError(ex); }
	}

	private void Storyboard_PointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (_isSeeking && e.GetCurrentPoint(StoryboardOverlay).Properties.IsLeftButtonPressed)
			SeekTo(Math.Clamp(e.GetCurrentPoint(StoryboardOverlay).Position.X / Math.Max(1, StoryboardOverlay.ActualWidth), 0, 1) * ViewModel.DurationSeconds);
	}

	private void Storyboard_PointerReleased(object sender, PointerRoutedEventArgs e)
	{
		_isSeeking = false;
		StoryboardOverlay.ReleasePointerCapture(e.Pointer);
	}

	private void GoToStart_Click(object sender, RoutedEventArgs e) => SeekTo(0);
	private void ZoomIn_Click(object sender, RoutedEventArgs e) { _storyboardSampleInterval = 0; SetTimelineZoom(_timelineZoom * 1.5); }
	private void ZoomOut_Click(object sender, RoutedEventArgs e) { _storyboardSampleInterval = 0; SetTimelineZoom(_timelineZoom / 1.5); }
	private void ZoomFit_Click(object sender, RoutedEventArgs e) { _storyboardSampleInterval = 0; SetTimelineZoom(TimelineViewport.ActualWidth / Math.Max(1, ViewModel.DurationSeconds * 120)); }
	private void TimelineViewport_SizeChanged(object sender, SizeChangedEventArgs e) => SetTimelineZoom(_timelineZoom);

	private void SetTimelineZoom(double zoom)
	{
		var naturalWidth = Math.Max(1, ViewModel.DurationSeconds * 120);
		var fitZoom = Math.Min(1, TimelineViewport.ActualWidth / naturalWidth);
		_timelineZoom = Math.Clamp(zoom, Math.Max(0.00001, fitZoom), 16);
		if (StoryboardSurface is not null && TimelineViewport.ActualWidth > 0)
			StoryboardSurface.Width = Math.Max(TimelineViewport.ActualWidth - 2, naturalWidth * _timelineZoom);
	}

	private void DrawTimelineRuler()
	{
		if (TimelineRuler is null)
			return;
		TimelineRuler.Children.Clear();
		var duration = ViewModel.DurationSeconds;
		var width = StoryboardOverlay.ActualWidth;
		if (duration <= 0 || width <= 0)
			return;
		var targetStep = duration * 180 / width;
		double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
		var step = steps.FirstOrDefault(value => value >= targetStep, Math.Ceiling(targetStep / 3600) * 3600);
		var visibleStart = TimelineViewport.HorizontalOffset / width * duration;
		var visibleEnd = Math.Min(duration, (TimelineViewport.HorizontalOffset + TimelineViewport.ActualWidth + 30) / width * duration);
		for (var seconds = Math.Max(step / 4, Math.Floor(visibleStart / (step / 4)) * (step / 4)); seconds < visibleEnd; seconds += step / 4)
		{
			var x = seconds / duration * width;
			var major = Math.Abs(seconds / step - Math.Round(seconds / step)) < 0.001;
			if (major)
			{
				var label = new TextBlock { Text = VideoEditorViewModel.FormatTimelineTime(seconds), FontSize = 10, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
				Canvas.SetLeft(label, x - 14);
				Canvas.SetTop(label, 5);
				TimelineRuler.Children.Add(label);
			}
			else
			{
				var dot = new Border { Width = 3, Height = 3, CornerRadius = new CornerRadius(2), Background = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] };
				Canvas.SetLeft(dot, x - 1.5);
				Canvas.SetTop(dot, 12);
				TimelineRuler.Children.Add(dot);
			}
		}
	}

	private void SeekTo(double seconds)
	{
		seconds = Math.Clamp(seconds, 0, ViewModel.DurationSeconds);
		ViewModel.CurrentPositionSeconds = seconds;
		if (Player.MediaPlayer is not null && ViewModel.HasVideo)
			Player.MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(ViewModel.TimelineToSource(seconds));
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
