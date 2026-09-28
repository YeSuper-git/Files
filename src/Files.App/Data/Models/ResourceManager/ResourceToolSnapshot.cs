// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

/// <summary>
/// Persistent pre-change state for a Resource Manager bulk operation.
/// </summary>
public sealed class ResourceToolSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string LibraryPath { get; set; } = string.Empty;
    public string ScopePath { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<ResourceFileOperation> Operations { get; set; } = [];
    public List<ResourceToolTagAssignmentSnapshot> TagAssignments { get; set; } = [];
    public string? CreatedTagUid { get; set; }
    public bool IsRestored { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
    public string? RestoreSummary { get; set; }

    public ResourceToolSnapshot Clone() => new()
    {
        Id = Id,
        CreatedAt = CreatedAt,
        LibraryPath = LibraryPath,
        ScopePath = ScopePath,
        Action = Action,
        Summary = Summary,
        Operations = Operations.Select(operation => new ResourceFileOperation
        {
            Operation = operation.Operation,
            Source = operation.Source,
            Target = operation.Target,
            Code = operation.Code,
            Status = operation.Status,
            Reason = operation.Reason,
            Backup = operation.Backup,
        }).ToList(),
        TagAssignments = TagAssignments.Select(assignment => assignment.Clone()).ToList(),
        CreatedTagUid = CreatedTagUid,
        IsRestored = IsRestored,
        RestoredAt = RestoredAt,
        RestoreSummary = RestoreSummary,
    };
}

public sealed class ResourceToolTagAssignmentSnapshot
{
    public string ItemPath { get; set; } = string.Empty;
    public List<string> TagIds { get; set; } = [];

    public ResourceToolTagAssignmentSnapshot Clone() => new()
    {
        ItemPath = ItemPath,
        TagIds = TagIds.ToList(),
    };
}
