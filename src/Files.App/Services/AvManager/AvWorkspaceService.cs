// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text.Json;
using Files.App.Data.Models.AvManager;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace Files.App.Services.AvManager;

/// <summary>
/// Stores AV Manager preferences in the app-local settings directory. The
/// file is written atomically so a process interruption cannot leave a partial
/// JSON document behind.
/// </summary>
public sealed class AvWorkspaceService : IAvWorkspaceService
{
    private const int MaxRecentLibraries = 8;
    private const string StateFileName = "av-workspace.json";

    private readonly ILogger<AvWorkspaceService> _logger;
    private readonly string _statePath;
    private AvWorkspaceState _state;

    public AvWorkspaceService(ILogger<AvWorkspaceService> logger)
    {
        _logger = logger;
        _statePath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            Constants.LocalSettings.SettingsFolderName,
            StateFileName);
        _state = LoadState();
    }

    public AvSettings Settings => _state.Settings;

    public string LibraryPath => _state.LibraryPath;

    public IReadOnlyList<string> RecentLibraries => _state.RecentLibraries;

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

    public void UpdateSettings(AvSettings settings)
    {
        var normalized = settings.Clone();
        normalized.Normalize();
        _state.Settings = normalized;
        PersistState();
    }

    private AvWorkspaceState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var state = JsonSerializer.Deserialize<AvWorkspaceState>(File.ReadAllText(_statePath));
                if (state is not null)
                {
                    state.LibraryPath ??= string.Empty;
                    state.Settings ??= new AvSettings();
                    state.Settings.Normalize();
                    state.RecentLibraries ??= [];
                    state.RecentLibraries = NormalizeRecentLibraries(state.RecentLibraries);
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load AV workspace state");
        }

        return new AvWorkspaceState();
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
                JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _statePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to save AV workspace state");
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
