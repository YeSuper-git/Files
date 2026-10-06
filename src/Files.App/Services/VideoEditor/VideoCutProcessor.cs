// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Files.App.Services.VideoEditor;

public sealed record VideoCutResult(string OutputPath, string? WarningMessage, string VideoEncoder, bool HardwareDecoded, long OutputSizeBytes = 0);

public sealed class VideoCutProcessor(VideoToolchain toolchain, VideoProbeService probeService)
{
	private readonly VideoToolchain _toolchain = toolchain;
	private readonly VideoProbeService _probeService = probeService;
	private bool _cudaDecodeUnavailable;
	private readonly HashSet<string> _unavailableEncoders = new(StringComparer.OrdinalIgnoreCase);

	public async Task<VideoCutResult> ProcessAsync(
		VideoCutJob job,
		Action<double> reportProgress,
		Action beginReplace,
		CancellationToken cancellationToken)
	{
		if (!_toolchain.IsAvailable || _toolchain.FfmpegPath is null)
			throw new InvalidOperationException(_toolchain.StatusMessage);
		if (!File.Exists(job.SourcePath))
			throw new FileNotFoundException(Strings.VideoEditorFileMissing.GetLocalizedResource(), job.SourcePath);
		if (job.StartSeconds < 0 || job.EndSeconds <= job.StartSeconds || job.EndSeconds > job.SourceDurationSeconds + 0.25)
			throw new InvalidDataException(Strings.VideoEditorInvalidTrimRange.GetLocalizedResource());

		if (job.Segments.Count == 0) throw new InvalidDataException(Strings.VideoEditorInvalidTrimRange.GetLocalizedResource());
		double previousEnd = 0;
		foreach (var segment in job.Segments)
		{
			if (!double.IsFinite(segment.StartSeconds) || !double.IsFinite(segment.EndSeconds) || segment.StartSeconds < previousEnd || segment.EndSeconds <= segment.StartSeconds || segment.EndSeconds > job.SourceDurationSeconds + 0.001)
				throw new InvalidDataException(Strings.VideoEditorInvalidTrimRange.GetLocalizedResource());
			previousEnd = segment.EndSeconds;
		}

		var source = new FileInfo(job.SourcePath);
		var sourceAttributes = File.GetAttributes(job.SourcePath);
		var extension = source.Extension;
		if (string.IsNullOrWhiteSpace(extension))
			throw new InvalidDataException(Strings.VideoEditorUnknownExtension.GetLocalizedResource());

		var directory = source.DirectoryName ?? throw new IOException(Strings.VideoEditorSourceFolderUnavailable.GetLocalizedResource());
		var outputDirectory = job.ReplaceOriginal ? directory : job.ExportDirectory;
		if (!Directory.Exists(outputDirectory))
			throw new DirectoryNotFoundException(string.Format(Strings.VideoEditorExportFolderUnavailable.GetLocalizedResource(), outputDirectory));
		var operationId = Guid.NewGuid();
		var temporaryPath = Path.Combine(outputDirectory, $".{source.Name}.filesmax-{operationId:N}.tmp{extension}");
		var backupPath = Path.Combine(directory, $".{source.Name}.filesmax-{operationId:N}.backup{extension}");
		var expectedDuration = job.OutputDurationSeconds;
		string? warningMessage = null;
		string outputPath;
        var selectedEncoder = GetVideoEncoder(job).Encoder;
        var selectedCudaDecode = false;

		CheckAvailableSpace(source, outputDirectory, expectedDuration, job.SourceDurationSeconds);

		try
		{
            job.TrackTemporaryOutput(temporaryPath);
			using (File.Create(temporaryPath))
			{
			}
			var softwareEncoder = GetVideoEncoder(job).Encoder;
            var candidates = job.SourceVideoCodec.ToLowerInvariant() switch
            {
                "h264" => new[] { "h264_nvenc", "h264_qsv", "h264_amf", softwareEncoder },
                "hevc" => new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf", softwareEncoder },
                "av1" => new[] { "av1_nvenc", softwareEncoder },
                _ => new[] { softwareEncoder }
            };
            var copied = false;
            if (job.Segments.Count == 1 && extension.ToLowerInvariant() is ".mp4" or ".mkv" or ".mov")
            {
                var nearby = await _probeService.FindNearbyKeyframeAsync(job.SourcePath, job.StartSeconds, cancellationToken).ConfigureAwait(false);
                if (nearby is double copyStart && copyStart < job.EndSeconds)
                {
                    var copyDuration = job.EndSeconds - copyStart;
                    try
                    {
                        await RunFfmpegAsync(job, temporaryPath, copyDuration, reportProgress, "copy", false, cancellationToken,
                            streamCopy: true, copyStartSeconds: copyStart).ConfigureAwait(false);
                        ValidateOutput(job, await _probeService.ProbeAsync(temporaryPath, cancellationToken).ConfigureAwait(false), copyDuration, durationTolerance: 2);
                        expectedDuration = copyDuration;
                        selectedEncoder = "copy";
                        copied = true;
                        if (Math.Abs(copyStart - job.StartSeconds) > 0.001)
                            warningMessage = string.Format(Strings.VideoEditorFastCutAdjusted.GetLocalizedResource(),
                                VideoCutJob.FormatTime(copyStart), Math.Abs(copyStart - job.StartSeconds).ToString("0.##", CultureInfo.CurrentCulture));
                    }
                    catch (InvalidDataException) { reportProgress(0); }
                }
            }
            if (!copied)
            foreach (var encoder in candidates.Where(encoder => !_unavailableEncoders.Contains(encoder)))
            {
                try
                {
                    var cudaDecode = job.Segments.Count == 1 && encoder.EndsWith("_nvenc", StringComparison.Ordinal) && !_cudaDecodeUnavailable;
                    try
                    {
                        await RunFfmpegAsync(job, temporaryPath, expectedDuration, reportProgress, encoder, cudaDecode, cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidDataException) when (cudaDecode)
                    {
                        _cudaDecodeUnavailable = true;
                        cudaDecode = false;
                        await RunFfmpegAsync(job, temporaryPath, expectedDuration, reportProgress, encoder, false, cancellationToken).ConfigureAwait(false);
                    }
                    selectedEncoder = encoder;
                    selectedCudaDecode = cudaDecode;
                    break;
                }
                catch (InvalidDataException) when (encoder != softwareEncoder)
                {
                    _unavailableEncoders.Add(encoder);
                }
            }
			cancellationToken.ThrowIfCancellationRequested();

			if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length < 1024)
				throw new InvalidDataException(Strings.VideoEditorInvalidTemporaryFile.GetLocalizedResource());
			File.SetAttributes(temporaryPath, File.GetAttributes(temporaryPath) | FileAttributes.Hidden);

			var result = await _probeService.ProbeAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
			ValidateOutput(job, result, expectedDuration, durationTolerance: copied ? 2 : null);
			cancellationToken.ThrowIfCancellationRequested();

            var outputSizeBytes = new FileInfo(temporaryPath).Length;
			beginReplace();
			cancellationToken.ThrowIfCancellationRequested();
			if (job.ReplaceOriginal)
			{
				File.SetAttributes(temporaryPath, sourceAttributes);
				try
				{
					File.Replace(temporaryPath, job.SourcePath, backupPath, ignoreMetadataErrors: true);
				}
				catch
				{
					RestoreBackupIfNeeded(job.SourcePath, temporaryPath, backupPath);
					throw;
				}
				outputPath = job.SourcePath;
				try
				{
					if (File.Exists(backupPath))
						File.SetAttributes(backupPath, File.GetAttributes(backupPath) | FileAttributes.Hidden);
					File.Delete(backupPath);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
				{
					App.Logger.LogWarning(ex, "Unable to remove the temporary video replacement backup {BackupPath}", backupPath);
					warningMessage = string.Format(Strings.VideoEditorBackupCleanupWarning.GetLocalizedResource(), backupPath);
				}
			}
			else
			{
				File.SetAttributes(temporaryPath, FileAttributes.Normal);
				outputPath = MoveToAvailableModifiedPath(temporaryPath, outputDirectory, source);
			}

			return new VideoCutResult(outputPath, warningMessage, selectedEncoder, selectedCudaDecode, outputSizeBytes);
		}
		finally
		{
			if (TryDeleteTemporaryFile(temporaryPath)) job.UntrackTemporaryOutput(temporaryPath);
		}
	}

	private static string MoveToAvailableModifiedPath(string temporaryPath, string outputDirectory, FileInfo source)
	{
		var baseName = Path.GetFileNameWithoutExtension(source.Name) + "-修改版";
		for (var index = 1; index < 10000; index++)
		{
			var suffix = index == 1 ? string.Empty : $" ({index})";
			var outputPath = Path.Combine(outputDirectory, baseName + suffix + source.Extension);
			try
			{
				File.Move(temporaryPath, outputPath);
				return outputPath;
			}
			catch (IOException) when (File.Exists(outputPath))
			{
			}
		}
		throw new IOException(Strings.VideoEditorOutputNameUnavailable.GetLocalizedResource());
	}

	private async Task RunFfmpegAsync(
		VideoCutJob job,
		string temporaryPath,
		double expectedDuration,
		Action<double> reportProgress,
        string encoder,
        bool cudaDecode,
		CancellationToken cancellationToken,
        bool streamCopy = false,
        double? copyStartSeconds = null)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = _toolchain.FfmpegPath!,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

		foreach (var argument in new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1" }) startInfo.ArgumentList.Add(argument);
        if (streamCopy)
        {
            foreach (var argument in new[] { "-ss", FormatSeconds(copyStartSeconds ?? job.StartSeconds), "-i", job.SourcePath, "-t", FormatSeconds(expectedDuration),
                "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-map_metadata", "0", "-map_chapters", "-1" }) startInfo.ArgumentList.Add(argument);
        }
        else
        {
		var separateInputs = job.Segments.Count <= 8;
        var singleSegment = job.Segments.Count == 1;
        void AddInput(double start, double duration)
        {
            if (cudaDecode)
            {
                startInfo.ArgumentList.Add("-hwaccel"); startInfo.ArgumentList.Add("cuda");
                if (singleSegment) { startInfo.ArgumentList.Add("-hwaccel_output_format"); startInfo.ArgumentList.Add("cuda"); }
            }
            foreach (var argument in new[] { "-ss", FormatSeconds(start), "-t", FormatSeconds(duration), "-i", job.SourcePath }) startInfo.ArgumentList.Add(argument);
        }
        if (!separateInputs) AddInput(job.StartSeconds, job.EndSeconds - job.StartSeconds);
        var filters = new List<string>();
		var concatInputs = new StringBuilder();
		for (var index = 0; index < job.Segments.Count; index++)
		{
			var segment = job.Segments[index];
			if (separateInputs)
                AddInput(segment.StartSeconds, segment.DurationSeconds);
            var inputIndex = separateInputs ? index : 0;
            var videoTrim = separateInputs ? "" : $"trim=start={FormatSeconds(segment.StartSeconds - job.StartSeconds)}:end={FormatSeconds(segment.EndSeconds - job.StartSeconds)},";
            var audioTrim = separateInputs ? "" : $"atrim=start={FormatSeconds(segment.StartSeconds - job.StartSeconds)}:end={FormatSeconds(segment.EndSeconds - job.StartSeconds)},";
            filters.Add($"[{inputIndex}:v:0]{videoTrim}setpts=PTS-STARTPTS[v{index}]");
			concatInputs.Append($"[v{index}]");
			for (var audio = 0; audio < job.AudioStreamCount; audio++)
			{
				filters.Add($"[{inputIndex}:a:{audio}]{audioTrim}asetpts=PTS-STARTPTS[a{index}_{audio}]");
				concatInputs.Append($"[a{index}_{audio}]");
			}
		}
		var audioOutputs = string.Concat(Enumerable.Range(0, job.AudioStreamCount).Select(audio => $"[aout{audio}]"));
		filters.Add($"{concatInputs}concat=n={job.Segments.Count}:v=1:a={job.AudioStreamCount}[vout]{audioOutputs}");
        if (!singleSegment)
            foreach (var argument in new[] { "-filter_complex", string.Join(";", filters) }) startInfo.ArgumentList.Add(argument);
        foreach (var argument in new[]
        {
            "-map", singleSegment ? "0:v:0" : "[vout]", "-map_metadata", "0", "-map_chapters", "-1",
            "-c:v", encoder, "-fps_mode", "vfr"
        }) startInfo.ArgumentList.Add(argument);
		if (encoder is "libx264" or "libx265")
		{
			foreach (var argument in new[] { "-preset", "veryfast" }) startInfo.ArgumentList.Add(argument);
		}
        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal))
            foreach (var argument in new[] { "-preset", "p3", "-rc", "vbr", "-cq", "18" }) startInfo.ArgumentList.Add(argument);
        else if (encoder.EndsWith("_qsv", StringComparison.Ordinal))
            foreach (var argument in new[] { "-preset", "veryfast" }) startInfo.ArgumentList.Add(argument);
        else if (encoder.EndsWith("_amf", StringComparison.Ordinal))
            foreach (var argument in new[] { "-quality", "speed", "-rc", "vbr_peak" }) startInfo.ArgumentList.Add(argument);
        var totalBitRate = job.SourceTotalBitRate > 0 ? job.SourceTotalBitRate
            : new FileInfo(job.SourcePath).Length * 8d / job.SourceDurationSeconds;
        var audioBudget = Enumerable.Range(0, job.AudioStreamCount).Sum(audio =>
            audio < job.SourceAudioBitRates.Count && job.SourceAudioBitRates[audio] > 0 ? job.SourceAudioBitRates[audio] : 128_000L);
        var videoBudget = Math.Max(1_000, totalBitRate * 0.95 - audioBudget);
        var videoBitRate = (long)Math.Min(job.SourceVideoBitRate > 0 ? job.SourceVideoBitRate : videoBudget, videoBudget);
        foreach (var argument in new[] { "-b:v", videoBitRate.ToString(CultureInfo.InvariantCulture),
            "-maxrate", videoBitRate.ToString(CultureInfo.InvariantCulture), "-bufsize", (videoBitRate * 2).ToString(CultureInfo.InvariantCulture) })
            startInfo.ArgumentList.Add(argument);
        for (var audio = 0; audio < job.AudioStreamCount; audio++)
        {
            startInfo.ArgumentList.Add("-map");
			startInfo.ArgumentList.Add(singleSegment ? $"0:a:{audio}" : $"[aout{audio}]");
            var audioBitRate = audio < job.SourceAudioBitRates.Count && job.SourceAudioBitRates[audio] > 0 ? job.SourceAudioBitRates[audio] : 128_000;
            startInfo.ArgumentList.Add($"-b:a:{audio}");
            startInfo.ArgumentList.Add(audioBitRate.ToString(CultureInfo.InvariantCulture));
		}
		if (job.AudioStreamCount > 0)
		{
			startInfo.ArgumentList.Add("-c:a");
			startInfo.ArgumentList.Add(Path.GetExtension(temporaryPath).ToLowerInvariant() switch { ".webm" => "libopus", ".wmv" => "wmav2", _ => "aac" });
		}
        }
		startInfo.ArgumentList.Add(temporaryPath);
		using var process = new Process { StartInfo = startInfo };
		if (!process.Start())
			throw new InvalidOperationException(Strings.VideoEditorFfmpegStartFailed.GetLocalizedResource());

		var errors = new Queue<string>();
		var errorReadTask = DrainErrorsAsync(process, errors, cancellationToken);
		try
		{
			while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
			{
				var separator = line.IndexOf('=');
				if (separator <= 0)
					continue;

				var key = line[..separator];
				var value = line[(separator + 1)..];
				if (key == "progress" && value == "end")
				{
					reportProgress(0.99);
					continue;
				}

				if (key == "out_time_us" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var elapsedMicroseconds))
					reportProgress(Math.Clamp(elapsedMicroseconds / (expectedDuration * 1_000_000), 0, 0.99));
			}

			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			await errorReadTask.ConfigureAwait(false);
			if (process.ExitCode != 0)
				throw new InvalidDataException(errors.Count == 0
					? string.Format(Strings.VideoEditorFfmpegFailed.GetLocalizedResource(), process.ExitCode)
					: string.Join(Environment.NewLine, errors));
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
			throw;
		}
	}

	private static async Task DrainErrorsAsync(Process process, Queue<string> errors, CancellationToken cancellationToken)
	{
		while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
		{
			lock (errors)
			{
				if (errors.Count == 20)
					errors.Dequeue();
				errors.Enqueue(line);
			}
		}
	}

	private static void CheckAvailableSpace(FileInfo source, string directory, double expectedDuration, double sourceDuration)
	{
		var root = Path.GetPathRoot(directory);
		if (string.IsNullOrWhiteSpace(root))
			return;

		long availableBytes;
		try
		{
			availableBytes = new DriveInfo(root).AvailableFreeSpace;
		}
		catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			// The processing and final atomic replacement remain authoritative when the volume cannot be queried.
			return;
		}

		var ratio = Math.Clamp(expectedDuration / sourceDuration, 0, 1);
		var estimatedBytes = (long)(source.Length * ratio * 1.25) + 64L * 1024 * 1024;
		if (availableBytes < estimatedBytes)
		{
			var estimatedGb = estimatedBytes / 1_000_000_000d;
			var availableGb = availableBytes / 1_000_000_000d;
			throw new IOException(string.Format(Strings.VideoEditorInsufficientSpace.GetLocalizedResource(), estimatedGb, availableGb));
		}
	}

	private static void ValidateOutput(VideoCutJob job, VideoMetadata result, double expectedDuration, double? durationTolerance = null)
	{
		if (!string.Equals(GetVideoEncoder(job).Codec, result.VideoCodec, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException(Strings.VideoEditorCodecMismatch.GetLocalizedResource());

		var tolerance = durationTolerance ?? Math.Max(0.1, job.SourceFrameRate > 0 ? 2 / job.SourceFrameRate : 0.1);
		if (result.DurationSeconds <= 0 || Math.Abs(result.DurationSeconds - expectedDuration) > tolerance)
			throw new InvalidDataException(string.Format(Strings.VideoEditorDurationMismatch.GetLocalizedResource(), expectedDuration, result.DurationSeconds));

		if (result.Width != job.SourceWidth || result.Height != job.SourceHeight)
			throw new InvalidDataException(Strings.VideoEditorDimensionsMismatch.GetLocalizedResource());
	}

	private static (string Encoder, string Codec) GetVideoEncoder(VideoCutJob job) => job.SourceVideoCodec.ToLowerInvariant() switch
	{
		"h264" => ("libx264", "h264"),
		"hevc" => ("libx265", "hevc"),
		"vp9" => ("libvpx-vp9", "vp9"),
		"vp8" => ("libvpx", "vp8"),
		"av1" => ("libsvtav1", "av1"),
		"mpeg4" => ("mpeg4", "mpeg4"),
		"mpeg2video" => ("mpeg2video", "mpeg2video"),
		"wmv2" or "wmv3" => ("wmv2", "wmv2"),
		_ => ("libx264", "h264")
	};

	private static void RestoreBackupIfNeeded(string sourcePath, string temporaryPath, string backupPath)
	{
		if (!File.Exists(backupPath))
			return;

		try
		{
			if (File.Exists(sourcePath))
				File.Move(sourcePath, temporaryPath, overwrite: true);
			File.Move(backupPath, sourcePath, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Preserve the backup file if rollback is blocked by a filesystem error.
		}
	}

	private static bool TryDeleteTemporaryFile(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
            return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
            App.Logger.LogWarning(ex, "Unable to clean partial export {Path}", path);
            return false;
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

	private static string FormatSeconds(double seconds)
		=> seconds.ToString("0.######", CultureInfo.InvariantCulture);
}
