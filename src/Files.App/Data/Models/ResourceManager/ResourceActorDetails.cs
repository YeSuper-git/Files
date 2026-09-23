// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models.ResourceManager;

/// <summary>
/// User-maintained details for an actor folder. These details live in the app's
/// local workspace state and never modify the folder or its files.
/// </summary>
public sealed class ResourceActorDetails
{
    public string Name { get; set; } = string.Empty;
    public string Aliases { get; set; } = string.Empty;
    public string HeightCm { get; set; } = string.Empty;
    public string WeightKg { get; set; } = string.Empty;
    public string Bust { get; set; } = string.Empty;
    public string Waist { get; set; } = string.Empty;
    public string Hip { get; set; } = string.Empty;
    public string CupSize { get; set; } = string.Empty;
    public DateTime? CareerRetirementDate { get; set; }

    public ResourceActorDetails Clone() => new()
    {
        Name = Name,
        Aliases = Aliases,
        HeightCm = HeightCm,
        WeightKg = WeightKg,
        Bust = Bust,
        Waist = Waist,
        Hip = Hip,
        CupSize = CupSize,
        CareerRetirementDate = CareerRetirementDate,
    };
}
