// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.ResourceManager;

public interface IResourceTitleTranslationService
{
    Task<string> TranslateJapaneseTitleAsync(
        string title,
        string accessKeyId,
        string accessKeySecret,
        CancellationToken cancellationToken = default);
}
