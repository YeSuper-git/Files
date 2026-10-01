// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

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
	private string? _errorMessage;
	private string? _warningMessage;
	private string? _outputPath;

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
	public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
	public DateTimeOffset? CompletedAt { get; set; }

	public VideoCutJobStatus Status
	{
		get => _status;
		set
		{
			if (SetProperty(ref _status, value))
			{
				OnPropertyChanged(nameof(StatusText));
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
				OnPropertyChanged(nameof(StatusText));
		}
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
		VideoCutJobStatus.Paused => Strings.VideoEditorResume.GetLocalizedResource(),
		VideoCutJobStatus.Failed or VideoCutJobStatus.Cancelled => Strings.VideoEditorRetry.GetLocalizedResource(),
		_ => Strings.VideoEditorPauseJob.GetLocalizedResource()
	};
	public string PauseActionGlyph => Status is VideoCutJobStatus.Paused or VideoCutJobStatus.Failed or VideoCutJobStatus.Cancelled ? "\uE768" : "\uE769";
	public bool CanPauseResume => Status is VideoCutJobStatus.Waiting or VideoCutJobStatus.Processing or VideoCutJobStatus.Paused or VideoCutJobStatus.Failed or VideoCutJobStatus.Cancelled;
	public bool CanCancel => Status != VideoCutJobStatus.Replacing;

	public string TrimSummary => $"{FormatTime(StartSeconds)} – {FormatTime(EndSeconds)}";
	public string JobDetails => string.Join(Environment.NewLine, new[] { TrimSummary, OutputPath, ErrorMessage, WarningMessage }
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
