// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;
using System.IO;

namespace Files.App.Services.ResourceManager;

public sealed record VideoAssistantActor(string Name, string Path, string Aliases, string? PosterPath);

public sealed class VideoAssistantCandidate
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string FolderPath { get; init; } = string.Empty;
    public string ActorName { get; init; } = string.Empty;
    public string ActorPath { get; init; } = string.Empty;
    public string? PosterPath { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public ResourceVideoWatchStatus WatchStatus { get; init; }
    public string Reason { get; init; } = string.Empty;
    public IReadOnlyList<string> LocationPaths { get; init; } = [];
    public IReadOnlyList<ResourceBrowserLocationKind> LocationKinds { get; init; } = [];
    public IReadOnlyList<string> LocationTitles { get; init; } = [];
}

public sealed class VideoAssistantSearchService
{
    private const int MaxResults = 6;
    private const int MaxDepth = 32;

    private readonly IResourceBrowserService _browser;
    private readonly IResourceWorkspaceService _workspace;

    public VideoAssistantSearchService(IResourceBrowserService browser, IResourceWorkspaceService workspace)
    {
        _browser = browser;
        _workspace = workspace;
    }

    public async Task<IReadOnlyList<VideoAssistantActor>> GetActorsAsync(string libraryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
            return [];

        var children = await _browser.GetChildrenAsync(libraryPath, ResourceBrowserLocationKind.LibraryRoot, _workspace.Settings, cancellationToken);
        return children
            .Where(item => item.Kind == ResourceBrowserItemKind.ActorFolder)
            .Select(item =>
            {
                var details = _workspace.GetActorDetails(item.Path);
                return new VideoAssistantActor(
                    string.IsNullOrWhiteSpace(details.Name) ? item.Name : details.Name,
                    item.Path,
                    details.Aliases,
                    item.PosterPath);
            })
            .OrderBy(actor => actor.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<VideoAssistantCandidate>> SearchAsync(
        string libraryPath,
        string? actorPath,
        VideoAssistantWatchFilter watchFilter,
        string? resourceTagId,
        string? keyword,
        IReadOnlySet<string>? excludedPaths = null,
        CancellationToken cancellationToken = default)
    {
        var actors = await GetActorsAsync(libraryPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(actorPath))
            actors = actors.Where(actor => PathEquals(actor.Path, actorPath)).ToArray();

        if (actors.Count == 0)
            return [];

        var candidates = new List<VideoAssistantCandidate>();
        var rootTrail = new List<ResourceAssistantLocation>
        {
            new(libraryPath, ResourceBrowserLocationKind.LibraryRoot, libraryPath),
        };

        foreach (var actor in actors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actorTrail = new List<ResourceAssistantLocation>(rootTrail)
            {
                new(actor.Path, ResourceBrowserLocationKind.ActorFolder, actor.Name),
            };
            await CollectActorVideosAsync(actor, actor.Path, ResourceBrowserLocationKind.ActorFolder, actorTrail, candidates, 0, cancellationToken);
        }

        var terms = SplitTerms(keyword);
        var filtered = candidates
            .Where(candidate => excludedPaths is null || !excludedPaths.Contains(candidate.Path))
            .Where(candidate => MatchesWatchFilter(candidate.WatchStatus, watchFilter))
            .Where(candidate => string.IsNullOrWhiteSpace(resourceTagId) ||
                _workspace.GetResourceTagIds(candidate.Path).Contains(resourceTagId, StringComparer.OrdinalIgnoreCase))
            .Where(candidate => terms.Length == 0 || MatchesKeyword(candidate, terms))
            .ToList();

        return filtered
            .OrderBy(_ => Random.Shared.Next())
            .Take(MaxResults)
            .ToArray();
    }

    private async Task CollectActorVideosAsync(
        VideoAssistantActor actor,
        string folderPath,
        ResourceBrowserLocationKind locationKind,
        List<ResourceAssistantLocation> trail,
        List<VideoAssistantCandidate> candidates,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaxDepth)
            return;

        var children = await _browser.GetChildrenAsync(folderPath, locationKind, _workspace.Settings, cancellationToken);
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Kind == ResourceBrowserItemKind.VideoFolder)
            {
                var videoFolderTrail = new List<ResourceAssistantLocation>(trail)
                {
                    new(child.Path, ResourceBrowserLocationKind.VideoFolder, child.Name),
                };
                var videoFiles = await _browser.GetChildrenAsync(child.Path, ResourceBrowserLocationKind.VideoFolder, _workspace.Settings, cancellationToken);
                foreach (var video in videoFiles.Where(item => item.Kind == ResourceBrowserItemKind.VideoFile))
                {
                    var tags = _workspace.GetResourceTagsByIds(_workspace.GetResourceTagIds(video.Path));
                    var tagNames = tags.Select(tag => tag.Name).ToArray();
                    var watchStatus = _workspace.GetVideoWatchStatus(video.Path);

                    candidates.Add(new VideoAssistantCandidate
                    {
                        Name = Path.GetFileNameWithoutExtension(video.Name),
                        Path = video.Path,
                        FolderPath = child.Path,
                        ActorName = actor.Name,
                        ActorPath = actor.Path,
                        PosterPath = video.PosterPath,
                        Tags = tagNames,
                        WatchStatus = watchStatus,
                        Reason = tagNames.Length > 0 ? string.Join(" · ", tagNames) : "资源库匹配",
                        LocationPaths = videoFolderTrail.Select(location => location.Path).ToArray(),
                        LocationKinds = videoFolderTrail.Select(location => location.Kind).ToArray(),
                        LocationTitles = videoFolderTrail.Select(location => location.Title).ToArray(),
                    });
                }
            }
            else if (child.Kind == ResourceBrowserItemKind.CategoryFolder)
            {
                var categoryTrail = new List<ResourceAssistantLocation>(trail)
                {
                    new(child.Path, ResourceBrowserLocationKind.CategoryFolder, child.Name),
                };
                await CollectActorVideosAsync(actor, child.Path, ResourceBrowserLocationKind.CategoryFolder, categoryTrail, candidates, depth + 1, cancellationToken);
            }
        }
    }

    private static bool MatchesWatchFilter(ResourceVideoWatchStatus status, VideoAssistantWatchFilter filter)
        => filter switch
        {
            VideoAssistantWatchFilter.Watched => status == ResourceVideoWatchStatus.Watched,
            VideoAssistantWatchFilter.NotMarkedWatched => status != ResourceVideoWatchStatus.Watched,
            _ => true,
        };

    private static bool MatchesKeyword(VideoAssistantCandidate candidate, IReadOnlyList<string> terms)
    {
        var searchableText = string.Join(' ', new[] { candidate.Name, candidate.ActorName, candidate.Path }
            .Concat(candidate.Tags));
        return terms.All(term => searchableText.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] SplitTerms(string? keyword)
        => (keyword ?? string.Empty)
            .Split([' ', '\t', '\r', '\n', ',', '，', '。', '！', '？'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 1)
            .ToArray();

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record ResourceAssistantLocation(string Path, ResourceBrowserLocationKind Kind, string Title);
}

public enum VideoAssistantWatchFilter
{
    Any,
    Watched,
    NotMarkedWatched,
}
