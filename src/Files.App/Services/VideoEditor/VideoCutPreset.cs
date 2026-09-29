// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.VideoEditor;

public sealed record VideoCutPreset(
	string Name,
	double HeadTrimSeconds,
	double TailTrimSeconds,
	int UseCount,
	DateTimeOffset LastUsed)
{
	public string TrimSummary => string.Format(
		Strings.VideoEditorPresetSummary.GetLocalizedResource(),
		VideoCutJob.FormatTime(HeadTrimSeconds),
		VideoCutJob.FormatTime(TailTrimSeconds));
}
