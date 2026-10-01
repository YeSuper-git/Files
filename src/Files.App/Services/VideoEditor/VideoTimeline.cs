// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Files.App.Services.VideoEditor;

public sealed record VideoSegment(double StartSeconds, double EndSeconds)
{
	public double DurationSeconds => EndSeconds - StartSeconds;
}

public sealed class VideoTimeline
{
	private readonly SegmentCollection _segments = new();
	public ObservableCollection<VideoSegment> Segments => _segments;
	public double DurationSeconds => Math.Round(Segments.Sum(segment => segment.DurationSeconds), 9);
	public void Reset(double duration) => Restore(duration > 0 ? [new VideoSegment(0, duration)] : []);
	public void Restore(IEnumerable<VideoSegment> segments)
	{
		var snapshot = segments.ToArray();
		_segments.ReplaceAll(snapshot);
	}
	public double ToSource(double position)
	{
		position = Math.Clamp(position, 0, DurationSeconds);
		foreach (var segment in Segments)
		{
			if (position < segment.DurationSeconds) return segment.StartSeconds + position;
			position -= segment.DurationSeconds;
		}
		return Segments.LastOrDefault()?.EndSeconds ?? 0;
	}
	public double FromSource(double position)
	{
		double offset = 0;
		foreach (var segment in Segments)
		{
			if (position < segment.EndSeconds) return offset + Math.Max(0, position - segment.StartSeconds);
			offset += segment.DurationSeconds;
		}
		return offset;
	}
	public bool Split(double position, double frameRate)
	{
		var source = ToSource(position);
		if (frameRate > 0) source = Math.Round(source * frameRate) / frameRate;
		var minimum = frameRate > 0 ? 0.5 / frameRate : 0.001;
		var segment = Segments.FirstOrDefault(segment => source > segment.StartSeconds + minimum && source < segment.EndSeconds - minimum);
		if (segment is null) return false;
		var index = Segments.IndexOf(segment);
		var result = Segments.ToList();
		result[index] = new VideoSegment(segment.StartSeconds, source);
		result.Insert(index + 1, new VideoSegment(source, segment.EndSeconds));
		_segments.ReplaceAll(result);
		return true;
	}
	public void KeepOnly(VideoSegment segment)
	{
		if (Segments.Contains(segment)) Restore([segment]);
	}
	private sealed class SegmentCollection : ObservableCollection<VideoSegment>
	{
		public void ReplaceAll(IEnumerable<VideoSegment> segments)
		{
			Items.Clear();
			foreach (var segment in segments) Items.Add(segment);
			OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
			OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
			OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		}
	}
}