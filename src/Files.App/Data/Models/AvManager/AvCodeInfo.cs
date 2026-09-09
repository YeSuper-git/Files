// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.AvManager;

public sealed class AvCodeInfo
{
    public string Normalized { get; init; } = string.Empty;
    public string NoZero { get; init; } = string.Empty;
    public string Raw { get; init; } = string.Empty;
    public override string ToString() => Normalized;
}
