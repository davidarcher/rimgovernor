using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

partial class RoundsGearPlanner
{
    internal static GearPlanningRequest GearRequest(ColonyProjection projection, IReadOnlyList<GearBench> benches) => new()
    {
        Observation = projection.Facts.Gear,
        Benches = Known(EquatableArray.From(benches)),
        StuffCategories = projection.Facts.Items.StuffCategories,
    };

    /// <summary>A failed read abstains from declaring, but a cancelled one is the caller's cancellation and propagates.</summary>
    internal static Declared AbstainOnRead(Cause fact, Exception? cause = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return WorkRules.Abstaining(fact);
    }
}

/// <summary>
/// The gear batches' work-ledger declarer (#2597), filed under the military/equipment Need. The apparel policies and
/// outfit prune its planner once committed are that Need's proposals (#3259).
/// </summary>
public static partial class RoundsGearPlanner
{
    public static Declared DeclareOrders(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        DeclareOrdersInner(snapshot, projection, benches, ct).For(MilitaryFamily.Equipment.FilingKey.Value);

    internal static Declared DeclareOrdersInner(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        GearOrdersRules.DeclareGearOrders(GearRequest(projection, benches));
}

public sealed partial class RoundsArmoryPlanner
{
    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        (await DeclareOrdersInnerAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(MilitaryFamily.Equipment.FilingKey.Value);

    internal async Task<Declared> DeclareOrdersInnerAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        SetSpotNeeded(false);
        var tier = ArmoryRules.AssessArmory(projection.Facts.RaidPoints, projection.Facts.DefenseCapacity, projection.Facts.Research).Tier;
        var known = projection.Facts.Gear.TryGetValue(out var census);
        if (_native is not FacadeNativeClient)
        {
            return WorkRules.Abstaining(Cause.UnreadNativeRead);
        }

        if (!known)
        {
            return WorkRules.Abstaining(Cause.UnreadGearDemand);
        }

        var ids = census!.Pawns.Select(p => p.Pawn.Value).ToList();
        IReadOnlyList<Amount> fighters;
        IReadOnlyList<Amount> hunters;
        int unarmed;
        try
        {
            (fighters, hunters, unarmed) = await WeaponDemandAsync(snapshot, ids, projection.Identity.Tick.Value, benches, tier, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return RoundsGearPlanner.AbstainOnRead(Cause.UnreadWeapons, e, ct);
        }

        // The review's d_reads lap read the shell targets (Facts.ShellTargets); the declarer does not read them again (#3566).
        if (!projection.Facts.ShellTargets.TryGetValue(out var shells))
        {
            return WorkRules.Abstaining(Cause.UnreadShells);
        }

        SetSpotNeeded((unarmed > 0 && !WeaponBenchHosted(benches)) || RoundsBuildingPlanner.PositiveFact(projection.Facts.CraftingSpotRetireOwed));
        return GearOrdersRules.DeclareArmoryOrders(RoundsGearPlanner.GearRequest(projection, benches), tier, MergeAmounts(fighters, hunters), shells);
    }

    internal void SetSpotNeeded(bool v)
    {
        lock (_mu)
        {
            _spotNeeded = v;
        }
    }

    internal bool SpotWanted()
    {
        lock (_mu)
        {
            return _spotNeeded;
        }
    }
}
