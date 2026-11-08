// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

public sealed class MikanAnimeFollow
{
    public int MikanBangumiId { get; set; }
    public int? BangumiSubjectId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public int UnseenReleaseCount { get; set; }
    public int? PreferredGroupId { get; set; }
    public Dictionary<int, string> PreferredVariants { get; set; } = [];
    public List<string> SeenEpisodeIds { get; set; } = [];
}
