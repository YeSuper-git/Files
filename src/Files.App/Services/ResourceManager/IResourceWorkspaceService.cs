// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;

namespace Files.App.Services.ResourceManager;

public interface IResourceWorkspaceService
{
    ResourceSettings Settings { get; }
    string LibraryPath { get; }
    IReadOnlyList<string> RecentLibraries { get; }

    void SetLibraryPath(string path);
    void UpdateSettings(ResourceSettings settings);
}
