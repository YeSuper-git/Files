// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Items;

namespace Files.App.Data.Items.ResourceManager;

public sealed partial class ResourceVideoFileListedItem : ListedItem
{
    public bool IsAnime { get; set; }
    public Files.App.Data.Models.ResourceManager.ResourceVideoDetails? AnimeDetails { get; set; }
}
