using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

partial class RoundsGearPlanner
{
    internal static GearPlanningRequest GearRequest(ColonyReading projection, GearObservation census, IReadOnlyList<GearBench> benches) => new()
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
    public static Declared DeclareOrders(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        DeclareOrdersInner(snapshot, projection, benches, ct).For(MilitaryFamily.Equipment.FilingKey.Value);

    /// <summary>Declares gear work from the complete colony census.</summary>
    internal static Declared DeclareOrdersInner(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        GearOrdersRules.DeclareGearOrders(GearRequest(projection, projection.Facts.Gear, benches));
}

public sealed partial class RoundsArmoryPlanner
{
    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        (await DeclareOrdersInnerAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(MilitaryFamily.Equipment.FilingKey.Value);

    internal async Task<Declared> DeclareOrdersInnerAsync(GenerationSnapshot snapshot, ColonyReading projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var tier = ArmoryRules.AssessArmory(projection.Facts).Tier;
        var census = projection.Facts.Gear;
        if (_native is not FacadeNativeClient)
        {
            return WorkRules.Abstaining(Cause.UnreadNativeRead);
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
        var shells = projection.Facts.ShellTargets;

        return GearOrdersRules.DeclareArmoryOrders(RoundsGearPlanner.GearRequest(projection, census, benches), tier, MergeAmounts(fighters, hunters), shells);
    }
}
