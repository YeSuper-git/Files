// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Collections.Concurrent;

namespace Files.App.Services.VideoEditor;

public enum VideoCutJobStatus
{
	Waiting,
	Paused,
	Processing,
	Replacing,
	Completed,
	Failed,
	Cancelled
}

public sealed class VideoCutJob : ObservableObject
{
	private VideoCutJobStatus _status = VideoCutJobStatus.Waiting;
	private double _progress;
	private int? _estimatedRemainingSeconds;
	private string? _errorMessage;
	private string? _warningMessage;
	private string? _outputPath;
    private readonly ConcurrentDictionary<string, byte> _temporaryOutputs = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> TemporaryOutputPaths => _temporaryOutputs.Keys.ToArray();
    internal void TrackTemporaryOutput(string path) => _temporaryOutputs.TryAdd(path, 0);
    internal void UntrackTemporaryOutput(string path) => _temporaryOutputs.TryRemove(path, out _);

	public Guid Id { get; } = Guid.NewGuid();
	public string SourcePath { get; }
	public string FileName => Path.GetFileName(SourcePath);
	public bool ReplaceOriginal { get; }
	public string ExportDirectory { get; }
	public string? OutputPath
	{
		get => _outputPath;
		set
		{
			if (SetProperty(ref _outputPath, value))
			{
				OnPropertyChanged(nameof(CompletedFileName));
				OnPropertyChanged(nameof(JobDetails));
			}
		}
	}
	public string CompletedFileName => Path.GetFileName(OutputPath ?? SourcePath);
	public double StartSeconds { get; }
	public double EndSeconds { get; }
	public IReadOnlyList<VideoSegment> Segments { get; }
	public double OutputDurationSeconds => Segments.Sum(segment => segment.DurationSeconds);
	public double SourceDurationSeconds { get; }
	public string SourceVideoCodec { get; }
	public int SourceWidth { get; }
	public int SourceHeight { get; }
	public double SourceFrameRate { get; }
	public int AudioStreamCount { get; }
    public long SourceVideoBitRate { get; }
    public long SourceTotalBitRate { get; }
    public IReadOnlyList<long> SourceAudioBitRates { get; }
	public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
	public DateTimeOffset? CompletedAt { get; set; }
    public bool HasCommittedOutput { get; set; }
    private long? _outputSizeBytes;
    public long? OutputSizeBytes
    {
        get => _outputSizeBytes;
        set
        {
            if (SetProperty(ref _outputSizeBytes, value))
            {
                OnPropertyChanged(nameof(OutputSizeText));
                OnPropertyChanged(nameof(JobDetails));
            }
        }
    }
    public string OutputSizeText => OutputSizeBytes is long bytes ? bytes.ToSizeString() : string.Empty;
    private string? _videoEncoder;
    public string? VideoEncoder { get => _videoEncoder; set { if (SetProperty(ref _videoEncoder, value)) OnPropertyChanged(nameof(JobDetails)); } }
    public string? GeneratedOutputPath => HasCommittedOutput ? OutputPath : null;
    private TimeSpan _processingElapsed;
    public TimeSpan ProcessingElapsed
    {
        get => _processingElapsed;
        set { if (SetProperty(ref _processingElapsed, value)) { OnPropertyChanged(nameof(ElapsedText)); OnPropertyChanged(nameof(JobDetails)); } }
    }
    public string ElapsedText => string.Format(Strings.VideoEditorElapsed.GetLocalizedResource(), ProcessingElapsed.TotalHours >= 1
        ? ProcessingElapsed.ToString(@"h\:mm\:ss") : ProcessingElapsed.ToString(@"m\:ss"));

	public VideoCutJobStatus Status
	{
		get => _status;
		set
		{
			if (SetProperty(ref _status, value))
			{
				EstimatedRemainingSeconds = null;
				OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ShowProgress));
                OnPropertyChanged(nameof(QueueStatusText));
				OnPropertyChanged(nameof(PauseActionText));
				OnPropertyChanged(nameof(PauseActionGlyph));
				OnPropertyChanged(nameof(CanPauseResume));
				OnPropertyChanged(nameof(CanCancel));
			}
		}
	}

	public double Progress
	{
		get => _progress;
		set
		{
            if (SetProperty(ref _progress, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ProgressText));
            }
		}
	}

	public int? EstimatedRemainingSeconds
	{
		get => _estimatedRemainingSeconds;
		private set
		{
			if (SetProperty(ref _estimatedRemainingSeconds, value))
				OnPropertyChanged(nameof(RemainingTimeText));
		}
	}

	public string RemainingTimeText => EstimatedRemainingSeconds is int seconds
		? seconds < 60
            ? string.Format(Strings.VideoEditorRemainingSeconds.GetLocalizedResource(), seconds)
            : string.Format(Strings.VideoEditorRemainingMinutesSeconds.GetLocalizedResource(), seconds / 60, seconds % 60)
		: Strings.VideoEditorEstimatingRemaining.GetLocalizedResource();

	public void UpdateProgress(double progress, TimeSpan elapsed)
	{
		Progress = double.IsFinite(progress) ? progress : 0;
		var remaining = Progress > 0 && Progress < 1 && elapsed.TotalSeconds >= 1
			? Math.Ceiling(elapsed.TotalSeconds * (1 - Progress) / Progress)
			: double.NaN;
		EstimatedRemainingSeconds = double.IsFinite(remaining)
			? (int)Math.Clamp(remaining, 1, int.MaxValue) : null;
	}

	public string? ErrorMessage
	{
		get => _errorMessage;
		set
		{
			if (SetProperty(ref _errorMessage, value))
				OnPropertyChanged(nameof(JobDetails));
		}
	}

	public string? WarningMessage
	{
		get => _warningMessage;
		set
		{
			if (SetProperty(ref _warningMessage, value))
				OnPropertyChanged(nameof(JobDetails));
		}
	}

    public bool ShowProgress => Status is VideoCutJobStatus.Processing or VideoCutJobStatus.Replacing;
    public string QueueStatusText => Status == VideoCutJobStatus.Waiting ? Strings.VideoEditorPendingStatus.GetLocalizedResource() : StatusText;
	public string ProgressText => $"{Progress:P0}";
	public string StatusText => Status switch
	{
		VideoCutJobStatus.Waiting => Strings.VideoEditorStatusWaiting.GetLocalizedResource(),
		VideoCutJobStatus.Paused => Strings.VideoEditorStatusPaused.GetLocalizedResource(),
		VideoCutJobStatus.Processing => $"{Strings.VideoEditorStatusProcessing.GetLocalizedResource()} {Progress:P0}",
		VideoCutJobStatus.Replacing => ReplaceOriginal
			? Strings.VideoEditorStatusReplacing.GetLocalizedResource()
			: Strings.VideoEditorStatusSaving.GetLocalizedResource(),
		VideoCutJobStatus.Completed => Strings.VideoEditorStatusCompleted.GetLocalizedResource(),
		VideoCutJobStatus.Failed => Strings.VideoEditorStatusFailed.GetLocalizedResource(),
		VideoCutJobStatus.Cancelled => Strings.VideoEditorStatusCancelled.GetLocalizedResource(),
		_ => string.Empty
	};

	public string PauseActionText => Status switch
	{
		VideoCutJobStatus.Paused or VideoCutJobStatus.Waiting => Strings.VideoEditorDownload.GetLocalizedResource(),
		VideoCutJobStatus.Failed or VideoCutJobStatus.Cancelled => Strings.VideoEditorRetry.GetLocalizedResource(),
		_ => Strings.VideoEditorPauseJob.GetLocalizedResource()
	};
	public string PauseActionGlyph => Status is VideoCutJobStatus.Processing or VideoCutJobStatus.Replacing ? "\uE769" : "\uE896";
	public bool CanPauseResume => Status is VideoCutJobStatus.Waiting or VideoCutJobStatus.Processing or VideoCutJobStatus.Paused or VideoCutJobStatus.Failed or VideoCutJobStatus.Cancelled;
	public bool CanCancel => Status != VideoCutJobStatus.Replacing;

	public string TrimSummary => $"{FormatTime(StartSeconds)} – {FormatTime(EndSeconds)}";
	public string JobDetails => string.Join(Environment.NewLine, new[] { TrimSummary, OutputPath, VideoEncoder is null ? null : string.Format(Strings.VideoEditorEncoder.GetLocalizedResource(), VideoEncoder), OutputSizeText, ElapsedText, ErrorMessage, WarningMessage }
		.Where(static detail => !string.IsNullOrWhiteSpace(detail)));

	public VideoCutJob(string sourcePath, double startSeconds, double endSeconds, VideoMetadata sourceMetadata, bool replaceOriginal, string exportDirectory, IEnumerable<VideoSegment>? segments = null)
	{
		SourcePath = Path.GetFullPath(sourcePath);
		ReplaceOriginal = replaceOriginal;
		ExportDirectory = Path.GetFullPath(exportDirectory);
		StartSeconds = startSeconds;
		EndSeconds = endSeconds;
		Segments = (segments ?? [new VideoSegment(startSeconds, endSeconds)]).ToArray();
		SourceDurationSeconds = sourceMetadata.DurationSeconds;
		SourceVideoCodec = sourceMetadata.VideoCodec;
		SourceWidth = sourceMetadata.Width;
		SourceHeight = sourceMetadata.Height;
		SourceFrameRate = sourceMetadata.FrameRate;
		AudioStreamCount = sourceMetadata.AudioStreamCount;
        SourceVideoBitRate = sourceMetadata.VideoBitRate;
        SourceTotalBitRate = sourceMetadata.TotalBitRate;
        SourceAudioBitRates = sourceMetadata.AudioBitRates?.ToArray() ?? [];
	}

	public static string FormatTime(double seconds)
	{
		if (!double.IsFinite(seconds) || seconds < 0)
			seconds = 0;

		var time = TimeSpan.FromSeconds(seconds);
		return time.TotalHours >= 1
			? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
			: $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
	}
}
