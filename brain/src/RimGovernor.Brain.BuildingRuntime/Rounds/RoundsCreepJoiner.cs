using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsCreepJoiner
{
    /// <summary>
    /// The creepjoiner work a census owes against the stored inspection record (#3264): weapon drops, inspections and
    /// isolation moves while the colonist census is complete, and disarm orders for arrested creepjoiners. Owed is
    /// unknown while the colonist census is incomplete and nothing is known owed.
    /// </summary>
    internal static CreepJoinerWork CreepJoinerWorkOf(ColonyProjection facts, BridgeRoundsFrame frame, DefinitionCatalog? catalog, CreepJoinerRecord record)
    {
        var hands = CreepJoinerHands(frame);
        var due = false;
        var work = new CreepJoinerWork();
        if (hands is not null)
        {
            var defs = catalog ?? throw new ControlException("creepJoiner review: no definition catalog");
            var downsides = defs.CreepJoinerDownsides();
            var drops = downsides.WeaponDrops(hands);
            due = drops.Count > 0;
            var inspections = downsides.Inspections(hands, defs.SurgicalInspectionRecipes(), record);
            if (inspections.Owed(record))
            {
                due = true;
            }
            var projection = facts with { Isolation = CreepJoinerIsolation(facts, frame, catalog, record) };
            var isolation = downsides.IsolationMoves(hands, inspections.Record, facts.Facts.IsolationArea, IsolationReady(projection));
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
        return work with { Owed = due || hands is not null ? Known(due) : Unknown<bool>() };
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
            Available = !row.Dead && !row.Downed && !row.Drafted && row.MentalState is null,
            // An armed pawn's primary is always read (PawnEquipment.PrimaryId).
            Weapon = row.Equipment.Armed
                ? row.Equipment.PrimaryId ?? throw BridgeContractException.Contract($"armed colonist {row.Pawn.Id} without a primary id")
                : null,
            Operations = surgery.Operations,
            QueuedSurgeries = surgery.SurgeryBills.Count,
            QueuedRecipes = EquatableArray.From(SurgeryFactsRules.QueuedSurgeryRecipes(surgery)),
            // An unset allowed area is an unrestricted pawn (native AllowedArea).
            Area = row.Settings.AllowedAreaId ?? "",
            // A pawn with no food need is never hungry.
            Hungry = row.Needs.Food is RimGovernor.Contracts.Wire.Observations.FoodNeed.Has food && food.Hunger != RimGovernor.Contracts.Wire.Defs.HungerCategory.Fed,
        };
        return hand;
    }

    internal static IsolationPlanning CreepJoinerIsolation(ColonyProjection facts, BridgeRoundsFrame frame, DefinitionCatalog? catalog, CreepJoinerRecord record)
    {
        var output = new IsolationPlanning { Pawns = Unknown<EquatableArray<PawnId>>(), Beds = facts.Shapes.Furniture.SleepingBeds() };
        if (CreepJoinerHands(frame) is { } hands)
        {
            var defs = catalog ?? throw new ControlException("creepJoinerIsolation: no definition catalog");
            output = output with { Pawns = Known(defs.CreepJoinerDownsides().Isolated(hands, record)) };
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

    /// <summary>
    /// The harm term's creepjoiners (#3844 D12): each colonist creepjoiner, split by whether its downside has shown
    /// (<see cref="CreepJoinerDownsides.Revealed"/>), an unshown one with the days since its tracker's joinedTick and its kindDef.combatPower,
    /// and the catalog's downside table. Unread while the colonist census is incomplete.
    /// </summary>
    internal static CreepJoinerHarmInputs CreepJoinerHarmOf(ColonyProjection facts, BridgeRoundsFrame frame, DefinitionCatalog? catalog)
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
            if (row.Anomaly is not PbObservations.PawnAnomalyState.Active { Facts.Creepjoiner: { } tracker })
            {
                continue;
            }
            // The row's facts are all read, so whether the downside has shown is known (CreepJoinerDownsides.Revealed).
            var pawn = row.Pawn.Id ?? "";
            if (downsides.Unrevealed(CreepJoinerFacts.CreepJoinerPawn(row)))
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

    internal static bool IsolationReady(ColonyProjection facts)
    {
        var (need, owed) = RoundsSleepingUpkeepPlanner.IsolationRoomNeed(facts);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var (ground, gk) = RoundsBuildingPlanner.ColonyGround(facts);
        var census = facts.Facts.CurrentConstruction;
        if (!owed || !pk || !gk || census is null)
        {
            return false;
        }
        return IsolationRoomRules.IsolationRoomStanding(plan!, plan!.GroundWithRock(ground, RoundsBuildingPlanner.NaturalRock(facts)), census.Buildings, need, RoundsSleepingUpkeepPlanner.FurnitureDefinitions(facts));
    }
}
