// Copyright (c) Files Community. Licensed under the MIT License.
namespace Files.App.Data.Models.ResourceManager;

public sealed class AnimeCategoryDisplaySettings
{
    public bool ShowEpisodePosters { get; set; } = true;
    public bool ShowEpisodeCount { get; set; } = true;
    public AnimeCategoryDisplaySettings Clone() => new() { ShowEpisodePosters = ShowEpisodePosters, ShowEpisodeCount = ShowEpisodeCount };
}
