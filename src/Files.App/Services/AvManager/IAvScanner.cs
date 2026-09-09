// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.AvManager;

namespace Files.App.Services.AvManager;

public interface IAvScanner
{
    Task<AvScanResult> AnalyzeLibraryAsync(string root, AvSettings settings, CancellationToken cancellationToken = default);
}
