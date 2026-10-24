using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

partial class RoundsGearPlanner
{
    internal static GearPlanningRequest GearRequest(ColonyProjection projection, GearObservation census, IReadOnlyList<GearBench> benches) => new()
    {
        Observation = census,
        Benches = EquatableArray.From(benches),
        Items = projection.Facts.Items,
        Crafts = projection.Facts.Crafts,
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

    /// <summary>Abstains while the rounds census is unread.</summary>
    internal static Declared DeclareOrdersInner(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        projection.Facts.Gear.TryGetValue(out var census)
            ? GearOrdersRules.DeclareGearOrders(GearRequest(projection, census, benches))
            : WorkRules.Abstaining(Cause.UnreadGearRecover);
}

public sealed partial class RoundsArmoryPlanner
{
    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        (await DeclareOrdersInnerAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(MilitaryFamily.Equipment.FilingKey.Value);

    internal async Task<Declared> DeclareOrdersInnerAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
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
        try
        {
            (fighters, hunters) = await WeaponDemandAsync(snapshot, ids, projection.Identity.Tick.Value, projection.Facts.MapBounds, benches, projection.Facts.Crafts, tier, ct).ConfigureAwait(false);
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

        return GearOrdersRules.DeclareArmoryOrders(RoundsGearPlanner.GearRequest(projection, census, benches), tier, MergeAmounts(fighters, hunters), shells);
    }
}
