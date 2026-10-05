// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Enums;

namespace Files.App.Data.Contracts;

/// <summary>Provides the default settings destination for a feature page.</summary>
public interface IPageSettingsContext
{
    SettingsPageKind SettingsPage { get; }
}
