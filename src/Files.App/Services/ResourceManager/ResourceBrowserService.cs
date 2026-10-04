// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Files.App.Services.ResourceManager;

public sealed class ResourceBrowserService : IResourceBrowserService
{
    private readonly bool _animeLibrary;
    private readonly IResourceWorkspaceService _workspace;
    private readonly ILogger<ResourceBrowserService> _logger;

    public ResourceBrowserService(IResourceWorkspaceService workspace, ILogger<ResourceBrowserService> logger, bool animeLibrary = false)
    {
        _workspace = workspace;
        _animeLibrary = animeLibrary;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ResourceBrowserItem>> GetChildrenAsync(
        string path,
        ResourceBrowserLocationKind locationKind,
        ResourceSettings settings,
        CancellationToken cancellationToken = default)
    {
        var effectiveSettings = settings.Clone();
        effectiveSettings.Normalize();

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
                return (IReadOnlyList<ResourceBrowserItem>)Array.Empty<ResourceBrowserItem>();

            return locationKind == ResourceBrowserLocationKind.VideoFolder
                ? GetVideoFiles(directory, effectiveSettings, cancellationToken)
                : GetFolders(directory, locationKind, effectiveSettings, cancellationToken);
        }, cancellationToken);
    }

    private IReadOnlyList<ResourceBrowserItem> GetFolders(
        DirectoryInfo directory,
        ResourceBrowserLocationKind locationKind,
        ResourceSettings settings,
        CancellationToken cancellationToken)
    {
        var result = new List<ResourceBrowserItem>();
        try
        {
            IEnumerable<DirectoryInfo> folders = directory.EnumerateDirectories();
            if (_animeLibrary && settings.AnimeFlattenSeasons && locationKind == ResourceBrowserLocationKind.ActorFolder)
            {
                var flattened = new List<DirectoryInfo>();
                void Visit(DirectoryInfo folder, int depth)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (depth > 8 || ShouldSkipDirectory(folder) || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
                    var children = folder.EnumerateDirectories().Where(child => !ShouldSkipDirectory(child) && !child.Attributes.HasFlag(FileAttributes.ReparsePoint)).ToArray();
                    if (children.Length == 0 || folder.EnumerateFiles().Any(file => settings.VideoExtensions.Contains(file.Extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase)))
                        flattened.Add(folder);
                    foreach (var child in children) Visit(child, depth + 1);
                }
                foreach (var folder in folders) Visit(folder, 0);
                folders = flattened;
            }
            foreach (var child in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldSkipDirectory(child))
                    continue;

                if (locationKind == ResourceBrowserLocationKind.LibraryRoot && _workspace.IsActorFolderHidden(child.FullName))
                    continue;

                var kind = locationKind == ResourceBrowserLocationKind.LibraryRoot
                    ? (_animeLibrary ? ResourceBrowserItemKind.CategoryFolder : ResourceBrowserItemKind.ActorFolder)
                    : (_animeLibrary && locationKind == ResourceBrowserLocationKind.ActorFolder) || IsVideoFolder(child, settings, cancellationToken)
                        ? ResourceBrowserItemKind.VideoFolder
                        : ResourceBrowserItemKind.CategoryFolder;

                result.Add(new ResourceBrowserItem
                {
                    Name = _animeLibrary && settings.AnimeFlattenSeasons && locationKind == ResourceBrowserLocationKind.ActorFolder ? AnimeLibraryService.GetSeasonTitle(child.FullName) : child.Name,
                    Path = child.FullName,
                    Kind = kind,
                    PosterPath = _animeLibrary || kind is ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder
                        ? ResolvePoster(child.FullName, child, child.Name, settings)
                        : null,
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to enumerate resource folders at {Path}", directory.FullName); }

        return result
            .OrderBy(x => x.Name, _animeLibrary ? new EpisodeNameComparer() : StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<ResourceBrowserItem> GetVideoFiles(
        DirectoryInfo directory,
        ResourceSettings settings,
        CancellationToken cancellationToken)
    {
        var result = new List<ResourceBrowserItem>();
        var extensions = settings.VideoExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in directory.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!extensions.Contains(file.Extension.TrimStart('.')))
                    continue;

                result.Add(new ResourceBrowserItem
                {
                    Name = file.Name,
                    Path = file.FullName,
                    Kind = ResourceBrowserItemKind.VideoFile,
                    PosterPath = ResolvePoster(file.FullName, directory, Path.GetFileNameWithoutExtension(file.Name), settings),
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to enumerate resource videos at {Path}", directory.FullName); }

        return result
            .OrderBy(x => x.Name, _animeLibrary ? new EpisodeNameComparer() : StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private bool IsVideoFolder(DirectoryInfo directory, ResourceSettings settings, CancellationToken token)
    {
        if (HasDirectVideo(directory, settings.VideoExtensions, token) || new ResourceCodeParser().ParseCode(directory.Name) is not null)
            return true;
        if (directory.Name is "无中文字幕" or "中文字幕" or "无中字" or "有中字") return false;
        // Leaf work folders retain their identity when a video is missing or has been removed.
        try { return !directory.EnumerateDirectories().Any(child => !ShouldSkipDirectory(child)); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool HasDirectVideo(DirectoryInfo directory, IReadOnlyCollection<string> extensions, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var file in directory.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (extensions.Contains(file.Extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "Unable to inspect resource folder {Path}", directory.FullName); }

        return false;
    }

    private string? ResolvePoster(string targetPath, DirectoryInfo directory, string targetName, ResourceSettings settings)
    {
        var excludedPosterPaths = _workspace.GetActorDetails(targetPath).ExcludedPosterPaths ?? [];
        var overridePath = _workspace.GetPosterOverride(targetPath);
        if (IsImageFile(overridePath, settings.ImageExtensions) &&
            !excludedPosterPaths.Contains(overridePath!, StringComparer.OrdinalIgnoreCase))
            return overridePath;

        try
        {
            if (_animeLibrary)
            {
                if (File.Exists(targetPath))
                    return AnimeLibraryService.ResolveEpisodePoster(targetPath, directory.FullName, directory.FullName, settings, null);
                var seriesCover = directory.EnumerateFiles().FirstOrDefault(file => IsImageFile(file.FullName, settings.ImageExtensions)
                    && AnimeLibraryService.IsSeriesCover(file.FullName, directory.FullName)
                    && !excludedPosterPaths.Contains(file.FullName, StringComparer.OrdinalIgnoreCase));
                if (seriesCover is not null) return seriesCover.FullName;
                string? FirstEpisode(DirectoryInfo folder, int depth)
                {
                    if (depth > 8 || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
                    var video = folder.EnumerateFiles().Where(file => settings.VideoExtensions.Contains(file.Extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                        .OrderBy(file => file.Name, new EpisodeNameComparer()).FirstOrDefault();
                    if (video is not null) return video.FullName;
                    foreach (var child in folder.EnumerateDirectories().Where(child => !ShouldSkipDirectory(child)).OrderBy(child => child.Name, new EpisodeNameComparer()))
                    {
                        var episode = FirstEpisode(child, depth + 1);
                        if (episode is not null) return episode;
                    }
                    return null;
                }
                var firstEpisode = FirstEpisode(directory, 0);
                if (firstEpisode is not null)
                {
                    var matched = AnimeLibraryService.ResolveEpisodePoster(firstEpisode, Path.GetDirectoryName(firstEpisode)!, directory.FullName, settings, null);
                    if (matched is not null) return matched;
                }
            }
            var candidates = directory.EnumerateFiles()
                .Where(file => IsImageFile(file.FullName, settings.ImageExtensions))
                .Where(file => !excludedPosterPaths.Contains(file.FullName, StringComparer.OrdinalIgnoreCase))
                .Select(file => new
                {
                    File = file,
                    Score = SimilarityScore(Path.GetFileNameWithoutExtension(file.Name), targetName),
                })
                .OrderBy(x => x.Score)
                .ThenByDescending(x => x.File.Length)
                .Select(x => x.File.FullName)
                .ToList();

            return candidates.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to resolve resource poster for {Path}", targetPath);
            return null;
        }
    }

    private static int SimilarityScore(string candidateName, string targetName)
    {
        var candidate = NormalizeName(candidateName);
        var target = NormalizeName(targetName);
        if (candidate.Length == 0 || target.Length == 0)
            return 20;
        if (candidate.Equals(target, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (candidate.StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (candidate.Contains(target, StringComparison.OrdinalIgnoreCase) ||
            target.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (candidate.Contains("poster", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains("cover", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains("fanart", StringComparison.OrdinalIgnoreCase))
            return 3;
        return 10;
    }

    private static string NormalizeName(string value)
        => new(value.Where(char.IsLetterOrDigit).ToArray());

    private static bool IsImageFile(string? path, IReadOnlyCollection<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        return extensions.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipDirectory(DirectoryInfo directory)
    {
        if (directory.Name.StartsWith('.') || directory.Name.Equals("@eaDir", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            return directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }
}
