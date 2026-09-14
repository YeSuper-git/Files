// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;

namespace Files.App.Services.ResourceManager;

public interface IResourceCodeParser
{
    ResourceCodeInfo? ParseCode(string name);
    bool HasChineseSubtitle(string name);
    bool HasChineseSubtitle(string name, IReadOnlyList<string> keywords);
}
