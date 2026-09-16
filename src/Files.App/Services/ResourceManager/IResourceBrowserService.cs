// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;

namespace Files.App.Services.ResourceManager;

public interface IResourceBrowserService
{
    Task<IReadOnlyList<ResourceBrowserItem>> GetChildrenAsync(
        string path,
        ResourceBrowserLocationKind locationKind,
        ResourceSettings settings,
        CancellationToken cancellationToken = default);
}
