// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Files.App.Services.VideoEditor;

public sealed class VideoFrameStripService(VideoToolchain toolchain)
{
	private readonly VideoToolchain _toolchain = toolchain;

	public async Task<string?> GetFrameAsync(string sourcePath, double seconds, int index, CancellationToken cancellationToken)
	{
		if (_toolchain.FfmpegPath is not { } ffmpegPath || !File.Exists(sourcePath))
			return null;

		var signature = $"{sourcePath}|{File.GetLastWriteTimeUtc(sourcePath).Ticks}|{new FileInfo(sourcePath).Length}";
		var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
		var directory = Path.Combine(Path.GetTempPath(), "FilesVideoFrameStrip", cacheKey);
		var outputPath = Path.Combine(directory, $"frame-v2-{Math.Round(seconds * 1000):F0}.jpg");
		if (File.Exists(outputPath))
			return outputPath;

		Directory.CreateDirectory(directory);
		var temporaryPath = Path.Combine(directory, $"{Guid.NewGuid():N}.jpg");
		var startInfo = new ProcessStartInfo(ffmpegPath)
		{
			CreateNoWindow = true,
			UseShellExecute = false,
			RedirectStandardError = true
		};
		foreach (var argument in new[]
		{
			"-hide_banner", "-nostdin", "-loglevel", "error", "-threads", "1", "-ss", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
			"-i", sourcePath, "-frames:v", "1", "-vf", "scale=640:108:force_original_aspect_ratio=decrease",
			"-q:v", "5", "-y", temporaryPath
		})
			startInfo.ArgumentList.Add(argument);

		using var process = Process.Start(startInfo);
		if (process is null)
			return null;
		try
		{
			var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);
			await errorTask;
			if (process.ExitCode != 0 || !File.Exists(temporaryPath))
				return null;
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporaryPath, outputPath, overwrite: true);
			return outputPath;
		}
		catch (OperationCanceledException)
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync(CancellationToken.None);
			throw;
		}
		finally
		{
			if (File.Exists(temporaryPath))
				File.Delete(temporaryPath);
		}
	}
}
