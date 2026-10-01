// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Files.App.Services.VideoEditor;

public sealed class VideoProbeService(VideoToolchain toolchain)
{
	private readonly VideoToolchain _toolchain = toolchain;

	public async Task<VideoMetadata> ProbeAsync(string path, CancellationToken cancellationToken = default)
	{
		if (_toolchain.FfprobePath is null)
			throw new InvalidOperationException(_toolchain.StatusMessage);

		var startInfo = CreateStartInfo(_toolchain.FfprobePath);
		foreach (var argument in new[]
		{
			"-v", "error",
			"-show_entries", "format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate,duration",
			"-show_format", "-show_streams",
			"-of", "json",
			path
		})
			startInfo.ArgumentList.Add(argument);

		var (exitCode, output, error) = await RunCapturedAsync(startInfo, cancellationToken).ConfigureAwait(false);
		if (exitCode != 0)
			throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? Strings.VideoEditorProbeFailed.GetLocalizedResource() : error.Trim());

		using var document = JsonDocument.Parse(output);
		var root = document.RootElement;
		var streams = root.TryGetProperty("streams", out var streamArray) ? streamArray : default;
		var videoStream = streams.ValueKind == JsonValueKind.Array
			? streams.EnumerateArray().FirstOrDefault(stream =>
				stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
			: default;

		if (videoStream.ValueKind != JsonValueKind.Object)
			throw new InvalidDataException(Strings.VideoEditorNoVideoStream.GetLocalizedResource());

		var duration = ReadNumber(root, "format", "duration");
		if (duration <= 0)
			duration = ReadNumber(videoStream, "duration");
		if (duration <= 0 || !double.IsFinite(duration))
			throw new InvalidDataException(Strings.VideoEditorDurationUnavailable.GetLocalizedResource());

		var codec = ReadString(videoStream, "codec_name") ?? "unknown";
		var width = ReadInt(videoStream, "width");
		var height = ReadInt(videoStream, "height");
		var frameRate = ParseRate(ReadString(videoStream, "avg_frame_rate"));

		return new VideoMetadata(duration, codec, width, height, frameRate);
	}

	public async Task<IReadOnlyList<double>> ReadKeyframesAsync(string path, CancellationToken cancellationToken = default)
	{
		if (_toolchain.FfprobePath is null)
			throw new InvalidOperationException(_toolchain.StatusMessage);

		var startInfo = CreateStartInfo(_toolchain.FfprobePath);
		foreach (var argument in new[]
		{
			"-v", "error",
			"-select_streams", "v:0",
			"-show_packets",
			"-show_entries", "packet=pts_time,flags",
			"-of", "csv=p=0",
			path
		})
			startInfo.ArgumentList.Add(argument);

		using var process = new Process { StartInfo = startInfo };
		if (!process.Start())
			throw new InvalidOperationException(Strings.VideoEditorFfprobeStartFailed.GetLocalizedResource());

		var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		var keyframes = new List<double>();
		try
		{
			while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
			{
				var fields = line.Split(',', StringSplitOptions.TrimEntries);
				if (fields.Length < 2 || !fields[1].Contains('K'))
					continue;
				var value = fields[0];
				if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
					double.IsFinite(seconds) && seconds >= 0)
					keyframes.Add(seconds);
			}

			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			var error = await errorTask.ConfigureAwait(false);
			if (process.ExitCode != 0)
				throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? Strings.VideoEditorKeyframesFailed.GetLocalizedResource() : error.Trim());
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			throw;
		}

		if (keyframes.Count == 0 || keyframes[0] > 0.001)
			keyframes.Insert(0, 0);

		return keyframes.Distinct().Order().ToArray();
	}

	private static ProcessStartInfo CreateStartInfo(string executablePath)
	{
		return new ProcessStartInfo
		{
			FileName = executablePath,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = System.Text.Encoding.UTF8,
			StandardErrorEncoding = System.Text.Encoding.UTF8
		};
	}

	private static async Task<(int ExitCode, string Output, string Error)> RunCapturedAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
	{
		using var process = new Process { StartInfo = startInfo };
		if (!process.Start())
			throw new InvalidOperationException(Strings.VideoEditorFfprobeStartFailed.GetLocalizedResource());

		var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		try
		{
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			return (process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			throw;
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
		}
		catch (System.ComponentModel.Win32Exception)
		{
		}
	}

	private static double ReadNumber(JsonElement root, string container, string property)
		=> root.TryGetProperty(container, out var child) ? ReadNumber(child, property) : 0;

	private static double ReadNumber(JsonElement root, string property)
	{
		if (!root.TryGetProperty(property, out var value))
			return 0;
		if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
			return number;
		return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
			? parsed
			: 0;
	}

	private static int ReadInt(JsonElement root, string property)
		=> root.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

	private static string? ReadString(JsonElement root, string property)
		=> root.TryGetProperty(property, out var value) ? value.GetString() : null;

	private static double ParseRate(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return 0;

		var parts = value.Split('/');
		if (parts.Length == 2 &&
			double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
			double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0)
			return numerator / denominator;

		return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : 0;
	}
}
