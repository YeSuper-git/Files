// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

/// <summary>
/// A tag owned by Resource Manager. Resource tags intentionally have their own
/// namespace and are not stored in Files' native file-tag stream.
/// </summary>
public sealed class ResourceTagDefinition
{
    public string Uid { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#0072BD";

    public ResourceTagDefinition Clone() => new()
    {
        Uid = Uid,
        Name = Name,
        Color = Color,
    };
}
