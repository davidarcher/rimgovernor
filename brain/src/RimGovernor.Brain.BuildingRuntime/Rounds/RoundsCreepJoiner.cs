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
                owed = Known(true);
            }
            facts.Facts.IsolationArea.TryGetValue(out var area);
            var projection = facts with { Isolation = CreepJoinerIsolation(facts, frame, catalog, record) };
            var isolation = downsides.IsolationMoves(hands, inspections.Record, area ?? "", IsolationReady(projection));
            if (isolation.Count > 0)
            {
                owed = Known(true);
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
                owed = Known(true);
            }
        }
        return work with { Owed = owed };
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
        var hand = new CreepJoinerHand
        {
            Pawn = new PawnId(row.Pawn.Id ?? ""),
            Facts = CreepJoinerFacts.CreepJoinerPawn(row),
            Available = Unknown<bool>(),
            Weapon = Unknown<string>(),
            // The weapon drop and inspections read recipe names only, no part items.
            Operations = SurgeryFactsRules.SurgeryFacts(row.Health.Surgery, null).Operations,
            QueuedSurgeries = SurgeryFactsRules.QueuedSurgeries(row.Health.Surgery),
            QueuedRecipes = SurgeryFactsRules.QueuedSurgeryRecipes(row.Health.Surgery) is { } recipes ? EquatableArray.From(recipes) : null,
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
