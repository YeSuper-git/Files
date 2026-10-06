// Copyright (c) Files Community. Licensed under the MIT License.
using Files.App.Data.Models.ResourceManager;
using System.IO;
using System.Text.RegularExpressions;

namespace Files.App.Services.ResourceManager;

public enum AnimeOptimizationKind { Folders, Posters, Illustrations }

public static class AnimeFormatOptimizationService
{
    public static List<ResourceFileOperation> PreviewAssignments(IReadOnlyDictionary<string, string?> assignments, ResourceSettings settings)
    {
        var result = new List<ResourceFileOperation>();
        foreach (var group in assignments.GroupBy(pair => Path.GetDirectoryName(pair.Key)!, StringComparer.OrdinalIgnoreCase))
        {
            var videos = Directory.EnumerateFiles(group.Key).Where(file => settings.VideoExtensions.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase)).OrderBy(file => Path.GetFileName(file), new EpisodeNameComparer()).ToArray();
            var used = new Dictionary<string, HashSet<int>>();
            foreach (var file in Directory.EnumerateFiles(group.Key))
            {
                if (!settings.ImageExtensions.Contains(Path.GetExtension(file).TrimStart('.'), StringComparer.OrdinalIgnoreCase)) continue;
                var match = Regex.Match(Path.GetFileNameWithoutExtension(file), @"^(\d{2,})插图(?:_.+)? \(([1-9]\d*)\)$");
                if (!match.Success) continue;
                var number = int.Parse(match.Groups[1].Value).ToString();
                if (!used.TryGetValue(number, out var indexes)) used[number] = indexes = [];
                indexes.Add(int.Parse(match.Groups[2].Value));
            }
            foreach (var pair in group.OrderBy(pair => Path.GetFileName(pair.Key), new EpisodeNameComparer()))
            {
                var target = pair.Key;
                string? reason = null;
                var videoIndex = Array.FindIndex(videos, video => string.Equals(video, pair.Value, StringComparison.OrdinalIgnoreCase));
                if (videoIndex < 0) reason = "请选择同文件夹内的对应视频";
                else
                {
                    var number = AnimeLibraryService.GetIllustrationNumber(videos[videoIndex], videos);
                    if (!used.TryGetValue(number, out var indexes)) used[number] = indexes = [];
                    var index = 1; while (indexes.Contains(index)) index++; indexes.Add(index);
                    target = Path.Combine(group.Key, $"{int.Parse(number):D2}插图 ({index}){Path.GetExtension(pair.Key)}");
                    if (!File.Exists(pair.Key)) reason = "插图文件已不存在";
                    else if (File.Exists(target) || Directory.Exists(target)) reason = "目标名称已存在";
                }
                result.Add(new ResourceFileOperation { Operation = "anime_rename", Source = pair.Key, Target = target, Status = reason is null ? "ready" : "conflict", Reason = reason });
            }
        }
        return result;
    }

    public static Task<List<ResourceFileOperation>> PreviewAsync(string scope, ResourceSettings settings, AnimeOptimizationKind kind, bool includeScope, CancellationToken token = default)
        => Task.Run(() =>
        {
            var operations = new List<ResourceFileOperation>();
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string source, string target, string? reason = null)
            {
                if (reason is null && string.Equals(source, target, StringComparison.Ordinal)) return;
                var conflict = reason is not null || !reserved.Add(target) || File.Exists(target) || Directory.Exists(target);
                operations.Add(new ResourceFileOperation { Operation = "anime_rename", Source = source, Target = target, Status = conflict ? "conflict" : "ready", Reason = reason ?? (conflict ? "目标名称已存在" : null) });
            }
            void Visit(DirectoryInfo folder, int depth, bool process)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 10 || folder.Attributes.HasFlag(FileAttributes.ReparsePoint) || folder.Name.StartsWith('.') || folder.Name == "@eaDir") return;
                var files = folder.EnumerateFiles().ToArray();
                var videos = files.Where(file => settings.VideoExtensions.Contains(file.Extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase)).OrderBy(file => file.Name, new EpisodeNameComparer()).ToArray();
                foreach (var child in folder.EnumerateDirectories().OrderBy(child => child.Name, new EpisodeNameComparer())) Visit(child, depth + 1, true);
                if (!process || videos.Length == 0) return;
                if (kind == AnimeOptimizationKind.Folders)
                {
                    if (AnimeLibraryService.IsSeasonFolder(folder.Name)) return;
                    var name = Regex.Replace(folder.Name, @"\s*1\s*-\s*\d+$", "").TrimEnd();
                    if (videos.Length > 1) name += $" 1-{videos.Length}";
                    if (name.Length > 0) Add(folder.FullName, Path.Combine(folder.Parent!.FullName, name));
                    return;
                }
                var images = files.Where(file => settings.ImageExtensions.Contains(file.Extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase)).OrderBy(file => file.Name, new EpisodeNameComparer()).ToArray();
                var posters = images.Where(image => AnimeLibraryService.IsSeriesCover(image.FullName, folder.FullName)).Select(image => image.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var video in videos)
                {
                    var stem = Path.GetFileNameWithoutExtension(video.Name);
                    var ranked = images.Where(image => !posters.Contains(image.FullName) && !image.Name.Contains("插图", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(Path.GetFileNameWithoutExtension(image.Name), @"^\d+\s*[（(]\d+[）)]$"))
                        .Select(image => (Image: image, Score: AnimeLibraryService.PosterMatchScore(Path.GetFileNameWithoutExtension(image.Name), stem)))
                        .Where(item => item.Score < 10).OrderBy(item => item.Score).ThenBy(item => item.Image.Name, new EpisodeNameComparer()).ToArray();
                    var poster = ranked.FirstOrDefault().Image;
                    if (poster is null && videos.Length == 1)
                    {
                        poster = images.FirstOrDefault(image => !posters.Contains(image.FullName) && new[] { "poster", "cover", "folder", "海报", "封面" }.Contains(Path.GetFileNameWithoutExtension(image.Name), StringComparer.OrdinalIgnoreCase));
                        if (poster is null && images.Length == 1 && !posters.Contains(images[0].FullName) && AnimeLibraryService.GetIllustrationGroup(images[0].Name) is null && !images[0].Name.Contains("插图", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(Path.GetFileNameWithoutExtension(images[0].Name), @"^\d+\s*[（(]\d+[）)]$")) poster = images[0];
                    }
                    if (poster is null || !posters.Add(poster.FullName)) continue;
                    if (kind == AnimeOptimizationKind.Posters) Add(poster.FullName, Path.Combine(folder.FullName, stem + poster.Extension));
                }
                if (kind != AnimeOptimizationKind.Illustrations) return;
                var indexes = new Dictionary<string, int>();
                var usedIndexes = new Dictionary<string, HashSet<int>>();
                foreach (var existing in images)
                {
                    var canonical = Regex.Match(Path.GetFileNameWithoutExtension(existing.Name), @"^(\d{2,})插图(?:_.+)? \(([1-9]\d*)\)$");
                    if (!canonical.Success) continue;
                    var key = int.Parse(canonical.Groups[1].Value).ToString();
                    if (!usedIndexes.TryGetValue(key, out var used)) usedIndexes[key] = used = [];
                    used.Add(int.Parse(canonical.Groups[2].Value));
                }
                foreach (var image in images.Where(image => !posters.Contains(image.FullName)))
                {
                    var number = AnimeLibraryService.GetIllustrationEpisodeNumber(image.Name);
                    var matches = videos.Where(video => AnimeLibraryService.GetEpisodeNumber(video.Name) == number && number is not null).ToArray();
                    if (matches.Length != 1)
                    {
                        matches = videos.Where(video => AnimeLibraryService.PosterMatchScore(Path.GetFileNameWithoutExtension(image.Name), Path.GetFileNameWithoutExtension(video.Name)) < 2).ToArray();
                        if (matches.Length == 1) number = AnimeLibraryService.GetEpisodeNumber(matches[0].Name) ?? (Array.IndexOf(videos, matches[0]) + 1).ToString();
                    }
                    if (matches.Length == 0 && videos.Length == 1) { matches = videos; number = AnimeLibraryService.GetEpisodeNumber(videos[0].Name) ?? "1"; }
                    if (matches.Length != 1 || number is null)
                    { Add(image.FullName, image.FullName, "无法确定对应集数"); continue; }
                    if (Regex.IsMatch(Path.GetFileNameWithoutExtension(image.Name), @"^\d{2,}插图(?:_.+)? \([1-9]\d*\)$")) continue;
                    if (!usedIndexes.TryGetValue(number, out var used)) usedIndexes[number] = used = [];
                    indexes.TryGetValue(number, out var index);
                    do { index++; } while (used.Contains(index));
                    used.Add(index); indexes[number] = index;
                    Add(image.FullName, Path.Combine(folder.FullName, $"{int.Parse(number):D2}插图 ({index}){image.Extension}"));
                }
            }
            Visit(new DirectoryInfo(scope), 0, includeScope);
            return operations;
        }, token);
}
