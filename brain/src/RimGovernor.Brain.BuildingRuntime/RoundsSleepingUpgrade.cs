namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The personal-share gate on in-place bedroom upgrades: charged steps begin at Reserves and must fit the
    /// owners' remaining share. stage is the review's colony stage.
    /// </summary>
    internal static Pol.RoomGate BedroomGate(Observation.ColonyProjection facts, Pol.ColonyStage stage)
    {
        // A piece is priced in the stuff UpgradeBedroom would build it in; the item facts carry no row for a stuff-made def.
        Func<string, (double Price, bool Ok)> piecePrice = def =>
            facts.BedPrice is null ? (0, false) : facts.BedPrice(new Pol.Resource(def), new Pol.Resource(PieceStuff(facts, def)));
        var gate = new Pol.RoomGate { Stage = stage, Shares = facts.PersonalShareOf, Items = facts.Facts.Items, BedPrice = facts.BedPrice, PiecePrice = piecePrice };
        // A suite is furnished with the bed the furnish step stages and the template pieces the closer fills, priced in their stuffs.
        var definitions = facts.Definitions.Select(d => new Pol.BenchDefinition { Name = d.Name, Available = d.Available }).ToList();
        var (bed, method) = SleepingDefinition(facts.Shapes.Furniture, definitions, null, false, true);
        if (method == Pol.SleepingMethod.Build)
        {
            gate = gate with { SuiteBed = new Pol.Resource(bed), SuiteBedStuff = new Pol.Resource(PieceStuff(facts, bed)) };
        }
        var pieces = new List<string>();
        foreach (var def in facts.Shapes.Furniture.BedroomUpgradePieces())
        {
            if (def != "" && facts.DefinitionAvailable(def).TryGetValue(out var v) && v)
            {
                pieces.Add(def);
            }
        }
        return gate with { SuitePieces = [.. pieces] };
    }

    /// <summary>The stuff a bedroom upgrade piece is built in: the allowed stocked one with the best rest effectiveness, else the ordinary placement stuff.</summary>
    internal static string PieceStuff(Observation.ColonyProjection facts, string def)
    {
        var stuff = facts.BuildStuff(def);
        if (facts.TryDefinition(def, out var d))
        {
            facts.TryStock(out var stock);
            try
            {
                stuff = d.StuffChoice(Observation.StuffCriterion.MaxRestEffectiveness, stock).Stuff;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
            }
        }
        return stuff;
    }

    private static bool DefinitionBuildable(Observation.ColonyProjection facts, string def) =>
        facts.DefinitionAvailable(def).TryGetValue(out var v) && v;

    /// <summary>The next room quality upgrade: one template piece for an owned bedroom below its target.</summary>
    internal static (Pol.RoomUpgrade Upgrade, bool Due) RoomUpgradeOf(Observation.ColonyProjection facts, Pol.ColonyStage stage)
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
        var targets = RoomQualityTargets(obs!, traits, tier, facts.Impressiveness);
        foreach (var (id, t) in CommonRoomTargets(obs!, tier, facts.Impressiveness))
        {
            targets.TryAdd(id, t);
        }
        targets = WithThroneTargets(facts, targets);
        return NextRoomUpgrade(obs!, UpgradeTargetsOf(facts, targets, stage), FurnitureRooms(rooms!, census, facts.Cells), def => DefinitionBuildable(facts, def), BedroomGate(facts, stage));
    }

    /// <summary>The next bed replacement step, read from the same census as RoomUpgradeOf.</summary>
    internal static (Pol.BedReplacement Replacement, bool Due) BedReplacementOf(Observation.ColonyProjection facts, Pol.ColonyStage stage)
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
        var materials = new Pol.BedMaterials { Cost = cost, Items = facts.Facts.Items, Gate = BedroomGate(facts, stage), Stock = stock };
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
        return NextBedReplacement(obs!, UpgradeTargetsOf(facts, RoomQualityTargets(obs!, traits, tier, facts.Impressiveness), stage), FurnitureRooms(rooms!, census, facts.Cells), def => DefinitionBuildable(facts, def), materials);
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
        return NextTitleFurniture(obs!, FurnitureRooms(rooms!, census, facts.Cells), def => DefinitionBuildable(facts, def));
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
        if (AnimalContainmentDefinition(facts.Definitions, facts.Shapes.Furniture.AnimalSpot) is not { } d)
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
        return NextCompanionBed(obs!, animals!, FurnitureRooms(rooms!, census, facts.Cells), facts.Shapes.Furniture, spot, available);
    }

    /// <summary>The next beauty lever: a plant pot or a prettier floor for a bedroom whose weakest stat is beauty.</summary>
    internal static (Pol.RoomUpgrade Upgrade, bool Due) BeautyUpgrade(Observation.ColonyProjection facts, Pol.ColonyStage stage)
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
        return NextBeautyUpgrade(obs!, UpgradeTargetsOf(facts, WithThroneTargets(facts, RoomQualityTargets(obs!, traits, tier, facts.Impressiveness)), stage), FurnitureRooms(rooms!, census, facts.Cells), def => DefinitionBuildable(facts, def), facts.Facts.Upkeep.Flooring, floors, BedroomGate(facts, stage));
    }
}

public sealed partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>Deconstructs a replaced bed, once per bed per Episode.</summary>
    internal async Task<RoundsBuildingResult> RemoveOldBedAsync(ControlState state, Sto.Rounds review, Sto.StandardState goal, Observation.RoundsReading reading, Pol.BedReplacement rep, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var method = new MethodId("bedroom-replace-remove-" + DigestHex(rep.Bed, 8));
        try
        {
            p.Journal.LoadOwnerMethod(goal, method, call);
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "old_bed_removal") };
        }
        catch (Sto.NotFoundException)
        {
        }
        // A real bed is packed, not deconstructed: the stored bed furnishes the next bedroom or bed spot (PackShellBed, ReinstallStoredBed).
        if (rep.Def == "Bed" || rep.Def == SleepingCoupleBedDefinition)
        {
            var value = new MoveBuilding(rep.Bed, rep.Def, rep.Cell, Rotation.South);
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewUninstallBuildingAction(new ActionId($"{id}-0"), value);
            var (result, _) = CommitCouple(state, goal, method, id, [action], call, epoch);
            return result;
        }
        return await Building.RetireBuildingAsync(state, review, goal, reading.Reading, rep.Bed, rep.Def, rep.Cell, "bedroom-replace-remove", "old_bed_removal", call, epoch).ConfigureAwait(false);
    }

    /// <summary>Previews and admits one upgrade piece, once per room and slot per Episode.</summary>
    internal async Task<RoundsBuildingResult> UpgradeBedroomAsync(ControlState state, Sto.Rounds review, Sto.StandardState goal, Observation.RoundsReading reading, Pol.RoomUpgrade u, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var facts = reading.Projection;
        var method = new MethodId($"bedroom-upgrade-{u.Slot}-{DigestHex(u.Room, 8)}");
        try
        {
            p.Journal.LoadOwnerMethod(goal, method, call);
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "bedroom_upgrade") };
        }
        catch (Sto.NotFoundException)
        {
        }
        var stuff = u.Stuff != "" ? u.Stuff : PieceStuff(facts, u.Def);
        var snapshot = state.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("upgradeBedroom: p.session.State() != state");
            }
        }
        Check();
        EquatableArray<Cell> cells = u.Cells.Count == 0 ? [u.Anchor] : u.Cells;
        var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var selected = new List<Pol.Preview>();
        for (var i = 0; i < cells.Count; i++)
        {
            var building = new Domain.Building(u.Def, cells[i], u.Rot, stuff);
            var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-{i}")), building, ConstructionTier.Sustain);
            var (preview, _) = await Native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
            var v = preview.Preview;
            var ck = v.CanPlace.TryGetValue(out var can);
            var sk = v.SafeToPlace.TryGetValue(out var safe);
            if (!ck || !can || !sk || !safe)
            {
                continue;
            }
            stock = MergeRoundsStock(stock, preview.Stock, selected.Count == 0);
            selected.Add(v);
        }
        if (selected.Count == 0)
        {
            return new RoundsBuildingResult { Verdict = NoSpace("bedroom_upgrade_site") };
        }
        return await Building.AdmitPreviewsAsync(new RoundsAdmission { State = state, Review = review, Owner = goal, Facts = facts, Method = method, Snapshot = snapshot, Selected = selected, Stock = stock, Purpose = Pol.Purpose.Shelter }, call, epoch).ConfigureAwait(false);
    }
}
