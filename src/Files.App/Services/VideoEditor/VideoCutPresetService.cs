// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace Files.App.Services.VideoEditor;

public sealed class VideoCutPresetService
{
	private const double PresetMatchToleranceSeconds = 0.75;
	private readonly SemaphoreSlim _saveLock = new(1, 1);
	private readonly string _filePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "video-editor-presets.json");

	public ObservableCollection<VideoCutPreset> Presets { get; } = [];

	public VideoCutPresetService()
	{
		try
		{
			if (!File.Exists(_filePath))
				return;

			var savedPresets = JsonSerializer.Deserialize(
				File.ReadAllText(_filePath),
				VideoCutPresetJsonSerializerContext.Default.ListVideoCutPreset);
			if (savedPresets is null)
				return;

			foreach (var preset in savedPresets
				.Where(preset => double.IsFinite(preset.HeadTrimSeconds) && double.IsFinite(preset.TailTrimSeconds) && preset.HeadTrimSeconds >= 0 && preset.TailTrimSeconds >= 0)
				.OrderByDescending(preset => preset.LastUsed))
				Presets.Add(preset);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			App.Logger.LogWarning(ex, "Unable to load video cut presets");
		}
	}

	public async Task<string?> RecordSuccessfulCutAsync(VideoCutJob job)
	{
		var headTrim = Math.Max(0, job.StartSeconds);
		var tailTrim = Math.Max(0, job.SourceDurationSeconds - job.EndSeconds);
		if (headTrim + tailTrim < 0.05)
			return null;

		await _saveLock.WaitAsync().ConfigureAwait(false);
		try
		{
			VideoCutPreset? match = null;
			VideoCutPreset? updated = null;
			List<VideoCutPreset> snapshot = [];
			await RunOnUiAsync(() =>
			{
				match = Presets.FirstOrDefault(preset =>
					Math.Abs(preset.HeadTrimSeconds - headTrim) <= PresetMatchToleranceSeconds &&
					Math.Abs(preset.TailTrimSeconds - tailTrim) <= PresetMatchToleranceSeconds);
				var now = DateTimeOffset.Now;
				updated = match is null
							? new VideoCutPreset(CreateName(headTrim, tailTrim), headTrim, tailTrim, 1, now)
							: match with
				{
					HeadTrimSeconds = headTrim,
					TailTrimSeconds = tailTrim,
					UseCount = match!.UseCount + 1,
					LastUsed = now
				};
				snapshot = Presets.Where(preset => !ReferenceEquals(preset, match)).ToList();
				snapshot.Insert(0, updated!);
			}).ConfigureAwait(false);

			var temporaryPath = _filePath + ".tmp";
			try
			{
				var json = JsonSerializer.Serialize(snapshot, VideoCutPresetJsonSerializerContext.Default.ListVideoCutPreset);
				await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
				File.Move(temporaryPath, _filePath, overwrite: true);
				await RunOnUiAsync(() =>
				{
					if (match is not null)
						Presets.Remove(match);
					Presets.Insert(0, updated!);
				}).ConfigureAwait(false);
				return null;
			}
			finally
			{
				try
				{
					if (File.Exists(temporaryPath))
						File.Delete(temporaryPath);
				}
				catch (IOException)
				{
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			App.Logger.LogWarning(ex, "Unable to persist video cut preset");
			return Strings.VideoEditorPresetSaveFailed.GetLocalizedResource();
		}
		finally
		{
			_saveLock.Release();
		}
	}

	public async Task DeleteAsync(VideoCutPreset preset)
	{
		await _saveLock.WaitAsync().ConfigureAwait(false);
		try
		{
			List<VideoCutPreset> snapshot = [];
			await RunOnUiAsync(() =>
			{
				snapshot = Presets.Where(item => !ReferenceEquals(item, preset)).ToList();
			}).ConfigureAwait(false);
			var temporaryPath = _filePath + ".tmp";
			var json = JsonSerializer.Serialize(snapshot, VideoCutPresetJsonSerializerContext.Default.ListVideoCutPreset);
			await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
			File.Move(temporaryPath, _filePath, overwrite: true);
			await RunOnUiAsync(() => Presets.Remove(preset)).ConfigureAwait(false);
		}
		finally
		{
			_saveLock.Release();
		}
	}

	private static string CreateName(double headTrim, double tailTrim)
	{
		var head = VideoCutJob.FormatTime(headTrim);
		var tail = VideoCutJob.FormatTime(tailTrim);
		if (headTrim >= 0.05 && tailTrim >= 0.05)
			return string.Format(Strings.VideoEditorPresetBoth.GetLocalizedResource(), head, tail);
		return headTrim >= 0.05
			? string.Format(Strings.VideoEditorPresetHead.GetLocalizedResource(), head)
			: string.Format(Strings.VideoEditorPresetTail.GetLocalizedResource(), tail);
	}

	private static Task RunOnUiAsync(Action action)
	{
		var dispatcher = App.UiDispatcher;
		if (dispatcher is null || dispatcher.HasThreadAccess)
		{
			action();
			return Task.CompletedTask;
		}

		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!dispatcher.TryEnqueue(() =>
		{
			try
			{
				action();
				completion.SetResult();
			}
			catch (Exception ex)
			{
				completion.SetException(ex);
			}
		}))
			completion.SetException(new InvalidOperationException("The UI dispatcher is unavailable."));
		return completion.Task;
	}
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<VideoCutPreset>))]
internal sealed partial class VideoCutPresetJsonSerializerContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
