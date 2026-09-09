// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models.AvManager;

namespace Files.App.Services.AvManager;

public interface IAvOperationsService
{
    Task<List<AvFileOperation>> PreviewRenameVideosAsync(string root, CancellationToken ct = default);
    Task<List<AvFileOperation>> PreviewClassifySubtitlesAsync(string root, AvSettings settings, CancellationToken ct = default);
    List<AvFileOperation> ApplyConflictStrategy(List<AvFileOperation> ops, string strategy);
    Task<List<AvFileOperation>> ExecuteOperationsAsync(string root, List<AvFileOperation> ops, CancellationToken ct = default);
    Task<List<AvOperationBatch>> GetOperationHistoryAsync(string root, CancellationToken ct = default);
    Task<List<AvFileOperation>> UndoLastOperationAsync(string root, CancellationToken ct = default);
}
