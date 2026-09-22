// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using Files.App.Data.Models.ResourceManager;

namespace Files.App.Services.ResourceManager;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ResourceOperationBatch>))]
[JsonSerializable(typeof(ResourceWorkspaceState))]
internal sealed partial class ResourceManagerJsonSerializerContext : JsonSerializerContext
{
}
