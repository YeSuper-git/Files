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
