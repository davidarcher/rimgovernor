using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsCreepJoiner
{
    /// <summary>
    /// The creepjoiner work a census owes against the stored inspection record (#3264): weapon drops, inspections and
    /// isolation moves while the colonist census is complete, and disarm orders for arrested creepjoiners. Owed is
    /// unknown while the census or a creepjoiner's facts cannot be read and nothing is known owed.
    /// </summary>
    internal static CreepJoinerWork CreepJoinerWorkOf(ColonyProjection facts, BridgeRoundsFrame frame, DefinitionCatalog? catalog, CreepJoinerRecord record)
    {
        var owed = Unknown<bool>();
        var due = false;
        var work = new CreepJoinerWork();
        if (CreepJoinerHands(frame) is { } hands)
        {
            var defs = catalog ?? throw new ControlException("creepJoiner review: no definition catalog");
            var downsides = defs.CreepJoinerDownsides();
            var (drops, dropsOwed) = downsides.WeaponDrops(hands);
            owed = dropsOwed;
            var inspections = downsides.Inspections(hands, defs.SurgicalInspectionRecipes(), record);
            if (inspections.Owed(record))
            {
                due = true;
            }
            facts.Facts.IsolationArea.TryGetValue(out var area);
            var projection = facts with { Isolation = CreepJoinerIsolation(facts, frame, catalog, record) };
            var isolation = downsides.IsolationMoves(hands, inspections.Record, area ?? "", IsolationReady(projection));
            if (isolation.Count > 0)
            {
                due = true;
            }
            work = work with { Drops = drops, Inspections = inspections, Isolation = isolation };
        }
        if (facts.Facts.Population.TryGetValue(out var census))
        {
            DisarmWork disarm;
            try
            {
                disarm = (catalog ?? throw new ControlException("creepJoiner review: no definition catalog")).CreepJoinerDisarm().Orders(census.Prisoners);
            }
            catch (DomainException)
            {
                // The disarm recipe is not in the catalog: nothing to order.
                disarm = new DisarmWork();
            }
            work = work with { Disarm = disarm };
            if (disarm.Orders.Count > 0)
            {
                due = true;
            }
        }
        return work with { Owed = due ? Known(true) : owed };
    }

    /// <summary>The hands of the colonists when the frame's pawn census is complete; null otherwise.</summary>
    internal static List<CreepJoinerHand>? CreepJoinerHands(BridgeRoundsFrame frame)
    {
        if (frame.Pawns is null || !frame.Emergency.Facts.AllSeen
            || frame.Pawns.Pawns.Count != frame.Emergency.Facts.Colonists.Count)
        {
            return null;
        }
        return frame.Pawns.Pawns.Select(CreepJoinerHandOf).ToList();
    }

    /// <summary>Lifts a combat-detail pawn row into the weapon drop's facts (the downside check's facts, whether the colonist can take an order, its weapon, surgery operations and bills, allowed area and hunger).</summary>
    internal static CreepJoinerHand CreepJoinerHandOf(PbObservations.PawnState row)
    {
        // A dead colonist has no surgery facts and no operations; a living census colonist always has them.
        var surgery = row.Health.Surgery
            ?? (row.Dead
                ? new PbObservations.PawnSurgery { SurgeryBills = [], MissingParts = [], Operations = [] }
                : throw BridgeContractException.Contract($"living colonist {row.Pawn.Id} without surgery facts"));
        var hand = new CreepJoinerHand
        {
            Pawn = new PawnId(row.Pawn.Id ?? ""),
            Facts = CreepJoinerFacts.CreepJoinerPawn(row),
            Available = Unknown<bool>(),
            Weapon = Unknown<string>(),
            Operations = surgery.Operations,
            QueuedSurgeries = surgery.SurgeryBills.Count,
            QueuedRecipes = EquatableArray.From(SurgeryFactsRules.QueuedSurgeryRecipes(surgery)),
            // An unset allowed area is an unrestricted pawn (native AllowedArea).
            Area = Known(row.Settings.AllowedAreaId ?? ""),
            Hungry = Unknown<bool>(),
        };
        if (row.Needs.Food is RimGovernor.Contracts.Wire.Observations.FoodNeed.Has food)
        {
            hand = hand with { Hungry = Known(food.Hunger != RimGovernor.Contracts.Wire.Defs.HungerCategory.Fed) };
        }
        hand = hand with { Available = Known(!row.Dead && !row.Downed && !row.Drafted && row.MentalState is null) };
        if (!row.Equipment.Armed)
        {
            hand = hand with { Weapon = Known("") };
        }
        else if (row.Equipment.PrimaryId is { } primary)
        {
            hand = hand with { Weapon = Known(primary) };
        }
        return hand;
    }

    internal static IsolationPlanning CreepJoinerIsolation(ColonyProjection facts, BridgeRoundsFrame frame, DefinitionCatalog? catalog, CreepJoinerRecord record)
    {
        var output = new IsolationPlanning { Pawns = Unknown<EquatableArray<PawnId>>(), Beds = facts.Shapes.Furniture.SleepingBeds() };
        if (CreepJoinerHands(frame) is { } hands)
        {
            var defs = catalog ?? throw new ControlException("creepJoinerIsolation: no definition catalog");
            output = output with { Pawns = defs.CreepJoinerDownsides().Isolated(hands, record) };
        }
        return output;
    }

    /// <summary>
    /// The isolation credit's creepjoiners (#3844): each one the projection holds apart (<see cref="ColonyProjection.Isolation"/>)
    /// with the days since its tracker's joinedTick, and the catalog's downside table. Unknown while the held-apart census or
    /// the pawn rows are unread.
    /// </summary>
    internal static CreepJoinerForecast CreepJoinerForecastOf(ColonyProjection facts, BridgeRoundsFrame frame, DefinitionCatalog? catalog)
    {
        if (!facts.Isolation.Pawns.TryGetValue(out var pawns) || frame.Pawns is null)
        {
            return new CreepJoinerForecast { Unread = "the held-apart creepjoiner census is unread" };
        }
        if (pawns.Count == 0)
        {
            return new CreepJoinerForecast { Unread = "", Held = EquatableArray.From(Array.Empty<HeldCreepJoiner>()) };
        }
        var defs = catalog ?? throw new ControlException("creepJoinerForecast: no definition catalog");
        var joined = new Dictionary<PawnId, int>();
        foreach (var row in frame.Pawns.Pawns)
        {
            if (row.Anomaly is PbObservations.PawnAnomalyState.Active { Facts.Creepjoiner: { } tracker })
            {
                joined[new PawnId(row.Pawn.Id ?? "")] = tracker.JoinedTick;
            }
        }
        var now = facts.Identity.Tick.Value;
        var held = pawns.Select(p => new HeldCreepJoiner(p.Value, Math.Max(0, now - (joined.TryGetValue(p, out var tick)
            ? tick : throw new ControlException($"creepJoinerForecast: held pawn {p.Value} has no creepjoiner tracker"))) / (double)GameTime.TicksPerDay));
        return new CreepJoinerForecast { Unread = "", Held = EquatableArray.From(held), Table = defs.CreepJoinerDownsideTable() };
    }

    internal static bool IsolationReady(ColonyProjection facts)
    {
        var (need, owed) = RoundsSleepingUpkeepPlanner.IsolationRoomNeed(facts);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var (ground, gk) = RoundsBuildingPlanner.ColonyGround(facts);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!owed || !pk || !gk || !ck || !census!.Colony)
        {
            return false;
        }
        return IsolationRoomRules.IsolationRoomStanding(plan!, plan!.GroundWithRock(ground, RoundsBuildingPlanner.NaturalRock(facts)), census.Buildings, need, RoundsSleepingUpkeepPlanner.FurnitureDefinitions(facts));
    }
}
