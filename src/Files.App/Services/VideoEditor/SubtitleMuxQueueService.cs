// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Xaml;

namespace Files.App.Services.VideoEditor;

public enum SubtitleMuxJobState { Waiting, Processing, Completed, Failed, Cancelled }

public sealed class SubtitleMuxJob(string source, SubtitleMuxTrack[] tracks, string output, int defaultTrack, string encoding) : ObservableObject
{
	public string Source { get; } = source;
	public SubtitleMuxTrack[] Tracks { get; } = tracks.ToArray();
	public string Output { get; } = output;
	public int DefaultTrack { get; } = defaultTrack;
	public string Encoding { get; } = encoding;
	public string FileName => Path.GetFileName(Output);
	public CancellationTokenSource Cancellation { get; } = new();
	private SubtitleMuxJobState _state;
	public SubtitleMuxJobState State
	{
		get => _state;
		set { if (SetProperty(ref _state, value)) { OnPropertyChanged(nameof(ProgressVisibility)); OnPropertyChanged(nameof(StatusVisibility)); OnPropertyChanged(nameof(Details)); } }
	}
	private double _progress;
	public double Progress
	{
		get => _progress;
		set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(Percent)); }
	}
	private string _result = string.Empty;
	public string Result { get => _result; set { if (SetProperty(ref _result, value)) OnPropertyChanged(nameof(Details)); } }
	public string Percent => $"{Progress:0}%";
	public Visibility ProgressVisibility => State == SubtitleMuxJobState.Processing ? Visibility.Visible : Visibility.Collapsed;
	public Visibility StatusVisibility => State == SubtitleMuxJobState.Processing ? Visibility.Collapsed : Visibility.Visible;
	public string Details => State switch
	{
		SubtitleMuxJobState.Waiting => Strings.SubtitleMuxQueued.GetLocalizedResource(),
		SubtitleMuxJobState.Processing => Strings.SubtitleMuxWorking.GetLocalizedResource(),
		_ => Result
	};
}

/// <summary>Runs immutable mux requests sequentially, independently of the workspace page.</summary>
public sealed class SubtitleMuxQueueService(SubtitleMuxService service)
{
	public ObservableCollection<SubtitleMuxJob> ProcessingJobs { get; } = [];
	public ObservableCollection<SubtitleMuxJob> CompletedJobs { get; } = [];
	private bool _running;
	public event EventHandler? Changed;

	public void Enqueue(SubtitleMuxJob job)
	{
		if (File.Exists(job.Output) || ProcessingJobs.Any(item => item.Output.Equals(job.Output, StringComparison.OrdinalIgnoreCase)))
			throw new IOException(Strings.MediaToolsOutputExists.GetLocalizedResource());
		ProcessingJobs.Add(job);
		Changed?.Invoke(this, EventArgs.Empty);
		if (!_running) _ = RunAsync();
	}

	public void Cancel(SubtitleMuxJob job)
	{
		if (job.State is not (SubtitleMuxJobState.Waiting or SubtitleMuxJobState.Processing)) return;
		job.Cancellation.Cancel();
		if (job.State == SubtitleMuxJobState.Waiting)
		{
			job.State = SubtitleMuxJobState.Cancelled;
			job.Result = Strings.SubtitleMuxCancelled.GetLocalizedResource();
			Finish(job);
		}
	}

	public void Remove(SubtitleMuxJob job) { CompletedJobs.Remove(job); Changed?.Invoke(this, EventArgs.Empty); }

	private void Finish(SubtitleMuxJob job)
	{
		ProcessingJobs.Remove(job);
		job.Cancellation.Dispose();
		CompletedJobs.Insert(0, job);
		Changed?.Invoke(this, EventArgs.Empty);
	}

	private async Task RunAsync()
	{
		_running = true;
		try
		{
			while (ProcessingJobs.FirstOrDefault(item => item.State == SubtitleMuxJobState.Waiting) is { } job)
			{
				job.State = SubtitleMuxJobState.Processing;
				try
				{
					var progress = new Progress<SubtitleMuxProgress>(value => { if (job.State == SubtitleMuxJobState.Processing) job.Progress = value.Percent; });
					var result = await service.MergeAsync(job.Source, job.Tracks, job.Output, job.DefaultTrack, job.Encoding, progress, job.Cancellation.Token);
					job.Progress = 100;
					job.Result = string.Format(Strings.SubtitleMuxJobComplete.GetLocalizedResource(), result.Size.ToSizeString(), (int)Math.Ceiling(result.Elapsed.TotalSeconds));
					job.State = SubtitleMuxJobState.Completed;
				}
				catch (OperationCanceledException) { job.Result = Strings.SubtitleMuxCancelled.GetLocalizedResource(); job.State = SubtitleMuxJobState.Cancelled; }
				catch (Exception ex) { job.Result = ex.Message; job.State = SubtitleMuxJobState.Failed; }
				Finish(job);
			}
		}
		finally { _running = false; }
	}
}
