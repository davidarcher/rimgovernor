using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

internal static class CreepJoiner
{
    /// <summary>
    /// The creepjoiner work a census owes against the stored inspection record (#3264): weapon drops, inspections and
    /// isolation moves while the colonist census is complete, and disarm orders for arrested creepjoiners. The complete
    /// colonist census is required before deriving any work.
    /// </summary>
    internal static CreepJoinerWork CreepJoinerWorkOf(ColonyReading facts, BridgeReviewFrame frame, DefinitionCatalog? catalog, CreepJoinerRecord record)
    {
        var hands = CreepJoinerHands(frame) ?? throw new RequiredFactException("creepjoiner colonist census");
        var due = false;
        var work = new CreepJoinerWork();
        var defs = catalog ?? throw new ControlException("creepJoiner review: no definition catalog");
        var downsides = defs.CreepJoinerDownsides();
        var drops = downsides.WeaponDrops(hands);
        due = drops.Count > 0;
        var inspections = downsides.Inspections(hands, defs.SurgicalInspectionRecipes(), record);
        if (inspections.Owed(record))
        {
            due = true;
        }
        var projection = facts with { Facts = facts.Facts with { Isolation = CreepJoinerIsolation(facts, frame, catalog, record) } };
        var isolation = downsides.IsolationMoves(hands, inspections.Record, facts.Facts.IsolationArea, IsolationReady(projection));
        if (isolation.Count > 0)
        {
            due = true;
        }
        work = work with { Drops = drops, Inspections = inspections, Isolation = isolation };
        DisarmWork disarm;
        try
        {
            disarm = (catalog ?? throw new ControlException("creepJoiner review: no definition catalog")).CreepJoinerDisarm().Orders(facts.Facts.Population.Prisoners);
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
        return work with { Owed = due };
    }

    /// <summary>The hands of the colonists when the frame's pawn census is complete; null otherwise.</summary>
    internal static List<CreepJoinerHand>? CreepJoinerHands(BridgeReviewFrame frame)
    {
        if (frame.Pawns is null || !frame.Emergency.Facts.AllSeen
            || frame.Pawns.Pawns.Count != frame.Emergency.Facts.Colonists.Count)
        {
            return null;
        }
        return frame.Pawns.Pawns.Select(CreepJoinerHandOf).ToList();
    }

    /// <summary>Retains the canonical colonist row and requires its weapon and living surgery reads.</summary>
    internal static CreepJoinerHand CreepJoinerHandOf(RimGovernor.Brain.GameFacts.Observations.PawnState row)
    {
        var hand = new CreepJoinerHand { Observed = row };
        // Validate required native reads before admitting this census into the review.
        _ = hand.Surgery;
        _ = hand.Weapon;
        return hand;
    }

    internal static IsolationPlanning CreepJoinerIsolation(ColonyReading facts, BridgeReviewFrame frame, DefinitionCatalog? catalog, CreepJoinerRecord record)
    {
        var output = new IsolationPlanning { Beds = facts.Facts.Shapes.Furniture.SleepingBeds() };
        if (CreepJoinerHands(frame) is { } hands)
        {
            var defs = catalog ?? throw new ControlException("creepJoinerIsolation: no definition catalog");
            output = output with { Pawns = new IsolationCensus.Counted(defs.CreepJoinerDownsides().Isolated(hands, record)) };
        }
        return output;
    }

    /// <summary>
    /// The isolation credit's creepjoiners (#3844): each one the projection holds apart (<see cref="ColonyReading.Isolation"/>)
    /// with the days since its tracker's joinedTick, and the catalog's downside table. Unknown while the held-apart census or
    /// the pawn rows are unread.
    /// </summary>
    internal static CreepJoinerForecast CreepJoinerForecastOf(ColonyReading facts, BridgeReviewFrame frame, DefinitionCatalog? catalog)
    {
        if (facts.Facts.Isolation.Pawns is not IsolationCensus.Counted { Held: var pawns } || frame.Pawns is null)
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
            if (row.Anomaly is RimGovernor.Brain.GameFacts.Observations.PawnAnomalyState.Active { Facts.Creepjoiner: { } tracker })
            {
                joined[new PawnId(row.Pawn.Id ?? "")] = tracker.JoinedTick;
            }
        }
        var now = facts.Identity.Tick.Value;
        var held = pawns.Select(p => new HeldCreepJoiner(p.Value, Math.Max(0, now - (joined.TryGetValue(p, out var tick)
            ? tick : throw new ControlException($"creepJoinerForecast: held pawn {p.Value} has no creepjoiner tracker"))) / (double)GameTime.TicksPerDay));
        return new CreepJoinerForecast { Unread = "", Held = EquatableArray.From(held), Table = defs.CreepJoinerDownsideTable() };
    }

    /// <summary>
    /// The harm term's creepjoiners (#3844 D12): each colonist creepjoiner, split by whether its downside has shown
    /// (<see cref="CreepJoinerDownsides.Revealed"/>), an unshown one with the days since its tracker's joinedTick and its kindDef.combatPower,
    /// and the catalog's downside table. Unread while the colonist census is incomplete.
    /// </summary>
    internal static CreepJoinerHarmInputs CreepJoinerHarmOf(ColonyReading facts, BridgeReviewFrame frame, DefinitionCatalog? catalog)
    {
        if (frame.Pawns is null || CreepJoinerHands(frame) is null)
        {
            return new CreepJoinerHarmInputs { Unread = "the colonist census is incomplete" };
        }
        var defs = catalog ?? throw new ControlException("creepJoinerHarm: no definition catalog");
        var downsides = defs.CreepJoinerDownsides();
        var now = facts.Identity.Tick.Value;
        var unshown = new List<UnshownCreepJoiner>();
        var shown = new List<string>();
        foreach (var row in frame.Pawns.Pawns.OrderBy(r => r.Pawn.Id ?? "", StringComparer.Ordinal))
        {
            if (row.Anomaly is not RimGovernor.Brain.GameFacts.Observations.PawnAnomalyState.Active { Facts.Creepjoiner: { } tracker })
            {
                continue;
            }
            // The row's facts are all read, so whether the downside has shown is known (CreepJoinerDownsides.Revealed).
            var pawn = row.Pawn.Id ?? "";
            if (downsides.Unrevealed(row))
            {
                unshown.Add(new UnshownCreepJoiner(pawn, Math.Max(0, now - tracker.JoinedTick) / (double)GameTime.TicksPerDay, tracker.CombatPower));
            }
            else
            {
                shown.Add(pawn);
            }
        }
        return new CreepJoinerHarmInputs { Unshown = EquatableArray.From(unshown), Shown = EquatableArray.From(shown), Table = unshown.Count > 0 ? defs.CreepJoinerDownsideTable() : new() };
    }

    internal static bool IsolationReady(ColonyReading facts)
    {
        var (need, owed) = SleepingUpkeepPlanner.IsolationRoomNeed(facts);
        var plan = facts.Facts.LayoutPlan;
        var pk = plan is not null;
        var ground = BuildingPlanner.ColonyGround(facts);
        var census = facts.Facts.CurrentConstruction;
        if (!owed || !pk)
        {
            return false;
        }
        return IsolationRoomRules.IsolationRoomStanding(plan!, plan!.GroundWithRock(ground, BuildingPlanner.NaturalRock(facts)), census.Buildings, need, SleepingUpkeepPlanner.FurnitureDefinitions(facts));
    }
}
