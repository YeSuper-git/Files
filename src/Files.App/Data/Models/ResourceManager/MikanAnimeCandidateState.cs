// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

public enum MikanAnimeCandidateDisposition
{
    Undecided,
    WantToFollow,
    NotInterested,
}

public sealed class MikanAnimeCandidateState
{
    public int MikanBangumiId { get; set; }
    public string Title { get; set; } = string.Empty;
    public MikanAnimeCandidateDisposition Disposition { get; set; }
}
