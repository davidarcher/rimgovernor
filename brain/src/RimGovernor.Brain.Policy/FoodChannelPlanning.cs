using O = RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;

/// <summary>
/// Native source potential, read whole every colony read. It does not select work.
/// </summary>
public sealed record FoodChannelPlanning
{
    /// <summary>Null without Odyssey.</summary>
    public FishingWaterReview? FishableWater { get; init; }

    /// <summary>The producing milk and egg yields; an inactive or stalled comp contributes nothing and has no row. Eggs need no handler.</summary>
    public IReadOnlyList<AnimalProduct> Products { get; init; } = [];

    public IReadOnlyList<O.PasteDispenser> PasteDispenser { get; init; } = [];

    public EquatableArray<PenGrazing> Grazing { get; init; }

    public PastureTable Pasture { get; init; } = new(new Dictionary<string, double>());

    public IReadOnlyList<SlaughterFoodAnimal> Slaughter { get; init; } = [];
}

public sealed record FishingWaterReview
{
    public IReadOnlyList<FishingRegionReview> Regions { get; init; } = [];

    public FishingResearch Research { get; init; } = new FishingResearch.NoProgress();
}

/// <summary>A water body whose fish the colony eats, with the policy verdicts: reachability, delivery, footprint and crew.</summary>
public sealed record FishingRegionReview
{
    public required O.FishableRegion Observed { get; init; }

    public bool Reachable { get; init; }

    public bool Delivering { get; init; }

    public IReadOnlyList<Cell> ProposedCells { get; init; } = [];

    public FishingCrew Crew { get; init; } = new FishingCrew.NoFisher();

}

