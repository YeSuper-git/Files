// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Files.App.Services.VideoEditor;

public sealed record SubtitleMuxTrack(string Path, string Title, string Language);
public sealed record SubtitleMuxProgress(double Percent);
public sealed record SubtitleMuxResult(string Path, long Size, TimeSpan Elapsed);

public sealed class SubtitleMuxService(VideoToolchain toolchain, VideoProbeService probe)
{
	public async Task<SubtitleMuxResult> MergeAsync(string source, IReadOnlyList<SubtitleMuxTrack> tracks, string output,
		int defaultTrack, string encoding, IProgress<SubtitleMuxProgress>? progress, CancellationToken token)
	{
		if (!toolchain.IsAvailable) throw new InvalidOperationException(toolchain.StatusMessage);
		source = Path.GetFullPath(source);
		output = Path.GetFullPath(output);
		if (!File.Exists(source) || tracks.Count == 0 || tracks.Any(track => !File.Exists(track.Path)))
			throw new FileNotFoundException(Strings.MediaToolsMissingInput.GetLocalizedResource());
		if (!Path.GetExtension(output).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException(Strings.SubtitleMuxMkvRequired.GetLocalizedResource());
		if (File.Exists(output) || output.Equals(source, StringComparison.OrdinalIgnoreCase))
			throw new IOException(Strings.MediaToolsOutputExists.GetLocalizedResource());
		var metadata = await probe.ProbeAsync(source, token).ConfigureAwait(false);
		var sourceCodecs = await ReadSubtitleCodecsAsync(source, token).ConfigureAwait(false);
		var temp = Path.Combine(Path.GetDirectoryName(output)!, $".subtitle-mux-{Guid.NewGuid():N}.mkv");
		var stopwatch = Stopwatch.StartNew();
		try
		{
			var info = CreateProcess(toolchain.FfmpegPath!);
			Add(info, "-hide_banner", "-nostdin", "-n", "-i", source);
			foreach (var track in tracks) Add(info, "-sub_charenc", encoding, "-i", Path.GetFullPath(track.Path));
			Add(info, "-map", "0:v", "-map", "0:a?", "-map", "0:s?", "-map", "0:t?");
			for (var index = 0; index < tracks.Count; index++) Add(info, "-map", $"{index + 1}:s:0");
			Add(info, "-map_metadata", "0", "-map_chapters", "0", "-c", "copy");
			for (var index = 0; index < sourceCodecs.Length; index++)
			{
				if (sourceCodecs[index] == "mov_text") Add(info, $"-c:s:{index}", "srt");
				Add(info, $"-disposition:s:{index}", "0");
			}
			for (var index = 0; index < tracks.Count; index++)
			{
				var target = sourceCodecs.Length + index;
				Add(info, $"-metadata:s:s:{target}", "title=" + tracks[index].Title,
					$"-metadata:s:s:{target}", "language=" + tracks[index].Language,
					$"-disposition:s:{target}", index == defaultTrack ? "default" : "0");
			}
			Add(info, "-progress", "pipe:1", "-nostats", "-f", "matroska", temp);
			await RunMuxAsync(info, metadata.DurationSeconds, progress, token).ConfigureAwait(false);
			var result = await probe.ProbeAsync(temp, token).ConfigureAwait(false);
			var codecs = await ReadSubtitleCodecsAsync(temp, token).ConfigureAwait(false);
			if (result.VideoCodec != metadata.VideoCodec || result.Width != metadata.Width || result.Height != metadata.Height
				|| result.AudioStreamCount != metadata.AudioStreamCount || Math.Abs(result.DurationSeconds - metadata.DurationSeconds) > 2
				|| codecs.Length != sourceCodecs.Length + tracks.Count)
				throw new InvalidDataException(Strings.SubtitleMuxValidationFailed.GetLocalizedResource());
			token.ThrowIfCancellationRequested();
			var size = new FileInfo(temp).Length;
			File.Move(temp, output, overwrite: false);
			progress?.Report(new(100));
			return new(output, size, stopwatch.Elapsed);
		}
		finally
		{
			if (File.Exists(temp)) File.Delete(temp);
		}
	}

	private async Task<string[]> ReadSubtitleCodecsAsync(string path, CancellationToken token)
	{
		var info = CreateProcess(toolchain.FfprobePath!);
		Add(info, "-v", "error", "-select_streams", "s", "-show_entries", "stream=codec_name", "-of", "json", path);
		using var process = new Process { StartInfo = info };
		process.Start();
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		try
		{
			await process.WaitForExitAsync(token).ConfigureAwait(false);
			var stderr = await error.ConfigureAwait(false);
			if (process.ExitCode != 0) throw new InvalidDataException(stderr);
			using var json = JsonDocument.Parse(await output.ConfigureAwait(false));
			return json.RootElement.GetProperty("streams").EnumerateArray().Select(stream => stream.GetProperty("codec_name").GetString() ?? "").ToArray();
		}
		catch (OperationCanceledException)
		{
			await StopAsync(process).ConfigureAwait(false);
			throw;
		}
	}

	private static async Task RunMuxAsync(ProcessStartInfo info, double duration, IProgress<SubtitleMuxProgress>? progress, CancellationToken token)
	{
		using var process = new Process { StartInfo = info };
		process.Start();
		var errors = new StringBuilder();
		var readError = Task.Run(async () =>
		{
			while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
			{
				errors.AppendLine(line);
				if (errors.Length > 16000) errors.Remove(0, errors.Length - 12000);
			}
		});
		var readProgress = Task.Run(async () =>
		{
			while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
				if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && double.TryParse(line[12..], NumberStyles.Float, CultureInfo.InvariantCulture, out var micros))
					progress?.Report(new(Math.Clamp(micros / 1000000 / Math.Max(1, duration) * 100, 0, 99)));
		});
		try
		{
			await process.WaitForExitAsync(token).ConfigureAwait(false);
			await Task.WhenAll(readError, readProgress).ConfigureAwait(false);
			if (process.ExitCode != 0) throw new InvalidDataException(errors.ToString());
		}
		catch (OperationCanceledException)
		{
			await StopAsync(process).ConfigureAwait(false);
			await Task.WhenAll(readError, readProgress).ConfigureAwait(false);
			throw;
		}
	}

	private static async Task StopAsync(Process process)
	{
		try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
		catch (InvalidOperationException) { }
		catch (System.ComponentModel.Win32Exception) { }
		await process.WaitForExitAsync().ConfigureAwait(false);
	}

	private static ProcessStartInfo CreateProcess(string path) => new()
	{
		FileName = path, UseShellExecute = false, CreateNoWindow = true,
		RedirectStandardOutput = true, RedirectStandardError = true,
		StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
	};

	private static void Add(ProcessStartInfo info, params string[] arguments)
	{
		foreach (var argument in arguments) info.ArgumentList.Add(argument);
	}
}
