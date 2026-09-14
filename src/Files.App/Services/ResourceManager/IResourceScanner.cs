// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;

namespace Files.App.Services.ResourceManager;

public interface IResourceScanner
{
    Task<ResourceScanResult> AnalyzeLibraryAsync(string root, ResourceSettings settings, CancellationToken cancellationToken = default);
}
