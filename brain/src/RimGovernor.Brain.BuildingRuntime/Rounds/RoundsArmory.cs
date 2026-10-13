namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsArmoryResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

public sealed partial class RoundsArmoryPlanner
{
    private readonly Rounder _reviewer;
    internal Rounder Reviewer => _reviewer;
    private readonly IRoundsGearSource _native;

    /// <summary>Places the crafting spot; null when the native source cannot build.</summary>
    private readonly RoundsBuildingPlanner? _spot;

    private readonly object _mu = new();
    private bool _spotNeeded;

    public RoundsArmoryPlanner(Rounder reviewer, IRoundsGearSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsArmoryPlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
        _spot = NewCraftingSpotPlanner(reviewer, native);
    }

    internal async Task<RoundsArmoryResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsArmoryResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown)
        {
            throw new ControlException("step: !state.ObservationKnown");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsArmoryResult { Verdict = BuildingReasonNoReview };
        }
        var (_, workable) = p.Journal.Workable(review, PolicyApi.MaintainEquipment, call);
        if (!workable || !SpotWanted())
        {
            return new RoundsArmoryResult { Verdict = BuildingReasonNoDeficit };
        }
        return await PlaceCraftingSpotAsync(arbiter, call, epoch).ConfigureAwait(false);
    }
}

public sealed partial class Rounder
{
    /// <summary>Whether the crafting spot stands with a usable bench elsewhere, so the spot is owed its retirement.</summary>
    internal async Task<Fact<bool>> CraftingSpotRetireOwedAsync(GenerationSnapshot snapshot, ColonyProjection facts, CancellationToken ct)
    {
        if (!facts.Facts.CurrentConstruction.TryGetValue(out var census))
        {
            return Unknown<bool>();
        }
        var standing = census.Buildings.Any(b => b.Building?.Definition == PolicyApi.CraftingSpotDefinition);
        if (!standing)
        {
            return Known(false);
        }
        if (Native is not IArtBenchSource source)
        {
            return Unknown<bool>();
        }
        var reads = (await source.ReadGearBenchesAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false)).Reads;
        foreach (var read in reads)
        {
            read.Bench.Usable.TryGetValue(out var usable);
            if (usable && read.Bench.Def != PolicyApi.CraftingSpotDefinition && WeaponBenchHosted([read.Bench]))
            {
                return Known(true);
            }
        }
        return Known(false);
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static RoundsBuildingPlanner? NewCraftingSpotPlanner(Rounder reviewer, IRoundsGearSource native)
    {
        if (native is not IRoundsBuildingSource building)
        {
            return null;
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = building, Concern = PolicyApi.MaintainEquipment, Definition = CraftingSpotDefinition, Environment = PlacementEnvironment.Anywhere, CraftingSpot = true };
    }
}
