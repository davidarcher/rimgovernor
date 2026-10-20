using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

partial class RoundsSleepingUpkeepPlanner
{
    private static bool DefinitionBuildable(Observation.ColonyProjection facts, string def) =>
        facts.DefinitionAvailable(def).TryGetValue(out var v) && v;

    /// <summary>The next room quality upgrade: one template piece for an owned bedroom below its target.</summary>
    internal static (Pol.RoomUpgrade Upgrade, bool Due) RoomUpgradeOf(Observation.ColonyProjection facts)
    {
        var sk = facts.Facts.Sleeping.TryGetValue(out var obs);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var traits = SleepingTraits(facts);
        if (!sk || !rk || !ck || !census!.Colony || traits is null)
        {
            return (new Pol.RoomUpgrade(), false);
        }
        facts.TechTier.TryGetValue(out var tier);
        var targets = RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness);
        foreach (var (id, t) in RoomQualityRules.CommonRoomTargets(obs!, tier, facts.Impressiveness))
        {
            targets.TryAdd(id, t);
        }
        targets = WithThroneTargets(facts, targets);
        return RoomQualityRules.NextRoomUpgrade(obs!, UpgradeTargetsOf(facts, targets), FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.Cells), def => DefinitionBuildable(facts, def));
    }

    /// <summary>The next bed replacement step, read from the same census as RoomUpgradeOf.</summary>
    internal static (Pol.BedReplacement Replacement, bool Due) BedReplacementOf(Observation.ColonyProjection facts)
    {
        var sk = facts.Facts.Sleeping.TryGetValue(out var obs);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var traits = SleepingTraits(facts);
        if (!sk || !rk || !ck || !census!.Colony || traits is null)
        {
            return (new Pol.BedReplacement(), false);
        }
        facts.TechTier.TryGetValue(out var tier);
        var cost = new Dictionary<Pol.Resource, long>();
        var stock = facts.Resources.TryGetValue(out var known) ? known : new Dictionary<Pol.Resource, long>();
        var materials = new Pol.BedMaterials { Cost = cost, Items = facts.Facts.Items, Stock = stock };
        foreach (var d in facts.Definitions)
        {
            // A bed's stuff is the allowed stocked one with the best rest effectiveness; a def with no rest effectiveness is no bed.
            Observation.StuffPrice price;
            try
            {
                price = d.StuffChoice(Observation.StuffCriterion.MaxRestEffectiveness, stock);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }
            if (price.Stuff == "")
            {
                continue;
            }
            foreach (var c in price.Costs)
            {
                if (c.Resource == new Pol.Resource(price.Stuff))
                {
                    cost[new Pol.Resource(d.Name)] = c.Count;
                }
            }
        }
        return RoomQualityRules.NextBedReplacement(obs!, UpgradeTargetsOf(facts, RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness)), FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.Cells), def => DefinitionBuildable(facts, def), materials, facts.Facts.Preferences);
    }

    /// <summary>The next unmet royal bedroom thing.</summary>
    internal static (Pol.RoomUpgrade Upgrade, bool Due) TitleFurniture(Observation.ColonyProjection facts)
    {
        var sk = facts.Facts.Sleeping.TryGetValue(out var obs);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!sk || !rk || !ck || !census!.Colony)
        {
            return (new Pol.RoomUpgrade(), false);
        }
        return RoomQualityRules.NextTitleFurniture(obs!, FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.Cells), def => DefinitionBuildable(facts, def));
    }

    /// <summary>The next animal sleeping spot for a master's solo bedroom: none while the definition is unavailable or unsized.</summary>
    internal static (Pol.RoomUpgrade Upgrade, bool Due) CompanionBed(Observation.ColonyProjection facts)
    {
        var sk = facts.Facts.Sleeping.TryGetValue(out var obs);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var ak = facts.Facts.AnimalUpkeep.Animals.TryGetValue(out var animals);
        if (!sk || !rk || !ck || !ak || !census!.Colony)
        {
            return (new Pol.RoomUpgrade(), false);
        }
        if (RoundsAnimalContainment.AnimalContainmentDefinition(facts.Definitions, facts.Shapes.Furniture.AnimalSpot) is not { } d)
        {
            return (new Pol.RoomUpgrade(), false);
        }
        var sizeKnown = d.Size.TryGetValue(out var size);
        var availableKnown = d.Available.TryGetValue(out var available);
        if (!sizeKnown || !availableKnown || size.Width < 1 || size.Height < 1)
        {
            return (new Pol.RoomUpgrade(), false);
        }
        var spot = new Pol.InteriorPieceDef { Def = d.Name, Size = new Cell(size.Width, size.Height) };
        return HerdCompanionRules.NextCompanionBed(obs!, animals!, FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.Cells), facts.Shapes.Furniture, spot, available);
    }

    /// <summary>The next beauty lever: a plant pot or a prettier floor for a bedroom whose weakest stat is beauty.</summary>
    internal static (Pol.RoomUpgrade Upgrade, bool Due) BeautyUpgrade(Observation.ColonyProjection facts)
    {
        var sk = facts.Facts.Sleeping.TryGetValue(out var obs);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var traits = SleepingTraits(facts);
        if (!sk || !rk || !ck || !census!.Colony || traits is null)
        {
            return (new Pol.RoomUpgrade(), false);
        }
        facts.TechTier.TryGetValue(out var tier);
        var definitions = new Dictionary<string, Pol.FloorDefinition>(StringComparer.Ordinal);
        var floors = new Pol.FlooringFacts { Definitions = definitions, Stock = facts.Resources };
        foreach (var d in facts.Definitions)
        {
            definitions[d.Name] = new Pol.FloorDefinition { Available = d.Available, Terrain = d.Terrain, Cleanliness = d.Cleanliness, Beauty = d.Beauty, Flammability = d.Flammability, PathCost = d.PathCost, Costs = d.Costs, WorkToBuild = d.WorkToBuild };
        }
        return RoomQualityRules.NextBeautyUpgrade(obs!, UpgradeTargetsOf(facts, WithThroneTargets(facts, RoomQualityRules.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness))), FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.Cells), def => DefinitionBuildable(facts, def), facts.Facts.Upkeep.Flooring, floors);
    }
}

public sealed partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>Deconstructs a replaced bed, once per bed per Episode.</summary>
    internal async Task<RoundsBuildingResult> RemoveOldBedAsync(ControlState state, Sto.Rounds review, Sto.NeedOwner goal, Observation.RoundsReading reading, Pol.BedReplacement rep, CancellationToken call, CancellationToken epoch)
    {
        if (reading.Projection.Facts.Preferences.Protects(rep.Bed))
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "protected_thing") };
        }
        var p = Reviewer.Player;
        var method = new MethodId("bedroom-replace-remove-" + RoundsBuildingPlanner.DigestHex(rep.Bed, 8));
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "old_bed_removal") };
        }
        // A real bed is packed, not deconstructed: the stored bed furnishes the next bedroom or bed spot (ReinstallStoredBed).
        if (rep.Def == "Bed" || rep.Def == SleepingRules.SleepingCoupleBedDefinition)
        {
            var value = new MoveBuilding(rep.Bed, rep.Def, rep.Cell, Rotation.South);
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewUninstallBuildingAction(new ActionId($"{id}-0"), value);
            var (result, _) = CommitSleepingPlan(state, goal, method, id, [action], call, epoch);
            return result;
        }
        return await Building.RetireBuildingAsync(state, review, goal, reading.Reading, rep.Bed, rep.Def, rep.Cell, "bedroom-replace-remove", "old_bed_removal", call, epoch).ConfigureAwait(false);
    }

    /// <summary>The colony's standing buildings, as the room furnishing counts them; none while the census is unknown.</summary>
    internal static IReadOnlyList<Pol.CurrentBuilding> StandingBuildings(Observation.ColonyProjection facts) =>
        facts.Facts.CurrentConstruction.TryGetValue(out var census) ? census.Buildings : [];

    /// <summary>
    /// Places one lever piece (title furniture, companion bed, room upgrade, plant pot) through the room's one
    /// reconcile: the room's furnishing lists every lever piece standing there and the asked one, and the shared
    /// build side installs or builds what is missing. A census room that stands in no planned room has nowhere to reconcile.
    /// </summary>
    internal async Task<RoundsBuildingResult> FurnishBedroomAsync(PackedStock stock, ControlState state, Sto.Rounds review, Sto.NeedOwner goal, Observation.RoundsReading reading, Pol.RoomUpgrade u, CancellationToken call, CancellationToken epoch)
    {
        var facts = reading.Projection;
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!rk || !ck || !facts.LayoutPlan.TryGetValue(out var plan))
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("room_census") };
        }
        var furniture = FurnitureRoomsRules.FurnitureRooms(rooms!, census!, facts.Cells).FirstOrDefault(r => r.Id == u.Room);
        var planned = furniture is null ? null : plan.Rooms.FirstOrDefault(r => r.Region.Bounds() == furniture.Room.Interior);
        if (planned is null)
        {
            return new RoundsBuildingResult { Verdict = NoSpace("bedroom_upgrade_site") };
        }
        var furnishing = SleepingRules.BedroomFurnishing(planned, rooms!.Shapes, "", census!.Buildings, u);
        if (!furnishing.Absent)
        {
            return new RoundsBuildingResult { Verdict = NoSpace("bedroom_upgrade_site") };
        }
        var name = $"bedroom-upgrade-{u.Slot}-{RoundsBuildingPlanner.DigestHex(u.Room, 8)}";
        return await Building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile { Room = planned, Template = furnishing.Pieces, Name = name, Reason = "room upgrade " + u.Slot }, call, epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// Lays the beauty floor over a bedroom's cells, once per room per Episode. A floor is cells, not a listed
    /// piece, and it is laid over the floor already there rather than reconciled (a reconcile tears the old
    /// floor out first and keeps any floor that meets the room's tier), so it stays a special-case op.
    /// </summary>
    internal async Task<RoundsBuildingResult> LayBeautyFloorAsync(ControlState state, Sto.Rounds review, Sto.NeedOwner goal, Observation.RoundsReading reading, Pol.RoomUpgrade u, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var facts = reading.Projection;
        var method = new MethodId($"bedroom-upgrade-{u.Slot}-{RoundsBuildingPlanner.DigestHex(u.Room, 8)}");
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "bedroom_upgrade") };
        }
        var stuff = facts.BuildStuff(u.Def);
        var snapshot = state.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("layBeautyFloor: p.session.State() != state");
            }
        }
        Check();
        var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var selected = new List<Pol.Preview>();
        for (var i = 0; i < u.Cells.Count; i++)
        {
            var building = new Domain.Building(u.Def, u.Cells[i], u.Rot, stuff);
            var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-{i}")), building, ConstructionTier.Sustain);
            var preview = await Native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
            var v = preview.Preview;
            var ck = v.CanPlace.TryGetValue(out var can);
            var sk = v.SafeToPlace.TryGetValue(out var safe);
            if (!ck || !can || !sk || !safe)
            {
                continue;
            }
            stock = RoundsBuildingPlanner.MergeRoundsStock(stock, preview.Stock, selected.Count == 0);
            selected.Add(v);
        }
        if (selected.Count == 0)
        {
            return new RoundsBuildingResult { Verdict = NoSpace("bedroom_upgrade_site") };
        }
        return await Building.AdmitPreviewsAsync(new RoundsAdmission { State = state, Review = review, Owner = goal, Facts = facts, Method = method, Snapshot = snapshot, Selected = selected, Stock = stock, Purpose = Pol.Purpose.Shelter }, call, epoch).ConfigureAwait(false);
    }
}
