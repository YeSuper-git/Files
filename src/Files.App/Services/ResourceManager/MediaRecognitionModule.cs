// Copyright (c) Files Community. Licensed under the MIT License.
using Files.App.Data.Models.ResourceManager;
using Files.App.UserControls.ResourceManager;
using System.IO;

namespace Files.App.Services.ResourceManager;

public static class MediaRecognitionModule
{
    public static ResourceMediaImportDialog CreateDialog(string folder, IResourceWorkspaceService workspace, string? selectedVideo, bool animeLibrary)
        => new(folder, workspace, selectedVideo, animeLibrary);

    public static IReadOnlyList<string> GetTargets(string folder, ResourceSettings settings, bool animeLibrary)
        => animeLibrary ? AnimeLibraryService.GetEpisodeTargets(folder, settings)
            : Directory.EnumerateFiles(folder)
                .Where(path => settings.VideoExtensions.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), new EpisodeNameComparer()).ToArray();

    public static string GetImageNumber(string video, IReadOnlyList<string> videos, bool animeLibrary)
        => animeLibrary ? AnimeLibraryService.GetIllustrationNumber(video, videos)
            : (Enumerable.Range(0, videos.Count).FirstOrDefault(index => string.Equals(videos[index], video, StringComparison.OrdinalIgnoreCase), -1) + 1).ToString("00");
}
