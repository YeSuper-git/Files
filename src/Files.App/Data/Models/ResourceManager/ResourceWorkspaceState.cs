// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

/// <summary>
/// Persistent resource workspace state. This is intentionally separate from the
/// library itself so no configuration file is written into the user's video
/// folders.
/// </summary>
public sealed class ResourceWorkspaceState
{
    public int Version { get; set; } = 2;
    public string LibraryPath { get; set; } = string.Empty;
    public List<string> RecentLibraries { get; set; } = [];
    public ResourceSettings Settings { get; set; } = new();
    public Dictionary<string, string> PosterOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> HiddenActorFolders { get; set; } = [];
    public Dictionary<string, ResourceActorDetails> ActorDetails { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ResourceTagDefinition> ResourceTags { get; set; } = [];
    public Dictionary<string, List<string>> ResourceTagAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
