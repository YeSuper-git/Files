// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Items;
using Files.App.Data.Models.ResourceManager;

namespace Files.App.Data.Items.ResourceManager;

/// <summary>
/// A native details-pane item that carries resource-library actor metadata.
/// </summary>
public sealed partial class ResourceActorListedItem : ListedItem
{
    public ResourceActorDetails ActorDetails { get; set; } = new();
    public Func<Task>? EditActorDetailsAsync { get; set; }
    public Func<Task<int>>? CountActorVideosAsync { get; init; }
}
