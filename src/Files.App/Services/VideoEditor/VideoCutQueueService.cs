// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Files.App.Services.VideoEditor;

public sealed class VideoCutQueueService(VideoCutProcessor processor)
{
	private readonly object _syncRoot = new();
	private readonly Queue<VideoCutJob> _waitingQueue = new();
	private readonly HashSet<string> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);
	private readonly VideoCutProcessor _processor = processor;
	private CancellationTokenSource? _currentCancellation;
	private Guid? _currentJobId;
	private bool _workerRunning;
	private bool _allPaused;
	private bool _cancelCurrent;

	public ObservableCollection<VideoCutJob> ProcessingJobs { get; } = [];
	public ObservableCollection<VideoCutJob> CompletedJobs { get; } = [];
	public event Action<string>? SourceReserved;
	public event Action<string>? SourceReservationReleased;

	public bool IsSourceReserved(string path)
	{
		lock (_syncRoot)
			return _reservedPaths.Contains(Path.GetFullPath(path));
	}

	public bool TryEnqueue(VideoCutJob job, bool startImmediately, out string? error)
	{
		lock (_syncRoot)
		{
			if (!_reservedPaths.Add(job.SourcePath))
			{
				error = Strings.VideoEditorDuplicateVideo.GetLocalizedResource();
				return false;
			}

			if (startImmediately)
				_waitingQueue.Enqueue(job);
		}

		RunOnUi(() =>
		{
			if (!startImmediately)
				job.Status = VideoCutJobStatus.Paused;
			ProcessingJobs.Add(job);
		});
		error = null;
		if (startImmediately)
			StartQueue();
		return true;
	}

	public void StartQueue()
	{
		lock (_syncRoot)
		{
			_allPaused = false;
			if (_workerRunning || _waitingQueue.Count == 0)
				return;
			_workerRunning = true;
		}

		_ = Task.Run(ProcessQueueAsync);
	}

	public void PauseAll()
	{
		lock (_syncRoot)
		{
			_allPaused = true;
			_waitingQueue.Clear();
			_currentCancellation?.Cancel();
		}
		RunOnUi(() =>
		{
			foreach (var job in ProcessingJobs.Where(job => job.Status == VideoCutJobStatus.Waiting))
				job.Status = VideoCutJobStatus.Paused;
		});
	}

	public void ResumeAll()
	{
		lock (_syncRoot)
		{
			_allPaused = false;
			foreach (var job in ProcessingJobs.Where(job => job.Status == VideoCutJobStatus.Paused && job.Id != _currentJobId))
			{
				job.Status = VideoCutJobStatus.Waiting;
				_waitingQueue.Enqueue(job);
			}
		}
		StartQueue();
	}

	public void Pause(VideoCutJob job)
	{
		if (job.Status == VideoCutJobStatus.Waiting)
		{
			RemoveWaiting(job);
			job.Status = VideoCutJobStatus.Paused;
		}
		else if (job.Status == VideoCutJobStatus.Processing)
		{
			lock (_syncRoot)
			{
				if (_currentJobId == job.Id)
					_currentCancellation?.Cancel();
			}
		}
	}

	public void Resume(VideoCutJob job)
	{
		if (job.Status != VideoCutJobStatus.Paused)
			return;
		lock (_syncRoot)
		{
			if (_currentJobId == job.Id)
				return;
			job.Status = VideoCutJobStatus.Waiting;
			_waitingQueue.Enqueue(job);
		}
		StartQueue();
	}

	private void RemoveWaiting(VideoCutJob job)
	{
		lock (_syncRoot)
		{
			var remaining = _waitingQueue.Where(item => !ReferenceEquals(item, job)).ToArray();
			_waitingQueue.Clear();
			foreach (var item in remaining)
				_waitingQueue.Enqueue(item);
		}
	}

	public async Task<bool> CancelForReeditAsync(VideoCutJob job)
	{
		if (job.Status is VideoCutJobStatus.Replacing or VideoCutJobStatus.Completed)
			return false;
		CancelOrRemove(job);
		while (true)
		{
			lock (_syncRoot)
			{
				if (_currentJobId != job.Id)
					return true;
			}
			await Task.Delay(50);
		}
	}

	public bool Retry(VideoCutJob job)
	{
		if (job.Status is not (VideoCutJobStatus.Failed or VideoCutJobStatus.Cancelled))
			return false;

		lock (_syncRoot)
		{
			if (!_reservedPaths.Contains(job.SourcePath))
				_reservedPaths.Add(job.SourcePath);
			_waitingQueue.Enqueue(job);
		}

		RunOnUi(() =>
		{
			job.Progress = 0;
			job.ErrorMessage = null;
			job.WarningMessage = null;
			job.Status = VideoCutJobStatus.Waiting;
		});
		StartQueue();
		return true;
	}

	public void CancelOrRemove(VideoCutJob job)
	{
		if (job.Status == VideoCutJobStatus.Replacing)
			return;

		if (job.Status == VideoCutJobStatus.Processing)
		{
			lock (_syncRoot)
			{
				if (_currentJobId == job.Id)
				{
					_cancelCurrent = true;
					_currentCancellation?.Cancel();
				}
			}
			return;
		}

		if (job.Status is VideoCutJobStatus.Waiting or VideoCutJobStatus.Paused or VideoCutJobStatus.Failed or VideoCutJobStatus.Cancelled)
		{
			lock (_syncRoot)
			{
				var remaining = _waitingQueue.Where(item => !ReferenceEquals(item, job)).ToArray();
				_waitingQueue.Clear();
				foreach (var item in remaining)
					_waitingQueue.Enqueue(item);
				_reservedPaths.Remove(job.SourcePath);
			}

			RunOnUi(() =>
			{
				ProcessingJobs.Remove(job);
				SourceReservationReleased?.Invoke(job.SourcePath);
			});
		}
	}

	private async Task ProcessQueueAsync()
	{
		while (true)
		{
			VideoCutJob job;
			CancellationTokenSource cancellation;
			lock (_syncRoot)
			{
				if (_allPaused || _waitingQueue.Count == 0)
				{
					_workerRunning = false;
					return;
				}

				job = _waitingQueue.Dequeue();
				cancellation = new CancellationTokenSource();
				_currentJobId = job.Id;
				_currentCancellation = cancellation;
				_cancelCurrent = false;
			}

			RunOnUi(() =>
			{
				job.Status = VideoCutJobStatus.Processing;
				job.ErrorMessage = null;
				job.Progress = 0;
			});

			try
			{
				var warning = await _processor.ProcessAsync(
					job,
					progress => RunOnUi(() => job.Progress = progress),
					() => RunOnUiAndWait(() => { if (job.ReplaceOriginal) SourceReserved?.Invoke(job.SourcePath); job.Status = VideoCutJobStatus.Replacing; }),
					cancellation.Token).ConfigureAwait(false);
				lock (_syncRoot)
					_reservedPaths.Remove(job.SourcePath);
				job.CompletedAt = DateTimeOffset.Now;
				RunOnUi(() =>
				{
					job.WarningMessage = warning;
					job.Progress = 1;
					job.Status = VideoCutJobStatus.Completed;
					ProcessingJobs.Remove(job);
					CompletedJobs.Insert(0, job);
					SourceReservationReleased?.Invoke(job.SourcePath);
				});
			}
			catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
			{
				bool cancelled;
				lock (_syncRoot)
					cancelled = _cancelCurrent;
				if (cancelled)
				{
					lock (_syncRoot)
						_reservedPaths.Remove(job.SourcePath);
					RunOnUi(() => { ProcessingJobs.Remove(job); SourceReservationReleased?.Invoke(job.SourcePath); });
				}
				else
				{
					bool resumeRequested;
					lock (_syncRoot)
					{
						resumeRequested = !_allPaused;
						if (resumeRequested)
						{
							var remaining = _waitingQueue.ToArray();
							_waitingQueue.Clear();
							_waitingQueue.Enqueue(job);
							foreach (var item in remaining)
								_waitingQueue.Enqueue(item);
						}
					}
					RunOnUi(() => { job.Progress = 0; job.Status = resumeRequested ? VideoCutJobStatus.Waiting : VideoCutJobStatus.Paused; });
				}
			}
			catch (Exception ex)
			{
				RunOnUi(() =>
				{
					job.Status = VideoCutJobStatus.Failed;
					job.ErrorMessage = ex.Message;
				});
			}
			finally
			{
				lock (_syncRoot)
				{
					if (ReferenceEquals(_currentCancellation, cancellation))
					{
						_currentCancellation = null;
						_currentJobId = null;
					}
				}
				cancellation.Dispose();
			}
		}
	}

	private static void RunOnUi(Action action)
	{
		var dispatcher = App.UiDispatcher;
		if (dispatcher is null || dispatcher.HasThreadAccess)
			action();
		else
			dispatcher.TryEnqueue(() => action());
	}

	private static void RunOnUiAndWait(Action action)
	{
		var dispatcher = App.UiDispatcher;
		if (dispatcher is null || dispatcher.HasThreadAccess)
		{
			action();
			return;
		}
		using var completed = new ManualResetEventSlim();
		Exception? error = null;
		if (!dispatcher.TryEnqueue(() =>
		{
			try { action(); }
			catch (Exception ex) { error = ex; }
			finally { completed.Set(); }
		}))
			throw new InvalidOperationException("Video editor UI is unavailable.");
		completed.Wait();
		if (error is not null)
			throw error;
	}
}
