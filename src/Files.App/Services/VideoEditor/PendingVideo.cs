// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Files.App.Services.VideoEditor;

public sealed class PendingVideo(string sourcePath) : ObservableObject
{
    private bool _isCurrentEditing;
    public bool IsCurrentEditing
    {
        get => _isCurrentEditing;
        set { if (SetProperty(ref _isCurrentEditing, value)) OnPropertyChanged(nameof(QueueStatusText)); }
    }
    public string QueueStatusText => (IsCurrentEditing ? Strings.VideoEditorCurrentlyEditing : Strings.VideoEditorPendingStatus).GetLocalizedResource();
	public string SourcePath { get; } = sourcePath;
	public string FileName => Path.GetFileName(SourcePath);
	public double? TrimStartSeconds { get; set; }
	public double? TrimEndSeconds { get; set; }
	public double CurrentPositionSeconds { get; set; }
	public VideoSegment[]? Segments { get; set; }
}
