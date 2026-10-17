namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The creepjoiner work a census owes against the stored inspection record (#3264): weapon drops, inspections and
    /// isolation moves while the colonist census is complete, and disarm orders for arrested creepjoiners. Owed is
    /// unknown while the census or a creepjoiner's facts cannot be read and nothing is known owed.
    /// </summary>
    internal static CreepJoinerWork CreepJoinerWorkOf(ColonyProjection facts, BridgeRoundsFrame frame, DefinitionCatalog? catalog, CreepJoinerRecord record)
    {
        var owed = Unknown<bool>();
        var work = new CreepJoinerWork { Record = record };
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
        if (facts.Facts.Prisoners.TryGetValue(out var prisoners))
        {
            DisarmWork disarm;
            try
            {
                disarm = (catalog ?? throw new ControlException("creepJoiner review: no definition catalog")).CreepJoinerDisarm().Orders(prisoners);
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
        if (frame.Pawns is null || !frame.Emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete
            || frame.Pawns.Pawns.Count != frame.Emergency.Facts.Colonists.Count)
        {
            return null;
        }
        return frame.Pawns.Pawns.Select(CreepJoinerHandOf).ToList();
    }

    /// <summary>Go bridge.CreepJoinerHand: lifts a combat-detail pawn row into the weapon drop's facts (the downside check's facts, whether the colonist can take an order, its weapon, surgery operations and bills, allowed area and hunger).</summary>
    internal static CreepJoinerHand CreepJoinerHandOf(PbObservations.PawnState row)
    {
        var hand = new CreepJoinerHand
        {
            Pawn = new PawnId(row.Pawn.Id ?? ""),
            Facts = BridgeApi.CreepJoinerPawn(row),
            Available = Unknown<bool>(),
            Weapon = Unknown<string>(),
            Operations = Unknown<EquatableArray<SurgeryOperation>>(),
            QueuedSurgeries = Unknown<int>(),
            Area = Unknown<string>(),
            Hungry = Unknown<bool>(),
        };
        if (row.Health is { } health && !Boundary.IssueField(row.Issues, "health"))
        {
            // The weapon drop and inspections read recipe names only, no part items.
            hand = hand with
            {
                Operations = BridgeApi.SurgeryFacts(health, null).Operations,
                QueuedSurgeries = BridgeApi.QueuedSurgeries(health),
                QueuedRecipes = BridgeApi.QueuedSurgeryRecipes(health) is { } recipes ? EquatableArray.From(recipes) : null,
            };
        }
        // An unset allowed area is an unrestricted pawn (native AllowedArea).
        if (row.Settings is { } settings && !Boundary.IssueField(settings.Issues, "allowed_area_id"))
        {
            hand = hand with { Area = Known((settings.AllowedAreaId ?? "")) };
        }
        if (row.Needs is { HungerCategory: not null } needs && !Boundary.IssueField(needs.Issues, "hunger_category"))
        {
            hand = hand with { Hungry = Known(needs.HungerCategory.GetValueOrDefault() != RimGovernor.Contracts.Wire.Defs.HungerCategory.Fed) };
        }
        hand = hand with { Available = Known(!row.Dead && !row.Downed && !row.Drafted && row.MentalState is null) };
        if (row.Equipment is { Armed: not null } equipment && !Boundary.IssueField(equipment.Issues, "armed"))
        {
            if (!equipment.Armed.GetValueOrDefault())
            {
                hand = hand with { Weapon = Known("") };
            }
            else if (equipment.PrimaryId is not null)
            {
                hand = hand with { Weapon = Known((equipment.PrimaryId ?? "")) };
            }
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
        var (need, owed) = IsolationRoomNeed(facts);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var (ground, gk) = ColonyGround(facts);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!owed || !pk || !gk || !ck || !census!.Colony)
        {
            return false;
        }
        return PolicyApi.IsolationRoomStanding(plan!, plan!.GroundWithRock(ground, NaturalRock(facts)), census.Buildings, need, FurnitureDefinitions(facts));
    }
}
