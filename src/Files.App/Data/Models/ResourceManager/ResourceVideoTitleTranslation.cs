// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

public sealed class ResourceVideoTitleTranslation
{
    public string SourceTitle { get; set; } = string.Empty;
    public string TranslatedTitle { get; set; } = string.Empty;
    public DateTimeOffset TranslatedAt { get; set; }

    public ResourceVideoTitleTranslation Clone() => new()
    {
        SourceTitle = SourceTitle,
        TranslatedTitle = TranslatedTitle,
        TranslatedAt = TranslatedAt,
    };
}
