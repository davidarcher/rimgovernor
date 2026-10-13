namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    internal static GearPlanningRequest GearRequest(ColonyProjection projection, IReadOnlyList<GearBench> benches) => new()
    {
        Observation = projection.Facts.Gear,
        Benches = Known(benches),
        StuffCategories = projection.Facts.Items.StuffCategories,
    };

    /// <summary>A failed read abstains from declaring, but a cancelled one is the caller's cancellation and propagates.</summary>
    internal static Declared AbstainOnRead(CancellationToken ct, Cause fact, Exception? cause = null)
    {
        ct.ThrowIfCancellationRequested();
        return PolicyFunctions.Abstaining(fact);
    }
}

public sealed partial class RoundsGearPlanner
{
    public Declared DeclareOrders(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches) =>
        DeclareOrdersInner(ct, snapshot, projection, benches).For(ConcernId.MaintainEquipment);

    internal Declared DeclareOrdersInner(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches) =>
        PolicyFunctions.DeclareGearOrders(GearRequest(projection, benches));
}

public sealed partial class RoundsArmoryPlanner
{
    public Declared DeclareOrders(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches) =>
        DeclareOrdersInner(ct, snapshot, projection, benches).For(ConcernId.MaintainEquipment);

    internal Declared DeclareOrdersInner(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches)
    {
        SetSpotNeeded(false);
        var tier = PolicyFunctions.AssessArmory(projection.Facts.RaidPoints, projection.Facts.Research).Tier;
        var known = projection.Facts.Gear.TryGetValue(out var census);
        if (Native is not IRoundsEquipSource)
        {
            return PolicyFunctions.Abstaining(Cause.UnreadNativeRead);
        }

        if (!known)
        {
            return PolicyFunctions.Abstaining(Cause.UnreadGearDemand);
        }

        var ids = census!.Pawns.Select(p => p.Pawn.Value).ToList();
        IReadOnlyList<Amount> fighters;
        IReadOnlyList<Amount> hunters;
        int unarmed;
        try
        {
            (fighters, hunters, unarmed) = WeaponDemand(ct, snapshot, ids, projection.Identity.Tick.Value, benches, tier);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return AbstainOnRead(ct, Cause.UnreadWeapons, e);
        }

        IReadOnlyList<ShellTarget> shells;
        try
        {
            shells = ShellTargets(ct, Native, Reviewer.Player.Journal, snapshot, projection);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return AbstainOnRead(ct, Cause.UnreadShells, e);
        }

        SetSpotNeeded((unarmed > 0 && !WeaponBenchHosted(benches)) || PositiveFact(projection.Facts.CraftingSpotRetireOwed));
        return PolicyFunctions.DeclareArmoryOrders(GearRequest(projection, benches), tier, MergeAmounts(fighters, hunters), shells);
    }

    internal void SetSpotNeeded(bool v)
    {
        lock (Mu)
        {
            SpotNeeded = v;
        }
    }

    internal bool SpotWanted()
    {
        lock (Mu)
        {
            return SpotNeeded;
        }
    }
}
