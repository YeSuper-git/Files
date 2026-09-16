// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Files.App.Services.ResourceManager;

public sealed class ResourceBrowserService : IResourceBrowserService
{
    private readonly IResourceWorkspaceService _workspace;
    private readonly ILogger<ResourceBrowserService> _logger;

    public ResourceBrowserService(IResourceWorkspaceService workspace, ILogger<ResourceBrowserService> logger)
    {
        _workspace = workspace;
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
            foreach (var child in directory.EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldSkipDirectory(child))
                    continue;

                var kind = locationKind == ResourceBrowserLocationKind.LibraryRoot
                    ? ResourceBrowserItemKind.ActorFolder
                    : HasDirectVideo(child, settings.VideoExtensions, cancellationToken)
                        ? ResourceBrowserItemKind.VideoFolder
                        : ResourceBrowserItemKind.CategoryFolder;

                result.Add(new ResourceBrowserItem
                {
                    Name = child.Name,
                    Path = child.FullName,
                    Kind = kind,
                    PosterPath = kind is ResourceBrowserItemKind.ActorFolder or ResourceBrowserItemKind.VideoFolder
                        ? ResolvePoster(child.FullName, child, child.Name, settings)
                        : null,
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogWarning(ex, "Unable to enumerate resource folders at {Path}", directory.FullName); }

        return result
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
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
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
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
        var overridePath = _workspace.GetPosterOverride(targetPath);
        if (IsImageFile(overridePath, settings.ImageExtensions))
            return overridePath;

        try
        {
            var candidates = directory.EnumerateFiles()
                .Where(file => IsImageFile(file.FullName, settings.ImageExtensions))
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
