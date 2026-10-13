namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
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
        return PolicyFunctions.Abstaining(fact);
    }
}

public sealed partial class RoundsGearPlanner
{
    public static Declared DeclareOrders(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        DeclareOrdersInner(snapshot, projection, benches, ct).For(PolicyFunctions.MaintainEquipment);

    internal static Declared DeclareOrdersInner(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        PolicyFunctions.DeclareGearOrders(GearRequest(projection, benches));
}

public sealed partial class RoundsArmoryPlanner
{
    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        (await DeclareOrdersInnerAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(PolicyFunctions.MaintainEquipment);

    internal async Task<Declared> DeclareOrdersInnerAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        SetSpotNeeded(false);
        var tier = PolicyFunctions.AssessArmory(projection.Facts.RaidPoints, projection.Facts.Research).Tier;
        var known = projection.Facts.Gear.TryGetValue(out var census);
        if (_native is not IRoundsEquipSource)
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
            (fighters, hunters, unarmed) = await WeaponDemandAsync(snapshot, ids, projection.Identity.Tick.Value, benches, tier, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return AbstainOnRead(Cause.UnreadWeapons, e, ct);
        }

        IReadOnlyList<Amount> shells;
        try
        {
            shells = await ShellTargetsAsync(_native, _reviewer.Player.Journal, snapshot, projection, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return AbstainOnRead(Cause.UnreadShells, e, ct);
        }

        SetSpotNeeded((unarmed > 0 && !WeaponBenchHosted(benches)) || PositiveFact(projection.Facts.CraftingSpotRetireOwed));
        return PolicyFunctions.DeclareArmoryOrders(GearRequest(projection, benches), tier, MergeAmounts(fighters, hunters), shells);
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
