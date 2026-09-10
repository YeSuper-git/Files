// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.AvManager;

namespace Files.App.Services.AvManager;

public interface IAvWorkspaceService
{
    AvSettings Settings { get; }
    string LibraryPath { get; }
    IReadOnlyList<string> RecentLibraries { get; }

    void SetLibraryPath(string path);
    void UpdateSettings(AvSettings settings);
}
