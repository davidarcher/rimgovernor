using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>The rooms/art Need's room input: a bedroom below target, weakest in beauty, with a free cell.</summary>
    internal static Fact<bool> SculptureRoomsOwedOf(Observation.ColonyProjection facts)
    {
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var census = facts.Facts.CurrentConstruction;
        var obs = facts.Facts.Sleeping;
        var traits = SleepingTraits(facts);
        if (!rk || census is null || traits is null)
        {
            return Unknown<bool>();
        }
        facts.TechTier.TryGetValue(out var tier);
        return Known(ArtRules.SculptureRoomsOwed(obs!, UpgradeTargetsOf(facts, RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness)), FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.Cells)));
    }

    /// <summary>The policy view of packed sculpture stock.</summary>
    internal static List<Pol.PackedSculpture> PackedSculptures(IEnumerable<PackedItem> items, Pol.ItemFacts facts)
    {
        var output = new List<Pol.PackedSculpture>();
        foreach (var item in items)
        {
            if (!facts.SculptureSize(item.InnerDef, out _))
            {
                continue;
            }
            var quality = item.QualityKnown ? (int)item.Quality : -1;
            output.Add(new Pol.PackedSculpture { Id = item.Id, Def = item.InnerDef, Quality = quality, MarketValue = item.MarketValue });
        }
        return output;
    }

    /// <summary>SaleSculptures over a routine projection and the packed items read; null when a room input is unknown.</summary>
    internal static async Task<HashSet<string>?> SaleSculpturesOfAsync(FacadeNativeClient native, C.Identity identity, Observation.ColonyProjection facts, CancellationToken ct)
    {
        var obs = facts.Facts.Sleeping;
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var census = facts.Facts.CurrentConstruction;
        var traits = SleepingTraits(facts);
        if (!rk || census is null || traits is null)
        {
            return null;
        }
        var items = await native.ReadPackedItemsAsync(identity, RoomQualityRules.PackedSculptureDefinition, ct).ConfigureAwait(false);
        facts.TechTier.TryGetValue(out var tier);
        return RoomQualityRules.SaleSculptures(obs!, RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness), FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.Cells), PackedSculptures(items, facts.Facts.Items), facts.Facts.Items);
    }

    /// <summary>
    /// The review's shed_art input: the unreserved packed art count, read only while the wealth headroom is
    /// known and negative (unknown otherwise, so the need adds nothing).
    /// </summary>
    internal static async Task<Fact<long>> ReviewSaleArtAsync(object source, C.Identity identity, Observation.ColonyProjection facts, CancellationToken ct)
    {
        var native = source as FacadeNativeClient;
        var hk = facts.Facts.WealthBudget().TryGetValue(out var h);
        if (native is null || !hk || h >= 0)
        {
            return Unknown<long>();
        }
        var sale = await SaleSculpturesOfAsync(native, identity, facts, ct).ConfigureAwait(false);
        if (sale is null)
        {
            return Unknown<long>();
        }
        return Known<long>(sale.Count);
    }
}

public sealed partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>
    /// The beauty lever after pots and floors: a finished packed sculpture (the rooms/art Need's pinned bill)
    /// installed (a RelocateIntent on the packed item's inner building) on free floor in the room, once per
    /// Episode. due is false when the lever has nothing to do.
    /// </summary>
    internal async Task<(RoundsBuildingResult Result, bool Due)> SculptBedroomAsync(PackedStock stock, ControlState state, Sto.NeedOwner goal, Observation.RoundsReading reading, CancellationToken call, CancellationToken epoch)
    {
        var none = (new RoundsBuildingResult(), false);
        var facts = reading.Projection;
        var obs = facts.Facts.Sleeping;
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var census = facts.Facts.CurrentConstruction;
        var traits = SleepingTraits(facts);
        if (!rk || census is null || traits is null)
        {
            return none;
        }
        facts.TechTier.TryGetValue(out var tier);
        if (await stock.ItemsAsync(RoomQualityRules.PackedSculptureDefinition, call).ConfigureAwait(false) is not { } items)
        {
            return none;
        }
        var packed = PackedSculptures(items, facts.Facts.Items);
        var inner = new Dictionary<string, PackedItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            inner[item.Id] = item;
        }
        var (step, due) = RoomQualityRules.NextSculpture(obs!, UpgradeTargetsOf(facts, RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness)), FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.Cells), packed, facts.Facts.Items);
        if (!due)
        {
            return none;
        }
        var p = Reviewer.Player;
        var method = new MethodId("bedroom-sculpture-install-" + RoundsBuildingPlanner.DigestHex(step.Packed, 8));
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return (new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "sculpture_install") }, true);
        }
        var item2 = inner.GetValueOrDefault(step.Packed);
        var id = PlanIds.MintPlanId();
        var move = new MoveBuilding(item2!.Inner, item2.InnerDef, step.Anchor, step.Rot);
        var action = DomainAction.NewMoveBuildingAction(new ActionId($"{id}-0"), move);
        return CommitSleepingPlan(state, goal, method, id, [action], call, epoch);
    }
}

public sealed partial class RoundsTradePlanner
{
    /// <summary>
    /// The packed art the trade selector may sell: every packed sculpture but those the install lever would
    /// put in owed rooms. The trade's colony read carries no rooms, construction census or work pawns, so the
    /// room inputs come from the routine read the install lever uses. Null (no art sale) when the source
    /// cannot read packed items or a room input is unknown.
    /// </summary>
    internal async Task<HashSet<string>?> SaleArtAsync(C.Identity identity, GenerationSnapshot snapshot, CancellationToken call)
    {
        if (Native is not FacadeNativeClient native)
        {
            return null;
        }
        var expected = await StepScope.StepScopeAsync(Reviewer.Native, call).ConfigureAwait(false);
        var claims = Reviewer.Player.Journal.ConstructionClaims(snapshot, expected.Tick, call);
        var read = await Reviewer.ObserveRoomsAsync(Reviewer.Native, expected, claims, call).ConfigureAwait(false);
        return await RoundsSleepingUpkeepPlanner.SaleSculpturesOfAsync(native, identity, read.Projection, call).ConfigureAwait(false);
    }
}
