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
    IReadOnlyList<string> HiddenActorFolders { get; }
    IReadOnlyList<ResourceTagDefinition> ResourceTags { get; }

    void SetLibraryPath(string path);
    void UpdateSettings(ResourceSettings settings);
    string? GetPosterOverride(string itemPath);
    void SetPosterOverride(string itemPath, string posterPath);
    ResourceActorDetails GetActorDetails(string actorFolderPath);
    void SetActorDetails(string actorFolderPath, ResourceActorDetails details);
    IReadOnlyList<string> GetResourceTagIds(string itemPath);
    IReadOnlyList<ResourceTagDefinition> GetResourceTagsByIds(IEnumerable<string>? tagIds);
    ResourceTagDefinition? GetResourceTagById(string uid);
    ResourceTagDefinition? GetResourceTagByName(string name);
    ResourceTagDefinition CreateResourceTag(string name, string color);
    bool EditResourceTag(string uid, string name, string color);
    bool DeleteResourceTag(string uid);
    void SetResourceTagIds(string itemPath, IEnumerable<string>? tagIds);
    void ClearPosterOverride(string itemPath);
    bool IsActorFolderHidden(string itemPath);
    void SetActorFolderHidden(string itemPath, bool isHidden);
}
