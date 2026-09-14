// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.ResourceManager;

namespace Files.App.Services.ResourceManager;

public interface IResourceOperationsService
{
    Task<List<ResourceFileOperation>> PreviewRenameVideosAsync(string root, ResourceSettings? settings = null, CancellationToken ct = default);
    Task<List<ResourceFileOperation>> PreviewClassifySubtitlesAsync(string root, ResourceSettings settings, CancellationToken ct = default);
    List<ResourceFileOperation> ApplyConflictStrategy(List<ResourceFileOperation> ops, string strategy);
    Task<List<ResourceFileOperation>> ExecuteOperationsAsync(string root, List<ResourceFileOperation> ops, CancellationToken ct = default);
    Task<List<ResourceOperationBatch>> GetOperationHistoryAsync(string root, CancellationToken ct = default);
    Task<List<ResourceFileOperation>> UndoLastOperationAsync(string root, CancellationToken ct = default);
}
