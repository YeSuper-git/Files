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

    public IReadOnlyList<ResourceToolSnapshot> ResourceToolSnapshots => _state.ResourceToolSnapshots
        .OrderByDescending(snapshot => snapshot.CreatedAt)
        .Select(snapshot => snapshot.Clone())
        .ToArray();

    public ResourceVideoWatchStatus GetVideoWatchStatus(string videoPath)
    {
        try
        {
            var normalizedPath = NormalizePath(videoPath);
            return _state.VideoWatchStatuses.TryGetValue(normalizedPath, out var status)
                ? status
                : ResourceVideoWatchStatus.Unknown;
        }
        catch
        {
            return ResourceVideoWatchStatus.Unknown;
        }
    }

    public DateTimeOffset? GetVideoLastWatchedAt(string videoPath)
    {
        try
        {
            var normalizedPath = NormalizePath(videoPath);
            return _state.VideoLastWatchedAt.TryGetValue(normalizedPath, out var watchedAt)
                ? watchedAt
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void SetVideoWatchStatus(string videoPath, ResourceVideoWatchStatus status)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
            return;

        try
        {
            var normalizedPath = NormalizePath(videoPath);
            if (status == ResourceVideoWatchStatus.Unknown)
                _state.VideoWatchStatuses.Remove(normalizedPath);
            else
                _state.VideoWatchStatuses[normalizedPath] = status;
            if (status == ResourceVideoWatchStatus.Watched)
                _state.VideoLastWatchedAt[normalizedPath] = DateTimeOffset.Now;
            PersistState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to save video watch status for {VideoPath}", videoPath);
        }
    }

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

    public bool IsResourceTagAssigned(string tagId)
        => !string.IsNullOrWhiteSpace(tagId)
            && _state.ResourceTagAssignments.Values.Any(tagIds => tagIds.Contains(tagId, StringComparer.OrdinalIgnoreCase));

    public ResourceTagDefinition CreateResourceTag(string name, string color, string? uid = null)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
            throw new ArgumentException("资源标签名称不能为空。", nameof(name));

        if (GetResourceTagByName(normalizedName) is not null)
            throw new InvalidOperationException($"资源标签“{normalizedName}”已存在。");

        var tag = new ResourceTagDefinition
        {
            Uid = string.IsNullOrWhiteSpace(uid) ? Guid.NewGuid().ToString() : uid.Trim(),
            Name = normalizedName,
            Color = string.IsNullOrWhiteSpace(color) ? "#0072BD" : color,
        };
        _state.ResourceTags.Add(tag);
        PersistState();
        return tag.Clone();
    }

    public bool SaveResourceToolSnapshot(ResourceToolSnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Id))
            return false;

        var copy = snapshot.Clone();
        var index = _state.ResourceToolSnapshots.FindIndex(item => string.Equals(item.Id, copy.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var previous = _state.ResourceToolSnapshots[index];
            _state.ResourceToolSnapshots[index] = copy;
            if (PersistState())
                return true;
            _state.ResourceToolSnapshots[index] = previous;
            return false;
        }
        else
            _state.ResourceToolSnapshots.Add(copy);
        if (PersistState())
            return true;
        _state.ResourceToolSnapshots.RemoveAll(item => string.Equals(item.Id, copy.Id, StringComparison.OrdinalIgnoreCase));
        return false;
    }

    public bool DeleteResourceToolSnapshot(string snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
            return false;

        var index = _state.ResourceToolSnapshots.FindIndex(snapshot => string.Equals(snapshot.Id, snapshotId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;

        var removed = _state.ResourceToolSnapshots[index];
        _state.ResourceToolSnapshots.RemoveAt(index);
        if (PersistState())
            return true;
        _state.ResourceToolSnapshots.Insert(index, removed);
        return false;
    }

    public void AddResourceTagToItems(IEnumerable<string> itemPaths, string tagId)
    {
        if (string.IsNullOrWhiteSpace(tagId) || !_state.ResourceTags.Any(tag => string.Equals(tag.Uid, tagId, StringComparison.OrdinalIgnoreCase)))
            return;

        var changed = false;
        foreach (var itemPath in itemPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(itemPath))
                continue;

            try
            {
                var normalizedPath = NormalizePath(itemPath);
                if (!_state.ResourceTagAssignments.TryGetValue(normalizedPath, out var tagIds))
                    _state.ResourceTagAssignments[normalizedPath] = tagIds = [];

                if (!tagIds.Contains(tagId, StringComparer.OrdinalIgnoreCase))
                {
                    tagIds.Add(tagId);
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to add resource tag {TagId} to {ItemPath}", tagId, itemPath);
            }
        }

        if (changed)
            PersistState();
    }

    public void RemoveResourceTagFromItems(IEnumerable<string> itemPaths, string tagId)
    {
        if (string.IsNullOrWhiteSpace(tagId))
            return;

        var changed = false;
        foreach (var itemPath in itemPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(itemPath))
                continue;

            try
            {
                var normalizedPath = NormalizePath(itemPath);
                if (!_state.ResourceTagAssignments.TryGetValue(normalizedPath, out var tagIds))
                    continue;

                changed |= tagIds.RemoveAll(existingTagId => string.Equals(existingTagId, tagId, StringComparison.OrdinalIgnoreCase)) > 0;
                if (tagIds.Count == 0)
                    _state.ResourceTagAssignments.Remove(normalizedPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to remove resource tag {TagId} from {ItemPath}", tagId, itemPath);
            }
        }

        if (changed)
            PersistState();
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

    public void RemapItemPaths(string sourcePath, string targetPath)
        => RemapItemPaths(new[] { (sourcePath, targetPath) });

    public void RemapItemPaths(IEnumerable<(string SourcePath, string TargetPath)> pathMappings)
    {
        if (pathMappings is null)
            return;

        try
        {
            var mappings = pathMappings
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.SourcePath) && !string.IsNullOrWhiteSpace(mapping.TargetPath))
                .Select(mapping => (
                    SourceRoot: Path.TrimEndingDirectorySeparator(Path.GetFullPath(mapping.SourcePath.Trim())),
                    TargetRoot: Path.TrimEndingDirectorySeparator(Path.GetFullPath(mapping.TargetPath.Trim()))))
                .Where(mapping => !string.Equals(mapping.SourceRoot, mapping.TargetRoot, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(mapping => mapping.SourceRoot.Length)
                .ToList();
            if (mappings.Count == 0)
                return;

            var changed = false;

            var posterOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _state.PosterOverrides)
            {
                var itemPath = RemapPath(pair.Key, mappings);
                var posterPath = RemapPath(pair.Value, mappings);
                changed |= !string.Equals(itemPath, pair.Key, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(posterPath, pair.Value, StringComparison.OrdinalIgnoreCase);
                posterOverrides[itemPath] = posterPath;
            }

            var tagAssignments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _state.ResourceTagAssignments)
            {
                var itemPath = RemapPath(pair.Key, mappings);
                changed |= !string.Equals(itemPath, pair.Key, StringComparison.OrdinalIgnoreCase);
                if (!tagAssignments.TryGetValue(itemPath, out var tagIds))
                    tagAssignments[itemPath] = tagIds = [];
                foreach (var tagId in pair.Value)
                {
                    if (!tagIds.Contains(tagId, StringComparer.OrdinalIgnoreCase))
                        tagIds.Add(tagId);
                }
            }

            var videoWatchStatuses = new Dictionary<string, ResourceVideoWatchStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _state.VideoWatchStatuses)
            {
                var itemPath = RemapPath(pair.Key, mappings);
                changed |= !string.Equals(itemPath, pair.Key, StringComparison.OrdinalIgnoreCase);
                videoWatchStatuses[itemPath] = pair.Value;
            }

            var videoLastWatchedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _state.VideoLastWatchedAt)
            {
                var itemPath = RemapPath(pair.Key, mappings);
                changed |= !string.Equals(itemPath, pair.Key, StringComparison.OrdinalIgnoreCase);
                videoLastWatchedAt[itemPath] = pair.Value;
            }

            var videoTitleTranslations = new Dictionary<string, ResourceVideoTitleTranslation>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _state.VideoTitleTranslations)
            {
                var itemKey = RemapVideoTitleTranslationKey(pair.Key, mappings);
                changed |= !string.Equals(itemKey, pair.Key, StringComparison.OrdinalIgnoreCase);
                videoTitleTranslations[itemKey] = pair.Value.Clone();
            }

            var actorDetails = new Dictionary<string, ResourceActorDetails>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _state.ActorDetails)
            {
                var itemPath = RemapPath(pair.Key, mappings);
                var details = pair.Value.Clone();
                details.PosterPaths = details.PosterPaths
                    .Select(path => RemapPath(path, mappings))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                details.ExcludedPosterPaths = (details.ExcludedPosterPaths ?? [])
                    .Select(path => RemapPath(path, mappings))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                changed |= !string.Equals(itemPath, pair.Key, StringComparison.OrdinalIgnoreCase)
                    || !details.PosterPaths.SequenceEqual(pair.Value.PosterPaths, StringComparer.OrdinalIgnoreCase)
                    || !details.ExcludedPosterPaths.SequenceEqual(pair.Value.ExcludedPosterPaths, StringComparer.OrdinalIgnoreCase);
                actorDetails[itemPath] = details;
            }

            var hiddenActorFolders = _state.HiddenActorFolders
                .Select(path => RemapPath(path, mappings))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            changed |= !_state.HiddenActorFolders.SequenceEqual(hiddenActorFolders, StringComparer.OrdinalIgnoreCase);

            if (!changed)
                return;

            _state.PosterOverrides = posterOverrides;
            _state.ResourceTagAssignments = tagAssignments;
            _state.VideoWatchStatuses = videoWatchStatuses;
            _state.VideoLastWatchedAt = videoLastWatchedAt;
            _state.VideoTitleTranslations = videoTitleTranslations;
            _state.ActorDetails = actorDetails;
            _state.HiddenActorFolders = hiddenActorFolders;
            PersistState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to update resource metadata paths for a batch of path mappings");
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

    public ResourceVideoTitleTranslation? GetVideoTitleTranslation(string videoPath)
    {
        try
        {
            var normalizedKey = NormalizeVideoTitleTranslationKey(videoPath);
            return _state.VideoTitleTranslations.TryGetValue(normalizedKey, out var translation)
                ? translation.Clone()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void SetVideoTitleTranslation(string videoPath, string sourceTitle, string translatedTitle)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || string.IsNullOrWhiteSpace(sourceTitle) || string.IsNullOrWhiteSpace(translatedTitle))
            return;

        try
        {
            var normalizedKey = NormalizeVideoTitleTranslationKey(videoPath);
            _state.VideoTitleTranslations[normalizedKey] = new ResourceVideoTitleTranslation
            {
                SourceTitle = sourceTitle.Trim(),
                TranslatedTitle = translatedTitle.Trim(),
                TranslatedAt = DateTimeOffset.Now,
            };
            PersistState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to save translated title for {VideoPath}", videoPath);
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
            normalizedDetails.Biography = normalizedDetails.Biography.Trim();
            normalizedDetails.HeightCm = normalizedDetails.HeightCm.Trim();
            normalizedDetails.WeightKg = normalizedDetails.WeightKg.Trim();
            normalizedDetails.Bust = normalizedDetails.Bust.Trim();
            normalizedDetails.Waist = normalizedDetails.Waist.Trim();
            normalizedDetails.Hip = normalizedDetails.Hip.Trim();
            normalizedDetails.CupSize = normalizedDetails.CupSize.Trim().ToUpperInvariant();
            normalizedDetails.PosterPaths = NormalizeActorPosterPaths(normalizedDetails.PosterPaths);
            normalizedDetails.ExcludedPosterPaths = NormalizeActorPosterPaths(normalizedDetails.ExcludedPosterPaths);
            if (normalizedDetails.CareerRetirementDate is not null)
                normalizedDetails.IsCurrentlyActive = false;
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
                    state.Version = 6;
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
                    state.VideoWatchStatuses = NormalizeVideoWatchStatuses(state.VideoWatchStatuses);
                    state.VideoLastWatchedAt = NormalizeVideoLastWatchedAt(state.VideoLastWatchedAt);
                    state.VideoTitleTranslations = NormalizeVideoTitleTranslations(state.VideoTitleTranslations);
                    state.ResourceToolSnapshots ??= [];
                    state.ResourceToolSnapshots = state.ResourceToolSnapshots
                        .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Id))
                        .Select(snapshot =>
                        {
                            snapshot.LibraryPath ??= string.Empty;
                            snapshot.ScopePath ??= snapshot.LibraryPath;
                            snapshot.Action ??= string.Empty;
                            snapshot.Summary ??= string.Empty;
                            snapshot.Operations ??= [];
                            snapshot.TagAssignments ??= [];
                            foreach (var assignment in snapshot.TagAssignments)
                            {
                                assignment.ItemPath ??= string.Empty;
                                assignment.TagIds ??= [];
                            }
                            return snapshot;
                        })
                        .ToList();
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

    private static Dictionary<string, ResourceVideoWatchStatus> NormalizeVideoWatchStatuses(
        IReadOnlyDictionary<string, ResourceVideoWatchStatus>? statuses)
    {
        var normalized = new Dictionary<string, ResourceVideoWatchStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in statuses ?? new Dictionary<string, ResourceVideoWatchStatus>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is not (ResourceVideoWatchStatus.Watched or ResourceVideoWatchStatus.WantToWatch))
                continue;

            try
            {
                normalized[NormalizePath(pair.Key)] = pair.Value;
            }
            catch
            {
                // Ignore one malformed video path and preserve the rest of the local state.
            }
        }

        return normalized;
    }

    private static Dictionary<string, DateTimeOffset> NormalizeVideoLastWatchedAt(
        IReadOnlyDictionary<string, DateTimeOffset>? timestamps)
    {
        var normalized = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in timestamps ?? new Dictionary<string, DateTimeOffset>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == default)
                continue;

            try
            {
                normalized[NormalizePath(pair.Key)] = pair.Value;
            }
            catch
            {
                // Ignore one malformed video path and preserve the remaining dates.
            }
        }

        return normalized;
    }

    private static Dictionary<string, ResourceVideoTitleTranslation> NormalizeVideoTitleTranslations(
        IReadOnlyDictionary<string, ResourceVideoTitleTranslation>? translations)
    {
        var normalized = new Dictionary<string, ResourceVideoTitleTranslation>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in translations ?? new Dictionary<string, ResourceVideoTitleTranslation>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                string.IsNullOrWhiteSpace(pair.Value.SourceTitle) || string.IsNullOrWhiteSpace(pair.Value.TranslatedTitle))
                continue;

            try
            {
                var translation = pair.Value.Clone();
                translation.SourceTitle = translation.SourceTitle.Trim();
                translation.TranslatedTitle = translation.TranslatedTitle.Trim();
                var normalizedKey = NormalizeVideoTitleTranslationKey(pair.Key);
                if (!normalizedKey.Contains("|provider:", StringComparison.OrdinalIgnoreCase))
                    normalizedKey += "|provider:BailianQwenMt";
                normalized[normalizedKey] = translation;
            }
            catch
            {
                // Ignore one malformed video path and preserve other translations.
            }
        }

        return normalized;
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path.Trim());

    private static string NormalizeVideoTitleTranslationKey(string key)
    {
        var providerMarkerIndex = key.IndexOf("|provider:", StringComparison.OrdinalIgnoreCase);
        var itemKey = providerMarkerIndex >= 0 ? key[..providerMarkerIndex] : key;
        var providerSuffix = providerMarkerIndex >= 0 ? key[providerMarkerIndex..] : string.Empty;
        var normalizedItemKey = IsVideoTitleCodeKey(itemKey)
            ? $"code:{itemKey[5..].Trim().ToUpperInvariant()}"
            : NormalizePath(itemKey);

        return normalizedItemKey + providerSuffix;
    }

    private static string RemapVideoTitleTranslationKey(string key, IReadOnlyList<(string SourceRoot, string TargetRoot)> mappings)
    {
        var providerMarkerIndex = key.IndexOf("|provider:", StringComparison.OrdinalIgnoreCase);
        var itemKey = providerMarkerIndex >= 0 ? key[..providerMarkerIndex] : key;
        var providerSuffix = providerMarkerIndex >= 0 ? key[providerMarkerIndex..] : string.Empty;
        var remappedItemKey = IsVideoTitleCodeKey(itemKey) ? itemKey : RemapPath(itemKey, mappings);
        return remappedItemKey + providerSuffix;
    }

    private static bool IsVideoTitleCodeKey(string key)
        => key.StartsWith("code:", StringComparison.OrdinalIgnoreCase);

    private static string RemapPath(string path, IReadOnlyList<(string SourceRoot, string TargetRoot)> mappings)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path.Trim());
            foreach (var (sourceRoot, targetRoot) in mappings)
            {
                if (string.Equals(normalizedPath, sourceRoot, StringComparison.OrdinalIgnoreCase))
                    return targetRoot;

                var sourcePrefix = sourceRoot + Path.DirectorySeparatorChar;
                if (normalizedPath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(targetRoot, normalizedPath[sourcePrefix.Length..]);
            }
        }
        catch
        {
            // Preserve metadata entries with paths that cannot be normalized.
        }

        return path;
    }

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
                var details = pair.Value.Clone();
                details.PosterPaths = NormalizeActorPosterPaths(details.PosterPaths);
                details.ExcludedPosterPaths = NormalizeActorPosterPaths(details.ExcludedPosterPaths);
                if (details.CareerRetirementDate is not null)
                    details.IsCurrentlyActive = false;
                normalized[Path.GetFullPath(pair.Key.Trim())] = details;
            }
            catch
            {
                // Ignore one malformed path and preserve valid actor metadata.
            }
        }

        return normalized;
    }

    private static List<string> NormalizeActorPosterPaths(IEnumerable<string>? paths)
    {
        var normalized = new List<string>();
        foreach (var path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;

            try
            {
                var fullPath = Path.GetFullPath(path.Trim());
                if (!normalized.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                    normalized.Add(fullPath);
            }
            catch
            {
                // Ignore one invalid poster path without discarding actor metadata.
            }
        }

        return normalized;
    }

    private bool PersistState()
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
            return true;
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
            return false;
        }
    }
}
