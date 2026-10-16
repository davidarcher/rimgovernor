namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The armory's work-ledger declarer and the crafting spot it asks for: the declaration prices the weapon and shell
/// demand and notes whether the spot is wanted (an unarmed fighter with no weapon bench, or a spot owed down); the
/// military/equipment Need's work step places or retires it (#3259).
/// </summary>
public sealed partial class RoundsArmoryPlanner
{
    private readonly Rounder _reviewer;
    internal Rounder Reviewer => _reviewer;
    private readonly IRoundsGearSource _native;

    /// <summary>Places the crafting spot under the equipment Need; null when the native source cannot build.</summary>
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
}

public sealed partial class Rounder
{
    private readonly object _equipmentMu = new();
    private (GenerationSnapshot World, bool Owed) _equipment;

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

    /// <summary>
    /// Notes whether the review in <paramref name="world"/> found the composed military/equipment Need unmet: the
    /// workshop ladder's research rung for equipment is owed only then. Unknown is not owed.
    /// </summary>
    internal void NoteEquipment(GenerationSnapshot world, RoundsFacts facts)
    {
        var owed = Needs.Contains(MilitaryFamily.Equipment)
            && MilitaryFamily.EquipmentMet(new MilitaryFacts { Gear = facts.Gear, CraftingSpotRetireOwed = facts.CraftingSpotRetireOwed, ShellsShort = facts.ShellsShort }).TryGetValue(out var met)
            && !met;
        lock (_equipmentMu)
        {
            _equipment = (world, owed);
        }
    }

    /// <summary>Whether the last review in <paramref name="world"/> found the equipment Need unmet.</summary>
    internal bool EquipmentOwed(GenerationSnapshot world)
    {
        lock (_equipmentMu)
        {
            return _equipment.World == world && _equipment.Owed;
        }
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
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = building, Concern = MilitaryFamily.Equipment.FilingKey, Need = MilitaryFamily.Equipment, Definition = CraftingSpotDefinition, Environment = PlacementEnvironment.Anywhere, CraftingSpot = true };
    }
}
