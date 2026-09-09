// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.AvManager;

namespace Files.App.Services.AvManager;

public interface IAvCodeParser
{
    AvCodeInfo? ParseCode(string name);
    bool HasChineseSubtitle(string name);
    bool HasChineseSubtitle(string name, IReadOnlyList<string> keywords);
}
