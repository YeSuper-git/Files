// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Files.App.Services.VideoEditor;
using Microsoft.Extensions.Logging;

namespace Files.App.ViewModels.VideoEditor;

public sealed class VideoEditorViewModel : ObservableObject
{
	private readonly VideoProbeService _probeService;
	private readonly VideoToolchain _toolchain;
	private readonly VideoCutQueueService _queueService;
	private readonly IAppSettingsService _settings;
	private VideoMetadata? _metadata;
	private string? _sourcePath;
	private string _sourceName = string.Empty;
	private double _durationSeconds;
	private double _currentPositionSeconds;
	private double _trimStartSeconds;
	private double _trimEndSeconds;
	private bool _isLoading;
	private DateTimeOffset _videoLoadedAt;
	private string _statusMessage = Strings.VideoEditorOpenVideoHint.GetLocalizedResource();
	private readonly VideoTimeline _timeline = new();
	public ObservableCollection<VideoSegment> Segments => _timeline.Segments;
	public double TimelineToSource(double seconds) => _timeline.ToSource(seconds);
	public double SourceToTimeline(double seconds) => _timeline.FromSource(seconds);
	public void SplitAtPlayhead()
	{
		var sourcePosition = _timeline.ToSource(CurrentPositionSeconds);
		var frameRate = _metadata?.FrameRate ?? 0;
		if (frameRate > 0) sourcePosition = Math.Round(sourcePosition * frameRate) / frameRate;
		var position = _timeline.FromSource(sourcePosition);
		if (_timeline.Split(position, frameRate)) CurrentPositionSeconds = position;
	}
	public void RemoveSegment(VideoSegment segment) => Segments.Remove(segment);
	public void KeepOnlySegment(VideoSegment segment) => _timeline.KeepOnly(segment);

	public ObservableCollection<PendingVideo> PendingVideos { get; } = [];
	public ObservableCollection<VideoCutJob> ProcessingJobs => _queueService.ProcessingJobs;
	public ObservableCollection<VideoCutJob> CompletedJobs => _queueService.CompletedJobs;
	public event Action<string>? SourceReserved
	{
		add => _queueService.SourceReserved += value;
		remove => _queueService.SourceReserved -= value;
	}
	public event Action<string>? SourceReservationReleased
	{
		add => _queueService.SourceReservationReleased += value;
		remove => _queueService.SourceReservationReleased -= value;
	}
	public string ToolStatus => _toolchain.StatusMessage;
	public bool IsToolchainAvailable => _toolchain.IsAvailable;
	public bool HasVideo => _metadata is not null && _sourcePath is not null;
	public bool CanSave => HasVideo && IsToolchainAvailable && !IsLoading && DurationSeconds > 0 && Segments.Count > 0;
	public string SourceName => string.IsNullOrWhiteSpace(_sourceName) ? Strings.VideoEditorNoVideo.GetLocalizedResource() : _sourceName;
	public string DurationText => FormatTime(DurationSeconds);
	public string SourceDetails => _metadata is null
		? string.Empty
		: $"{_metadata.Width} × {_metadata.Height} · {DurationText} · {_metadata.VideoCodec}";
	public string CurrentTimeText => FormatTime(CurrentPositionSeconds);
	public double SourceDurationSeconds => _metadata?.DurationSeconds ?? 0;
	public double VideoAspectRatio => _metadata is { Height: > 0 } metadata ? (double)metadata.Width / metadata.Height : 16d / 9;
	public string PlaybackTimeText => $"{FormatTimelineTime(CurrentPositionSeconds)} / {FormatTimelineTime(DurationSeconds)}";
	public static string FormatTimelineTime(double seconds)
	{
		var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
		return $"{(int)time.TotalMinutes}:{time.Seconds:D2}";
	}
	public string TrimStartText => FormatTime(TrimStartSeconds);
	public string TrimEndText => FormatTime(TrimEndSeconds);

	public string? SourcePath
	{
		get => _sourcePath;
		private set
		{
			if (SetProperty(ref _sourcePath, value))
			{
				OnPropertyChanged(nameof(HasVideo));
				OnPropertyChanged(nameof(CanSave));
			}
		}
	}

	public string SourceNameValue
	{
		get => _sourceName;
		private set
		{
			if (SetProperty(ref _sourceName, value))
				OnPropertyChanged(nameof(SourceName));
		}
	}

	public double DurationSeconds
	{
		get => _durationSeconds;
		private set
		{
			if (SetProperty(ref _durationSeconds, value))
			{
				OnPropertyChanged(nameof(DurationText));
				OnPropertyChanged(nameof(PlaybackTimeText));
			}
		}
	}

	public double CurrentPositionSeconds
	{
		get => _currentPositionSeconds;
		set
		{
			if (SetProperty(ref _currentPositionSeconds, Math.Clamp(value, 0, Math.Max(0, DurationSeconds))))
			{
				OnPropertyChanged(nameof(CurrentTimeText));
				OnPropertyChanged(nameof(PlaybackTimeText));
			}
		}
	}

	public double TrimStartSeconds
	{
		get => _trimStartSeconds;
		private set
		{
			if (SetProperty(ref _trimStartSeconds, value))
			{
				OnPropertyChanged(nameof(TrimStartText));
				OnPropertyChanged(nameof(CanSave));
			}
		}
	}

	public double TrimEndSeconds
	{
		get => _trimEndSeconds;
		private set
		{
			if (SetProperty(ref _trimEndSeconds, value))
			{
				OnPropertyChanged(nameof(TrimEndText));
				OnPropertyChanged(nameof(CanSave));
			}
		}
	}

	public bool IsLoading
	{
		get => _isLoading;
		private set
		{
			if (SetProperty(ref _isLoading, value))
				OnPropertyChanged(nameof(CanSave));
		}
	}

	public string StatusMessage
	{
		get => _statusMessage;
		private set => SetProperty(ref _statusMessage, value);
	}

	public VideoEditorViewModel(VideoProbeService probeService, VideoToolchain toolchain, VideoCutQueueService queueService, IAppSettingsService settings)
	{
		_probeService = probeService;
		_toolchain = toolchain;
		_queueService = queueService;
		_settings = settings;
		Segments.CollectionChanged += (_, _) =>
		{
			DurationSeconds = _timeline.DurationSeconds;
			CurrentPositionSeconds = Math.Min(CurrentPositionSeconds, DurationSeconds);
			OnPropertyChanged(nameof(Segments));
			OnPropertyChanged(nameof(CanSave));
		};
	}

	public PendingVideo AddPendingVideo(string path)
	{
		var fullPath = Path.GetFullPath(path);
		var existing = PendingVideos.FirstOrDefault(video => string.Equals(video.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));
		if (existing is not null)
			return existing;
		var video = new PendingVideo(fullPath);
		PendingVideos.Add(video);
		return video;
	}

	public void SavePendingDraft()
	{
		var video = PendingVideos.FirstOrDefault(video => string.Equals(video.SourcePath, SourcePath, StringComparison.OrdinalIgnoreCase));
		if (video is null || !HasVideo || IsLoading)
			return;
		video.TrimStartSeconds = TrimStartSeconds;
		video.TrimEndSeconds = TrimEndSeconds;
		video.CurrentPositionSeconds = CurrentPositionSeconds;
		video.Segments = Segments.ToArray();
	}

	public async Task LoadVideoAsync(string path, CancellationToken cancellationToken = default)
	{
		SavePendingDraft();
		if (!IsToolchainAvailable)
		{
			StatusMessage = ToolStatus;
			return;
		}

		var fullPath = Path.GetFullPath(path);
		if (!File.Exists(fullPath))
		{
			StatusMessage = Strings.VideoEditorFileMissing.GetLocalizedResource();
			return;
		}

		IsLoading = true;
		StatusMessage = Strings.VideoEditorReadingVideo.GetLocalizedResource();
		try
		{
			var metadata = await _probeService.ProbeAsync(fullPath, cancellationToken);
			_metadata = metadata;
			SourcePath = fullPath;
			SourceNameValue = Path.GetFileName(fullPath);
			DurationSeconds = metadata.DurationSeconds;
			OnPropertyChanged(nameof(SourceDetails));
			_videoLoadedAt = DateTimeOffset.Now;
			TrimStartSeconds = 0;
			TrimEndSeconds = metadata.DurationSeconds;
			CurrentPositionSeconds = 0;
			var pendingVideo = AddPendingVideo(fullPath);
			if (pendingVideo.TrimStartSeconds is { } start && pendingVideo.TrimEndSeconds is { } end)
			{
				TrimStartSeconds = Math.Clamp(start, 0, metadata.DurationSeconds);
				TrimEndSeconds = Math.Clamp(end, TrimStartSeconds, metadata.DurationSeconds);
				CurrentPositionSeconds = pendingVideo.CurrentPositionSeconds;
			}
			_timeline.Restore(pendingVideo.Segments ?? [new VideoSegment(TrimStartSeconds, TrimEndSeconds)]);
			CurrentPositionSeconds = pendingVideo.CurrentPositionSeconds;
			StatusMessage = string.Format(Strings.VideoEditorLoadedMessage.GetLocalizedResource(), metadata.Width, metadata.Height, metadata.VideoCodec);

		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			StatusMessage = Strings.VideoEditorOperationCancelled.GetLocalizedResource();
		}
		catch (Exception ex)
		{
			StatusMessage = string.Format(Strings.VideoEditorLoadFailed.GetLocalizedResource(), ex.Message);
			_metadata = null;
			_timeline.Reset(0);
			OnPropertyChanged(nameof(SourceDetails));
			SourcePath = null;
			SourceNameValue = string.Empty;
			DurationSeconds = 0;
			CurrentPositionSeconds = 0;
			TrimStartSeconds = 0;
			TrimEndSeconds = 0;
		}
		finally
		{
			IsLoading = false;
		}
	}

	public void RestoreTrim()
	{
		if (!HasVideo)
			return;
		TrimStartSeconds = 0;
		TrimEndSeconds = _metadata!.DurationSeconds;
		_timeline.Reset(_metadata.DurationSeconds);
		CurrentPositionSeconds = 0;
	}

	public void CloseVideo()
	{
		_metadata = null;
		_timeline.Reset(0);
		SourcePath = null;
		SourceNameValue = string.Empty;
		DurationSeconds = 0;
		CurrentPositionSeconds = 0;
		TrimStartSeconds = 0;
		TrimEndSeconds = 0;
		OnPropertyChanged(nameof(SourceDetails));
		StatusMessage = Strings.VideoEditorOpenVideoHint.GetLocalizedResource();
	}

	public void ExportCurrent(bool replaceOriginal)
	{
		EnqueueCurrent(startImmediately: true, replaceOriginal);
	}

	public void AddCurrentToQueue(bool replaceOriginal)
	{
		EnqueueCurrent(startImmediately: false, replaceOriginal);
	}

	public void StartQueue() => _queueService.StartQueue();
	public void PauseAll() => _queueService.PauseAll();
	public void ResumeAll() => _queueService.ResumeAll();
	public void Pause(VideoCutJob job) => _queueService.Pause(job);
	public void Resume(VideoCutJob job) => _queueService.Resume(job);
	public Task<bool> CancelForReeditAsync(VideoCutJob job) => _queueService.CancelForReeditAsync(job);
	public void ApplyJobRange(VideoCutJob job)
	{
		_timeline.Restore(job.Segments);
		CurrentPositionSeconds = 0;
	}

	public bool IsSourceReserved(string path) => _queueService.IsSourceReserved(path);

	public void NotifySourceReservationChanged() => OnPropertyChanged(nameof(CanSave));

	public void CancelOrRemove(VideoCutJob job)
	{
		_queueService.CancelOrRemove(job);
		OnPropertyChanged(nameof(CanSave));
	}

	public void Retry(VideoCutJob job)
	{
		_queueService.Retry(job);
		OnPropertyChanged(nameof(CanSave));
	}

	public void SetStatusMessage(string message) => StatusMessage = message;

	public bool InvalidateAfterOutput(VideoCutJob job)
	{
		if (!job.ReplaceOriginal || !string.Equals(SourcePath, job.SourcePath, StringComparison.OrdinalIgnoreCase) ||
			job.CompletedAt is not { } completedAt || completedAt <= _videoLoadedAt)
			return false;

		_metadata = null;
		_timeline.Reset(0);
		OnPropertyChanged(nameof(SourceDetails));
		SourcePath = null;
		SourceNameValue = string.Empty;
		DurationSeconds = 0;
		CurrentPositionSeconds = 0;
		TrimStartSeconds = 0;
		TrimEndSeconds = 0;
		StatusMessage = Strings.VideoEditorOutputComplete.GetLocalizedResource();
		return true;
	}

	private void EnqueueCurrent(bool startImmediately, bool replaceOriginal)
	{
		if (_sourcePath is null || _metadata is null)
		{
			StatusMessage = Strings.VideoEditorOpenVideoHint.GetLocalizedResource();
			return;
		}

		if (DurationSeconds < 0.05 || Segments.Count == 0)
		{
			StatusMessage = Strings.VideoEditorInvalidTrimRange.GetLocalizedResource();
			return;
		}

		var sourceDirectory = Path.GetDirectoryName(_sourcePath)!;
		var exportDirectory = replaceOriginal || _settings.VideoEditorExportToSourceFolder
			? sourceDirectory
			: _settings.VideoEditorExportFolder;
		if (string.IsNullOrWhiteSpace(exportDirectory) || !Directory.Exists(exportDirectory))
		{
			StatusMessage = string.Format(Strings.VideoEditorExportFolderUnavailable.GetLocalizedResource(), exportDirectory);
			return;
		}
		var job = new VideoCutJob(_sourcePath, Segments.First().StartSeconds, Segments.Last().EndSeconds, _metadata, replaceOriginal, exportDirectory, Segments);
		if (_queueService.TryEnqueue(job, startImmediately, out var error))
		{
			var pendingVideo = PendingVideos.FirstOrDefault(video => string.Equals(video.SourcePath, job.SourcePath, StringComparison.OrdinalIgnoreCase));
			if (pendingVideo is not null)
				PendingVideos.Remove(pendingVideo);
			StatusMessage = startImmediately
				? Strings.VideoEditorTaskStarted.GetLocalizedResource()
				: Strings.VideoEditorTaskQueued.GetLocalizedResource();
			OnPropertyChanged(nameof(CanSave));
		}
		else
			StatusMessage = error ?? Strings.VideoEditorQueueFailed.GetLocalizedResource();
	}

	private static string FormatTime(double seconds) => VideoCutJob.FormatTime(seconds);
}
