// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.AvManager;

/// <summary>
/// Persistent AV workspace state. This is intentionally separate from the
/// library itself so no configuration file is written into the user's video
/// folders.
/// </summary>
public sealed class AvWorkspaceState
{
    public int Version { get; set; } = 1;
    public string LibraryPath { get; set; } = string.Empty;
    public List<string> RecentLibraries { get; set; } = [];
    public AvSettings Settings { get; set; } = new();
}
