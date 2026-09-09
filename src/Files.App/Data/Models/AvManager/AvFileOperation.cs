// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.AvManager;

public sealed class AvFileOperation
{
    public string Operation { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string? Code { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Reason { get; init; }
}
