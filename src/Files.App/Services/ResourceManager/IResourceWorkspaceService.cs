// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;

namespace Files.App.Services.ResourceManager;

public interface IResourceWorkspaceService
{
    ResourceSettings Settings { get; }
    string LibraryPath { get; }
    IReadOnlyList<string> RecentLibraries { get; }
    IReadOnlyDictionary<string, string> PosterOverrides { get; }

    void SetLibraryPath(string path);
    void UpdateSettings(ResourceSettings settings);
    string? GetPosterOverride(string itemPath);
    void SetPosterOverride(string itemPath, string posterPath);
}
