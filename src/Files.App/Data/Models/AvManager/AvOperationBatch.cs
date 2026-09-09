// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.AvManager;

public sealed class AvOperationBatch
{
    public string Id { get; init; } = string.Empty;
    public ulong CreatedAt { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<AvFileOperation> Operations { get; init; } = [];
    public DateTime CreatedDateTime => DateTime.UnixEpoch.AddSeconds(CreatedAt).ToLocalTime();
}
