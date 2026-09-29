// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Files.App.Services.VideoEditor;

namespace Files.App.ViewModels.VideoEditor;

public sealed class VideoEditorViewModel : ObservableObject
{
	private readonly VideoProbeService _probeService;
	private readonly VideoToolchain _toolchain;
	private readonly VideoCutQueueService _queueService;
	private readonly VideoCutPresetService _presetService;
	private readonly IAppSettingsService _settings;
	private IReadOnlyList<double> _allKeyframes = [];
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

	public ObservableCollection<VideoKeyframeMarker> KeyframeMarkers { get; } = [];
	public ObservableCollection<VideoCutJob> ProcessingJobs => _queueService.ProcessingJobs;
	public ObservableCollection<VideoCutJob> CompletedJobs => _queueService.CompletedJobs;
	public ObservableCollection<VideoCutPreset> Presets => _presetService.Presets;
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
	public bool CanSave => HasVideo && IsToolchainAvailable && !IsLoading && TrimEndSeconds > TrimStartSeconds;
	public string SourceName => string.IsNullOrWhiteSpace(_sourceName) ? Strings.VideoEditorNoVideo.GetLocalizedResource() : _sourceName;
	public string DurationText => FormatTime(DurationSeconds);
	public string SourceDetails => _metadata is null
		? string.Empty
		: $"{_metadata.Width} × {_metadata.Height} · {DurationText} · {_metadata.VideoCodec}";
	public string CurrentTimeText => FormatTime(CurrentPositionSeconds);
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
				OnPropertyChanged(nameof(DurationText));
		}
	}

	public double CurrentPositionSeconds
	{
		get => _currentPositionSeconds;
		set
		{
			if (SetProperty(ref _currentPositionSeconds, Math.Clamp(value, 0, Math.Max(0, DurationSeconds))))
				OnPropertyChanged(nameof(CurrentTimeText));
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

	public VideoEditorViewModel(VideoProbeService probeService, VideoToolchain toolchain, VideoCutQueueService queueService, VideoCutPresetService presetService, IAppSettingsService settings)
	{
		_probeService = probeService;
		_toolchain = toolchain;
		_queueService = queueService;
		_presetService = presetService;
		_settings = settings;
	}

	public async Task LoadVideoAsync(string path, CancellationToken cancellationToken = default)
	{
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
			var keyframes = await _probeService.ReadKeyframesAsync(fullPath, cancellationToken);

			_metadata = metadata;
			_allKeyframes = keyframes;
			SourcePath = fullPath;
			SourceNameValue = Path.GetFileName(fullPath);
			DurationSeconds = metadata.DurationSeconds;
			OnPropertyChanged(nameof(SourceDetails));
			_videoLoadedAt = DateTimeOffset.Now;
			TrimStartSeconds = 0;
			TrimEndSeconds = metadata.DurationSeconds;
			CurrentPositionSeconds = 0;
			BuildKeyframeMarkers(keyframes);
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
			OnPropertyChanged(nameof(SourceDetails));
			SourcePath = null;
			SourceNameValue = string.Empty;
			DurationSeconds = 0;
			CurrentPositionSeconds = 0;
			TrimStartSeconds = 0;
			TrimEndSeconds = 0;
			_allKeyframes = [];
			KeyframeMarkers.Clear();
		}
		finally
		{
			IsLoading = false;
		}
	}

	public void SetTrimStart(double seconds)
	{
		if (!HasVideo)
			return;

		var snapped = FindNearestKeyframe(seconds);
		if (snapped >= TrimEndSeconds)
			snapped = FindPreviousKeyframe(TrimEndSeconds);
		TrimStartSeconds = Math.Max(0, snapped);
	}

	public void SetTrimEnd(double seconds)
	{
		if (!HasVideo)
			return;

		var snapped = FindNearestKeyframe(seconds);
		if (snapped <= TrimStartSeconds)
			snapped = FindNextKeyframe(TrimStartSeconds);
		TrimEndSeconds = Math.Min(DurationSeconds, Math.Max(TrimStartSeconds, snapped));
	}

	public void RestoreTrim()
	{
		if (!HasVideo)
			return;
		TrimStartSeconds = 0;
		TrimEndSeconds = DurationSeconds;
	}

	public void CloseVideo()
	{
		_metadata = null;
		SourcePath = null;
		SourceNameValue = string.Empty;
		DurationSeconds = 0;
		CurrentPositionSeconds = 0;
		TrimStartSeconds = 0;
		TrimEndSeconds = 0;
		_allKeyframes = [];
		KeyframeMarkers.Clear();
		OnPropertyChanged(nameof(SourceDetails));
		StatusMessage = Strings.VideoEditorOpenVideoHint.GetLocalizedResource();
	}

	public double SnapToNearestKeyframe(double seconds) => FindNearestKeyframe(seconds);

	public double FindPreviousKeyframe(double seconds)
	{
		for (var index = _allKeyframes.Count - 1; index >= 0; index--)
		{
			if (_allKeyframes[index] < seconds - 0.001)
				return _allKeyframes[index];
		}
		return 0;
	}

	public double FindNextKeyframe(double seconds)
	{
		foreach (var keyframe in _allKeyframes)
		{
			if (keyframe > seconds + 0.001)
				return keyframe;
		}
		return DurationSeconds;
	}

	public void ExportCurrent()
	{
		EnqueueCurrent(startImmediately: true);
	}

	public void AddCurrentToQueue()
	{
		EnqueueCurrent(startImmediately: false);
	}

	public void StartQueue() => _queueService.StartQueue();
	public void PauseAll() => _queueService.PauseAll();
	public void ResumeAll() => _queueService.ResumeAll();
	public void Pause(VideoCutJob job) => _queueService.Pause(job);
	public void Resume(VideoCutJob job) => _queueService.Resume(job);
	public Task<bool> CancelForReeditAsync(VideoCutJob job) => _queueService.CancelForReeditAsync(job);
	public void ApplyJobRange(VideoCutJob job)
	{
		SetTrimStart(job.StartSeconds);
		SetTrimEnd(job.EndSeconds);
		CurrentPositionSeconds = TrimStartSeconds;
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

	public void ApplyPreset(VideoCutPreset preset)
	{
		if (!HasVideo)
		{
			StatusMessage = Strings.VideoEditorOpenVideoHint.GetLocalizedResource();
			return;
		}

		var requestedStart = preset.HeadTrimSeconds < 0.05 ? 0 : Math.Min(preset.HeadTrimSeconds, DurationSeconds);
		var requestedEnd = preset.TailTrimSeconds < 0.05 ? DurationSeconds : Math.Max(0, DurationSeconds - preset.TailTrimSeconds);
		TrimEndSeconds = DurationSeconds;
		SetTrimStart(requestedStart);
		SetTrimEnd(Math.Max(TrimStartSeconds, requestedEnd));
		CurrentPositionSeconds = TrimStartSeconds;
		StatusMessage = Strings.VideoEditorPresetApplied.GetLocalizedResource();
	}

	public async Task DeletePresetAsync(VideoCutPreset preset)
	{
		try
		{
			await _presetService.DeleteAsync(preset);
		}
		catch (Exception ex)
		{
			StatusMessage = string.Format(Strings.VideoEditorPresetDeleteFailed.GetLocalizedResource(), ex.Message);
		}
	}

	public void SetStatusMessage(string message) => StatusMessage = message;

	public bool InvalidateAfterOutput(VideoCutJob job)
	{
		if (!job.ReplaceOriginal || !string.Equals(SourcePath, job.SourcePath, StringComparison.OrdinalIgnoreCase) ||
			job.CompletedAt is not { } completedAt || completedAt <= _videoLoadedAt)
			return false;

		_metadata = null;
		OnPropertyChanged(nameof(SourceDetails));
		SourcePath = null;
		SourceNameValue = string.Empty;
		DurationSeconds = 0;
		CurrentPositionSeconds = 0;
		TrimStartSeconds = 0;
		TrimEndSeconds = 0;
		_allKeyframes = [];
		KeyframeMarkers.Clear();
		StatusMessage = Strings.VideoEditorOutputComplete.GetLocalizedResource();
		return true;
	}

	private void EnqueueCurrent(bool startImmediately)
	{
		if (_sourcePath is null || _metadata is null)
		{
			StatusMessage = Strings.VideoEditorOpenVideoHint.GetLocalizedResource();
			return;
		}

		if (TrimEndSeconds - TrimStartSeconds < 0.05)
		{
			StatusMessage = Strings.VideoEditorInvalidTrimRange.GetLocalizedResource();
			return;
		}

		var sourceDirectory = Path.GetDirectoryName(_sourcePath)!;
		var replaceOriginal = _settings.VideoEditorReplaceOriginal;
		var exportDirectory = replaceOriginal || _settings.VideoEditorExportToSourceFolder
			? sourceDirectory
			: _settings.VideoEditorExportFolder;
		if (string.IsNullOrWhiteSpace(exportDirectory) || !Directory.Exists(exportDirectory))
		{
			StatusMessage = string.Format(Strings.VideoEditorExportFolderUnavailable.GetLocalizedResource(), exportDirectory);
			return;
		}
		var job = new VideoCutJob(_sourcePath, TrimStartSeconds, TrimEndSeconds, _metadata, replaceOriginal, exportDirectory);
		if (_queueService.TryEnqueue(job, startImmediately, out var error))
		{
			StatusMessage = startImmediately
				? Strings.VideoEditorTaskStarted.GetLocalizedResource()
				: Strings.VideoEditorTaskQueued.GetLocalizedResource();
			OnPropertyChanged(nameof(CanSave));
		}
		else
			StatusMessage = error ?? Strings.VideoEditorQueueFailed.GetLocalizedResource();
	}

	private double FindNearestKeyframe(double seconds)
	{
		if (_allKeyframes.Count == 0)
			return Math.Clamp(seconds, 0, DurationSeconds);

		var target = Math.Clamp(seconds, 0, DurationSeconds);
		if (target <= 0.001 || DurationSeconds - target <= 0.001)
			return target <= 0.001 ? 0 : DurationSeconds;
		var index = BinarySearch(_allKeyframes, target);
		if (index >= 0)
			return _allKeyframes[index];

		index = ~index;
		if (index == 0)
			return _allKeyframes[0];
		if (index >= _allKeyframes.Count)
			return _allKeyframes[^1];

		var before = _allKeyframes[index - 1];
		var after = _allKeyframes[index];
		return target - before <= after - target ? before : after;
	}

	private static int BinarySearch(IReadOnlyList<double> values, double target)
	{
		var low = 0;
		var high = values.Count - 1;
		while (low <= high)
		{
			var middle = low + ((high - low) / 2);
			var comparison = values[middle].CompareTo(target);
			if (comparison == 0)
				return middle;
			if (comparison < 0)
				low = middle + 1;
			else
				high = middle - 1;
		}
		return ~low;
	}

	private void BuildKeyframeMarkers(IReadOnlyList<double> keyframes)
	{
		KeyframeMarkers.Clear();
		if (keyframes.Count == 0)
			return;

		const int maxMarkers = 180;
		var stride = Math.Max(1, (int)Math.Ceiling(keyframes.Count / (double)maxMarkers));
		for (var index = 0; index < keyframes.Count; index += stride)
			KeyframeMarkers.Add(new VideoKeyframeMarker(keyframes[index], FormatTime(keyframes[index])));

		var last = keyframes[^1];
		if (KeyframeMarkers.Count == 0 || Math.Abs(KeyframeMarkers[^1].Seconds - last) > 0.001)
			KeyframeMarkers.Add(new VideoKeyframeMarker(last, FormatTime(last)));
	}

	private static string FormatTime(double seconds) => VideoCutJob.FormatTime(seconds);
}
