// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.App.Services.VideoEditor;

public sealed class VideoToolchain
{
	public string? FfmpegPath { get; }
	public string? FfprobePath { get; }

	public bool IsAvailable => FfmpegPath is not null && FfprobePath is not null;

	public string StatusMessage => IsAvailable
		? string.Format(Strings.VideoEditorToolReady.GetLocalizedResource(), FfmpegPath)
		: Strings.VideoEditorToolMissing.GetLocalizedResource();

	public VideoToolchain()
	{
		FfmpegPath = FindExecutable("ffmpeg.exe");
		FfprobePath = FindExecutable("ffprobe.exe");
	}

	private static string? FindExecutable(string name)
	{
		var localCandidates = new[]
		{
			Path.Combine(AppContext.BaseDirectory, "Tools", name),
			Path.Combine(AppContext.BaseDirectory, name),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", name)
		};

		foreach (var candidate in localCandidates)
		{
			if (File.Exists(candidate))
				return candidate;
		}

		var pathValue = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrWhiteSpace(pathValue))
			return null;

		foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			try
			{
				var candidate = Path.Combine(directory.Trim('"'), name);
				if (File.Exists(candidate))
					return candidate;
			}
			catch (ArgumentException)
			{
				// Ignore malformed PATH entries and keep looking.
			}
		}

		return null;
	}
}
