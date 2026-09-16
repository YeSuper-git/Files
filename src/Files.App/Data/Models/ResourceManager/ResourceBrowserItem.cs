// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

public enum ResourceBrowserLocationKind
{
    LibraryRoot,
    ActorFolder,
    CategoryFolder,
    VideoFolder,
}

public enum ResourceBrowserItemKind
{
    ActorFolder,
    CategoryFolder,
    VideoFolder,
    VideoFile,
}

public sealed class ResourceBrowserItem
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? PosterPath { get; init; }
    public ResourceBrowserItemKind Kind { get; init; }
}
