// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.ResourceManager;

public interface IBailianQwenMtTitleTranslationService
{
    Task<string> TranslateJapaneseTitleAsync(
        string title,
        string apiKey,
        CancellationToken cancellationToken = default);
}
