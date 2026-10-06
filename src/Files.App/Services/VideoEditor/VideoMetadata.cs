// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.VideoEditor;

public sealed record VideoMetadata(
	double DurationSeconds,
	string VideoCodec,
	int Width,
	int Height,
	double FrameRate,
	int AudioStreamCount = 0,
    long VideoBitRate = 0,
    long TotalBitRate = 0,
    IReadOnlyList<long>? AudioBitRates = null);
