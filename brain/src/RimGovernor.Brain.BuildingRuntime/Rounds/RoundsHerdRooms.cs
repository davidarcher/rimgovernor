namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    internal static readonly string[] HerdDefinitions =
    [
        "Wall",
        "Door",
        PolicyFunctions.PenFenceDefinition,
        PolicyFunctions.PenGateDefinition,
        PolicyFunctions.PenMarkerDefinition,
    ];

    /// <summary>The animal furniture the herd rooms are built from; unusable when a definition is unavailable or unsized, and a control fault when native never read it.</summary>
    internal static (HerdFurniture Furniture, bool Usable) HerdFurnitureOf(ColonyProjection facts)
    {
        var output = new HerdFurniture();
        var usable = true;
        foreach (var name in new[] { facts.Shapes.Furniture.AnimalSpot, facts.Shapes.Furniture.AnimalBed })
        {
            var (d, found) = AnimalContainmentDefinition(facts.Definitions, name);
            if (!found)
            {
                throw new ControlException($"herdFurniture: {name} was not read from the native catalog");
            }

            var sizeKnown = d.Size.TryGetValue(out var size);
            var availableKnown = d.Available.TryGetValue(out var available);
            if (availableKnown && !available && !sizeKnown)
            {
                throw new ControlException($"herdFurniture: the native catalog has no definition {name}");
            }

            if (!availableKnown || !available || !sizeKnown || size.Width < 1 || size.Height < 1)
            {
                usable = false;
                continue;
            }

            var def = new InteriorPieceDef { Def = name, Size = new Cell(size.Width, size.Height) };
            if (name == facts.Shapes.Furniture.AnimalSpot)
            {
                output = output with { Spot = def };
            }
            else
            {
                output = output with { Bed = def };
            }
        }

        var heater = facts.Shapes.Furniture.Heater;
        if (heater.Length != 0)
        {
            var (d, found) = AnimalContainmentDefinition(facts.Definitions, heater);
            if (!found)
            {
                throw new ControlException($"herdFurniture: {heater} was not read from the native catalog");
            }

            var sizeKnown = d.Size.TryGetValue(out var size);
            if (d.Available.TryGetValue(out var available) && available && sizeKnown && size.Width == 1 && size.Height == 1)
            {
                output = output with { Heater = new InteriorPieceDef { Def = heater, Size = new Cell(1, 1) } };
            }
        }

        var (marker, markerFound) = AnimalContainmentDefinition(facts.Definitions, PolicyFunctions.PenMarkerDefinition);
        if (markerFound)
        {
            var sizeKnown = marker.Size.TryGetValue(out var size);
            if (marker.Available.TryGetValue(out var available) && available && sizeKnown && size.Width >= 1 && size.Height >= 1)
            {
                output = output with { Marker = new InteriorPieceDef { Def = PolicyFunctions.PenMarkerDefinition, Size = new Cell(size.Width, size.Height) } };
            }
        }

        return (output, usable);
    }

    /// <summary>The next herd-room step; unknown while any planning fact is unread, and a no-op step when there is nothing to house.</summary>
    internal static (HerdStep Step, bool Known) HerdStepOf(ColonyProjection facts)
    {
        var ak = facts.Facts.AnimalUpkeep.Animals.TryGetValue(out var animals);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        if (!ak || !pk || !rk || !ck || !sk || !census!.Colony)
        {
            return (default!, false);
        }

        var ward = PolicyFunctions.WardBeds(animals!, facts.Facts.HerdPolicy());
        var barns = plan!.HerdRooms(PlannedRole.Barn).Count + plan.HerdRooms(PlannedRole.Pen).Count;
        if (animals!.Count == 0 || (barns == 0 && (ward == 0 || plan.HerdRooms(PlannedRole.Hospital).Count == 0)))
        {
            return (default!, true);
        }

        var (furniture, usable) = HerdFurnitureOf(facts);
        if (!usable)
        {
            return (default!, false);
        }

        var (ground, gk) = ColonyGround(facts);
        if (!gk)
        {
            return (default!, false);
        }

        return (PolicyFunctions.NextHerdStep(plan, rooms!, plan.GroundWithRock(ground, NaturalRock(facts)), census.Buildings, sleeping!.Beds, animals.Count, ward, furniture), true);
    }

    internal static Fact<bool> WardReadyOf(ColonyProjection facts)
    {
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        if (!pk || !rk || !ck || !sk || !census!.Colony)
        {
            return Unknown<bool>();
        }

        return PolicyFunctions.WardReady(plan!, rooms!, census.Buildings, sleeping!.Beds, facts.Shapes.Furniture.AnimalBed);
    }

    internal static Fact<bool> HerdRoomsOwed(ColonyProjection facts)
    {
        var (step, known) = HerdStepOf(facts);
        return known ? Known(step.Owed()) : Unknown<bool>();
    }
}

public sealed partial class RoundsAnimalContainmentPlanner
{
    internal RoundsAnimalContainmentResult StageHerdRooms(CancellationToken call, CancellationToken epoch, ControlState state, Rounds review, StandardState goal, Identity expected, Fact<IReadOnlyList<ConstructionClaim>> claims)
    {
        var reading = Reviewer.ObserveRooms(call, Reviewer.Native, expected, claims, HerdDefinitions);
        var (step, known) = HerdStepOf(reading.Projection);
        if (!known)
        {
            return new RoundsAnimalContainmentResult { Verdict = FieldUnavailable("herd_rooms") };
        }

        if (!step.Owed())
        {
            return new RoundsAnimalContainmentResult { Verdict = BuildingReasonNoDeficit };
        }

        RoundsBuildingResult result;
        switch (step.Kind)
        {
            case HerdStepKind.Medical:
                result = MarkHerdBedMedical(call, epoch, state, goal, reading.Projection, step.Bed);
                break;
            default:
                var inner = step.Room.Region.Bounds();
                var stock = NewPackedStock(Reviewer.Native, Boundary.Identity(state.Snapshot));
                result = Building.ReconcileRoom(call, epoch, state, review, goal, reading, stock, new RoomReconcile
                {
                    Room = step.Room,
                    Template = step.Template,
                    Name = $"herd-{inner.X}-{inner.Z}",
                    Reason = step.Role.WireName(),
                });
                break;
        }

        return new RoundsAnimalContainmentResult { Verdict = result.Verdict };
    }

    internal RoundsBuildingResult MarkHerdBedMedical(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, ColonyProjection facts, string bed)
    {
        var p = Reviewer.Player;
        if (Reviewer.Native is not IRoundsHospitalSource native)
        {
            throw new ControlException("markHerdBedMedical: the native source cannot read bed use");
        }

        var method = new MethodId("herd-medical-" + bed);
        try
        {
            p.Journal.LoadMethod(call, goal.Standard.Id, goal.Standard.Episode, method);
            return new RoundsBuildingResult { Verdict = WaitFor(Cause.MethodUsed, "herd_bed_medical_method") };
        }
        catch (NotFoundException)
        {
        }

        var (target, _) = native.ReadBedUseTarget(call, Boundary.Identity(state.Snapshot), bed);
        try
        {
            Boundary.Context(target.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("markHerdBedMedical: stale bed read", e);
        }

        if (target.Context.Tick < facts.Identity.Tick.Value)
        {
            throw new ControlException("markHerdBedMedical: stale bed read");
        }

        if (target.Medical)
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Cause.MethodUsed, "herd_bed_medical") };
        }

        var patch = BedUse.NewBedMedical(bed, true);
        p.CommitBedPatch(call, epoch, state, goal, method, patch, null);
        return new RoundsBuildingResult { Verdict = BuildingReasonAdmitted };
    }
}
