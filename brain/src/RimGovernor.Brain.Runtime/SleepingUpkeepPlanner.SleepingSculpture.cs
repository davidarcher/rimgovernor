using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

partial class SleepingUpkeepPlanner
{
    /// <summary>The rooms/art Need's room input: a bedroom below target, weakest in beauty, with a free cell.</summary>
    internal static bool SculptureRoomsOwedOf(RimGovernor.Brain.GameAccess.ColonyReading facts)
    {
        var rooms = facts.Facts.Upkeep.Rooms;
        var census = facts.Facts.CurrentConstruction;
        var obs = facts.Facts.Sleeping;
        var traits = SleepingTraits(facts);
        if (census is null || traits is null)
        {
            throw new RequiredFactException(census is null ? "construction census" : "colonist profiles");
        }
        var tier = TechTierRules.SelectTechTier(facts.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray(), facts.Snapshot.PlayerTechLevel);
        return ArtRules.SculptureRoomsOwed(obs!, UpgradeTargetsOf(facts, RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Facts.Impressiveness)), FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.PlanningWindow.Cells));
    }

    /// <summary>The policy view of packed sculpture stock.</summary>
    internal static List<Pol.PackedSculpture> PackedSculptures(IEnumerable<RimGovernor.Brain.GameFacts.Observations.StockItem> items, Pol.ItemFacts facts)
    {
        var output = new List<Pol.PackedSculpture>();
        foreach (var item in items)
        {
            var packed = item.Packed ?? throw new ObservationContractException("packed stock lacks its building");
            if (!facts.SculptureSize(packed.DefName, out _))
            {
                continue;
            }
            output.Add(new Pol.PackedSculpture { Observed = item });
        }
        return output;
    }

    /// <summary>SaleSculptures over a routine projection and the packed items read; null when a room input is unknown.</summary>
    internal static async Task<HashSet<string>?> SaleSculpturesOfAsync(GameClient native, RimGovernor.Brain.GameFacts.Common.Identity identity, RimGovernor.Brain.GameAccess.ColonyReading facts, CancellationToken ct)
    {
        var obs = facts.Facts.Sleeping;
        var rooms = facts.Facts.Upkeep.Rooms;
        var census = facts.Facts.CurrentConstruction;
        var traits = SleepingTraits(facts);
        if (census is null || traits is null)
        {
            return null;
        }
        var items = await native.ReadPackedItemsAsync(identity, ct).ConfigureAwait(false);
        var tier = TechTierRules.SelectTechTier(facts.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray(), facts.Snapshot.PlayerTechLevel);
        return RoomQualityRules.SaleSculptures(obs!, RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Facts.Impressiveness), FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.PlanningWindow.Cells), PackedSculptures(items, facts.Facts.Items), facts.Facts.Items);
    }

    /// <summary>
    /// The review's shed_art input: the unreserved packed art count, read only while the wealth headroom is
    /// known and negative (unknown otherwise, so the need adds nothing).
    /// </summary>
    internal static async Task<long> ReviewSaleArtAsync(object source, RimGovernor.Brain.GameFacts.Common.Identity identity, RimGovernor.Brain.GameAccess.ColonyReading facts, CancellationToken ct)
    {
        var native = source as GameClient;
        var h = facts.Facts.WealthBudget();
        if (h >= 0)
        {
            return 0;
        }
        var sale = await SaleSculpturesOfAsync(native ?? throw new RequiredFactException("native art stock source"), identity, facts, ct).ConfigureAwait(false);
        if (sale is null)
        {
            throw new RequiredFactException("sculpture sale facts");
        }
        return sale.Count;
    }
}

public sealed partial class SleepingUpkeepPlanner
{
    /// <summary>
    /// The beauty lever after pots and floors: a finished packed sculpture (the rooms/art Need's pinned bill)
    /// installed (a RelocateIntent on the packed item's inner building) on free floor in the room, once per
    /// Episode. due is false when the lever has nothing to do.
    /// </summary>
    internal async Task<(BuildingResult Result, bool Due)> SculptBedroomAsync(PackedStock stock, ControlState state, Sto.NeedOwner goal, ReviewReading reading, CancellationToken call, CancellationToken epoch)
    {
        var none = (new BuildingResult(), false);
        var facts = reading.Reading;
        var obs = facts.Facts.Sleeping;
        var rooms = facts.Facts.Upkeep.Rooms;
        var census = facts.Facts.CurrentConstruction;
        var traits = SleepingTraits(facts);
        if (census is null || traits is null)
        {
            return none;
        }
        var tier = TechTierRules.SelectTechTier(facts.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray(), facts.Snapshot.PlayerTechLevel);
        if (await stock.ItemsAsync(call).ConfigureAwait(false) is not { } items)
        {
            return none;
        }
        var packed = PackedSculptures(items, facts.Facts.Items);
        var inner = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.StockItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            inner[item.Id] = item;
        }
        var (step, due) = RoomQualityRules.NextSculpture(obs!, UpgradeTargetsOf(facts, RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Facts.Impressiveness)), FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.PlanningWindow.Cells), packed, facts.Facts.Items);
        if (!due)
        {
            return none;
        }
        var p = Reviewer.Player;
        var method = new MethodId("bedroom-sculpture-install-" + BuildingPlanner.DigestHex(step.Packed, 8));
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return (new BuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "sculpture_install") }, true);
        }
        var item2 = inner.GetValueOrDefault(step.Packed)
            ?? throw new ObservationContractException("selected sculpture missing from packed stock");
        var building = item2.Packed ?? throw new ObservationContractException("packed stock lacks its building");
        var id = PlanIds.MintPlanId();
        var move = new MoveBuilding(building.Id, building.DefName, step.Anchor, step.Rot);
        var action = DomainAction.NewMoveBuildingAction(new ActionId($"{id}-0"), move);
        return CommitSleepingPlan(state, goal, method, id, [action], call, epoch);
    }
}

public sealed partial class TradePlanner
{
    /// <summary>
    /// The packed art the trade selector may sell: every packed sculpture but those the install lever would
    /// put in owed rooms. The trade's colony read carries no rooms, construction census or work pawns, so the
    /// room inputs come from the routine read the install lever uses. Null (no art sale) when the source
    /// cannot read packed items or a room input is unknown.
    /// </summary>
    internal async Task<HashSet<string>?> SaleArtAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, GenerationSnapshot snapshot, CancellationToken call)
    {
        if (Native is not GameClient native)
        {
            return null;
        }
        var expected = await StepScope.StepScopeAsync(Reviewer.Native, call).ConfigureAwait(false);
        var claims = Reviewer.Player.Journal.ConstructionClaims(snapshot, expected.Tick, call);
        var read = await Reviewer.ObserveOwnedAsync(Reviewer.Native, expected, claims, call).ConfigureAwait(false);
        return await SleepingUpkeepPlanner.SaleSculpturesOfAsync(native, identity, read.Reading, call).ConfigureAwait(false);
    }
}
