// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.App.Services.VideoEditor;

public sealed class PendingVideo(string sourcePath)
{
	public string SourcePath { get; } = sourcePath;
	public string FileName => Path.GetFileName(SourcePath);
	public double? TrimStartSeconds { get; set; }
	public double? TrimEndSeconds { get; set; }
	public double CurrentPositionSeconds { get; set; }
	public VideoSegment[]? Segments { get; set; }
}
