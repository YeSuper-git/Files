// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text.Json;
using Files.App.Data.Models.ResourceManager;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace Files.App.Services.ResourceManager;

/// <summary>
/// Stores Resource Manager preferences in the app-local settings directory. The
/// file is written atomically so a process interruption cannot leave a partial
/// JSON document behind.
/// </summary>
public sealed class ResourceWorkspaceService : IResourceWorkspaceService
{
    private const int MaxRecentLibraries = 8;
    private const string StateFileName = "files-resource-workspace.json";

    private readonly ILogger<ResourceWorkspaceService> _logger;
    private readonly string _statePath;
    private ResourceWorkspaceState _state;

    public ResourceWorkspaceService(ILogger<ResourceWorkspaceService> logger)
    {
        _logger = logger;
        _statePath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            Constants.LocalSettings.SettingsFolderName,
            StateFileName);
        _state = LoadState();
    }

    public ResourceSettings Settings => _state.Settings;

    public string LibraryPath => _state.LibraryPath;

    public IReadOnlyList<string> RecentLibraries => _state.RecentLibraries;

    public IReadOnlyDictionary<string, string> PosterOverrides => _state.PosterOverrides;

    public IReadOnlyList<string> HiddenActorFolders => _state.HiddenActorFolders;

    public IReadOnlyList<ResourceTagDefinition> ResourceTags => _state.ResourceTags;

    public IReadOnlyList<string> GetResourceTagIds(string itemPath)
    {
        try
        {
            var normalizedPath = NormalizePath(itemPath);
            return _state.ResourceTagAssignments.TryGetValue(normalizedPath, out var tagIds)
                ? tagIds.ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<ResourceTagDefinition> GetResourceTagsByIds(IEnumerable<string>? tagIds)
    {
        if (tagIds is null)
            return [];

        var ids = tagIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _state.ResourceTags
            .Where(tag => ids.Contains(tag.Uid))
            .Select(tag => tag.Clone())
            .ToArray();
    }

    public ResourceTagDefinition? GetResourceTagById(string uid)
        => string.IsNullOrWhiteSpace(uid)
            ? null
            : _state.ResourceTags.FirstOrDefault(tag => string.Equals(tag.Uid, uid.Trim(), StringComparison.OrdinalIgnoreCase))?.Clone();

    public ResourceTagDefinition? GetResourceTagByName(string name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : _state.ResourceTags.FirstOrDefault(tag => string.Equals(tag.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Clone();

    public ResourceTagDefinition CreateResourceTag(string name, string color)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
            throw new ArgumentException("资源标签名称不能为空。", nameof(name));

        if (GetResourceTagByName(normalizedName) is not null)
            throw new InvalidOperationException($"资源标签“{normalizedName}”已存在。");

        var tag = new ResourceTagDefinition
        {
            Name = normalizedName,
            Color = string.IsNullOrWhiteSpace(color) ? "#0072BD" : color,
        };
        _state.ResourceTags.Add(tag);
        PersistState();
        return tag.Clone();
    }

    public bool EditResourceTag(string uid, string name, string color)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return false;

        var tag = _state.ResourceTags.FirstOrDefault(item => string.Equals(item.Uid, uid.Trim(), StringComparison.OrdinalIgnoreCase));
        var normalizedName = name.Trim();
        if (tag is null || normalizedName.Length == 0)
            return false;

        if (_state.ResourceTags.Any(item => !ReferenceEquals(item, tag) &&
            string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            return false;

        tag.Name = normalizedName;
        tag.Color = string.IsNullOrWhiteSpace(color) ? tag.Color : color;
        PersistState();
        return true;
    }

    public bool DeleteResourceTag(string uid)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return false;

        var normalizedUid = uid.Trim();
        var removed = _state.ResourceTags.RemoveAll(tag => string.Equals(tag.Uid, normalizedUid, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
            return false;

        foreach (var assignment in _state.ResourceTagAssignments.Values)
            assignment.RemoveAll(tagId => string.Equals(tagId, normalizedUid, StringComparison.OrdinalIgnoreCase));

        _state.ResourceTagAssignments = _state.ResourceTagAssignments
            .Where(pair => pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        PersistState();
        return true;
    }

    public void SetResourceTagIds(string itemPath, IEnumerable<string>? tagIds)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
            return;

        try
        {
            var normalizedPath = NormalizePath(itemPath);
            var validIds = (tagIds ?? [])
                .Where(tagId => !string.IsNullOrWhiteSpace(tagId))
                .Where(tagId => _state.ResourceTags.Any(tag => string.Equals(tag.Uid, tagId, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validIds.Count == 0)
                _state.ResourceTagAssignments.Remove(normalizedPath);
            else
                _state.ResourceTagAssignments[normalizedPath] = validIds;

            PersistState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to save resource tags for {ItemPath}", itemPath);
        }
    }

    public ResourceActorDetails GetActorDetails(string actorFolderPath)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(actorFolderPath.Trim());
            return _state.ActorDetails.TryGetValue(normalizedPath, out var details)
                ? details.Clone()
                : new ResourceActorDetails { Name = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) };
        }
        catch
        {
            return new ResourceActorDetails();
        }
    }

    public void SetActorDetails(string actorFolderPath, ResourceActorDetails details)
    {
        if (string.IsNullOrWhiteSpace(actorFolderPath) || details is null)
            return;

        try
        {
            var normalizedPath = Path.GetFullPath(actorFolderPath.Trim());
            var normalizedDetails = details.Clone();
            normalizedDetails.Name = normalizedDetails.Name.Trim();
            normalizedDetails.Aliases = normalizedDetails.Aliases.Trim();
            normalizedDetails.HeightCm = normalizedDetails.HeightCm.Trim();
            normalizedDetails.WeightKg = normalizedDetails.WeightKg.Trim();
            normalizedDetails.Bust = normalizedDetails.Bust.Trim();
            normalizedDetails.Waist = normalizedDetails.Waist.Trim();
            normalizedDetails.Hip = normalizedDetails.Hip.Trim();
            normalizedDetails.CupSize = normalizedDetails.CupSize.Trim().ToUpperInvariant();
            _state.ActorDetails[normalizedPath] = normalizedDetails;
            PersistState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to save actor details for {ActorFolderPath}", actorFolderPath);
        }
    }

    public void SetLibraryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var normalizedPath = Path.GetFullPath(path.Trim());
        _state.LibraryPath = normalizedPath;
        _state.RecentLibraries.RemoveAll(x => string.Equals(x, normalizedPath, StringComparison.OrdinalIgnoreCase));
        _state.RecentLibraries.Insert(0, normalizedPath);
        _state.RecentLibraries = _state.RecentLibraries
            .Where(Directory.Exists)
            .Take(MaxRecentLibraries)
            .ToList();
        PersistState();
    }

    public void UpdateSettings(ResourceSettings settings)
    {
        var normalized = settings.Clone();
        normalized.Normalize();
        _state.Settings = normalized;
        PersistState();
    }

    public string? GetPosterOverride(string itemPath)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(itemPath.Trim());
            return _state.PosterOverrides.TryGetValue(normalizedPath, out var posterPath) && File.Exists(posterPath)
                ? posterPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void SetPosterOverride(string itemPath, string posterPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath) || string.IsNullOrWhiteSpace(posterPath))
            return;

        try
        {
            var normalizedItemPath = Path.GetFullPath(itemPath.Trim());
            var normalizedPosterPath = Path.GetFullPath(posterPath.Trim());
            if (!File.Exists(normalizedPosterPath))
                return;

            _state.PosterOverrides[normalizedItemPath] = normalizedPosterPath;
            PersistState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to save poster override for {ItemPath}", itemPath);
        }
    }

    public bool IsActorFolderHidden(string itemPath)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(itemPath.Trim());
            return _state.HiddenActorFolders.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void ClearPosterOverride(string itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
            return;

        try
        {
            var normalizedItemPath = Path.GetFullPath(itemPath.Trim());
            if (_state.PosterOverrides.Remove(normalizedItemPath))
                PersistState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to restore automatic poster selection for {ItemPath}", itemPath);
        }
    }

    public void SetActorFolderHidden(string itemPath, bool isHidden)
    {
        if (string.IsNullOrWhiteSpace(itemPath))
            return;

        try
        {
            var normalizedPath = Path.GetFullPath(itemPath.Trim());
            _state.HiddenActorFolders.RemoveAll(x => string.Equals(x, normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (isHidden)
                _state.HiddenActorFolders.Add(normalizedPath);
            PersistState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to update hidden resource folder state for {ItemPath}", itemPath);
        }
    }

    private ResourceWorkspaceState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var state = JsonSerializer.Deserialize(File.ReadAllText(_statePath), ResourceManagerJsonSerializerContext.Default.ResourceWorkspaceState);
                if (state is not null)
                {
                    state.LibraryPath ??= string.Empty;
                    state.Settings ??= new ResourceSettings();
                    state.Settings.Normalize();
                    state.RecentLibraries ??= [];
                    state.RecentLibraries = NormalizeRecentLibraries(state.RecentLibraries);
                    state.PosterOverrides ??= new(StringComparer.OrdinalIgnoreCase);
                    state.PosterOverrides = NormalizePosterOverrides(state.PosterOverrides);
                    state.HiddenActorFolders = NormalizeHiddenActorFolders(state.HiddenActorFolders);
                    state.ActorDetails = NormalizeActorDetails(state.ActorDetails);
                    state.ResourceTags = NormalizeResourceTags(state.ResourceTags);
                    state.ResourceTagAssignments = NormalizeResourceTagAssignments(state.ResourceTagAssignments, state.ResourceTags);
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load resource workspace state");
        }

        return new ResourceWorkspaceState();
    }

    private static List<string> NormalizeRecentLibraries(IEnumerable<string>? paths)
    {
        var normalized = new List<string>();
        foreach (var path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            try
            {
                var fullPath = Path.GetFullPath(path.Trim());
                if (normalized.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                    continue;
                normalized.Add(fullPath);
                if (normalized.Count == MaxRecentLibraries)
                    break;
            }
            catch
            {
                // Ignore one malformed recent path and keep the remaining state.
            }
        }

        return normalized;
    }

    private static Dictionary<string, string> NormalizePosterOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in overrides ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                continue;

            try
            {
                normalized[Path.GetFullPath(pair.Key.Trim())] = Path.GetFullPath(pair.Value.Trim());
            }
            catch
            {
                // Ignore one malformed override and keep the remaining state.
            }
        }

        return normalized;
    }

    private static List<ResourceTagDefinition> NormalizeResourceTags(IEnumerable<ResourceTagDefinition>? tags)
    {
        var normalized = new List<ResourceTagDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in tags ?? [])
        {
            if (tag is null || string.IsNullOrWhiteSpace(tag.Name))
                continue;

            var normalizedTag = tag.Clone();
            normalizedTag.Name = normalizedTag.Name.Trim();
            normalizedTag.Uid = string.IsNullOrWhiteSpace(normalizedTag.Uid) ? Guid.NewGuid().ToString() : normalizedTag.Uid.Trim();
            normalizedTag.Color = string.IsNullOrWhiteSpace(normalizedTag.Color) ? "#0072BD" : normalizedTag.Color;
            if (!names.Add(normalizedTag.Name) || !ids.Add(normalizedTag.Uid))
                continue;
            normalized.Add(normalizedTag);
        }

        return normalized;
    }

    private static Dictionary<string, List<string>> NormalizeResourceTagAssignments(
        IReadOnlyDictionary<string, List<string>>? assignments,
        IReadOnlyCollection<ResourceTagDefinition> tags)
    {
        var validIds = tags.Select(tag => tag.Uid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in assignments ?? new Dictionary<string, List<string>>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;

            try
            {
                var path = NormalizePath(pair.Key);
                var ids = (pair.Value ?? [])
                    .Where(validIds.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (ids.Count > 0)
                    normalized[path] = ids;
            }
            catch
            {
                // Ignore one malformed assignment and preserve valid resource tags.
            }
        }

        return normalized;
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path.Trim());

    private static List<string> NormalizeHiddenActorFolders(IEnumerable<string>? paths)
    {
        var normalized = new List<string>();
        foreach (var path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            try
            {
                var fullPath = Path.GetFullPath(path.Trim());
                if (!normalized.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                    normalized.Add(fullPath);
            }
            catch
            {
                // Ignore one malformed path and preserve valid hidden-folder state.
            }
        }

        return normalized;
    }

    private static Dictionary<string, ResourceActorDetails> NormalizeActorDetails(IReadOnlyDictionary<string, ResourceActorDetails>? actorDetails)
    {
        var normalized = new Dictionary<string, ResourceActorDetails>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in actorDetails ?? new Dictionary<string, ResourceActorDetails>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                continue;

            try
            {
                normalized[Path.GetFullPath(pair.Key.Trim())] = pair.Value.Clone();
            }
            catch
            {
                // Ignore one malformed path and preserve valid actor metadata.
            }
        }

        return normalized;
    }

    private void PersistState()
    {
        var temporaryPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (directory is not null)
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(_state, ResourceManagerJsonSerializerContext.Default.ResourceWorkspaceState));
            File.Move(temporaryPath, _statePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to save resource workspace state");
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // The original state is still intact; nothing else is needed.
            }
        }
    }
}
