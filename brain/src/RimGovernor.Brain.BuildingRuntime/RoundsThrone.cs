namespace RimGovernor.Brain.BuildingRuntime;

// The throne room: MaintainHousing's sleeping planner shells the planned room, places the title's throne and
// furnishes it to the title's impressiveness (Policy NextThroneStep, ThroneRoomTargets). The layout review
// (ReplanLayout) plans the room from the title's minimum area. Once it stands, the throne is assigned to its
// holder through the generic Assign action; the step holds MaintainHousing open until the royalty read lists
// the holder as its owner.
public sealed partial class Rounder
{
    /// <summary>
    /// Reads the royalty facts onto the projection, resolves the ladder's throne definitions from the review's
    /// catalog and remembers the read for the planners. A colony without Royalty owes no throne room; a failed
    /// read leaves royalty unknown.
    /// </summary>
    internal void ReviewRoyalty(GenerationSnapshot snapshot, Observation.RoundsReading reading)
    {
        var projection = reading.Projection;
        if (!MethodEnabled(MaintainHousing) && !MethodEnabled(MaintainPsylink) && !MoodCasts)
        {
            return;
        }
        // The colony section gates Royalty; the ladder and permits come from the def mirror and the colonists'
        // own holdings from their pawn rows. A read that cannot be used leaves royalty unknown.
        Fact<Pol.RoyaltyFacts> royalty;
        try
        {
            royalty = projection.RoyaltyOf(reading.Frame.Pawns, reading.Frame.Catalog);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }
        if (!royalty.TryGetValue(out var facts))
        {
            return;
        }
        projection.Royalty = royalty;
        projection.AddDefinitions(reading.Frame, [.. ThroneThings(facts), .. ThroneFloorTerrains(reading.Frame.Catalog, facts)]);
        Census.RememberRoyalty(projection.Identity, projection.Royalty);
    }

    /// <summary>The throne definitions a planner's read names beside its own: the ladder's, from the review's royalty read.</summary>
    internal IReadOnlyList<string> RememberedThrones() =>
        Census.Remembered().TryGetValue(out var facts) ? ThroneThings(facts) : [];
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Every throne definition the ladder names, sorted.</summary>
    internal static List<string> ThroneThings(Pol.RoyaltyFacts f)
    {
        var names = new List<string>();
        foreach (var rung in f.Ladder)
        {
            rung.Throne.TryGetValue(out var req);
            foreach (var thing in req?.Things ?? [])
            {
                if (!names.Contains(thing))
                {
                    names.Add(thing);
                }
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>The throne room the colony owes: unknown royalty owes none.</summary>
    internal static (Pol.ThroneNeed Need, bool Owed) ThroneNeed(Observation.ColonyProjection facts)
    {
        if (!facts.Royalty.TryGetValue(out var royalty))
        {
            return (new Pol.ThroneNeed(), false);
        }
        var (ceremony, ok) = CeremonyThroneNeed(royalty);
        if (ok)
        {
            return (ceremony, true);
        }
        return NextThroneNeed(royalty);
    }

    /// <summary>
    /// The bestowing-ceremony quests the title claim gate allows to accept now (NextTitleClaim, ClaimQuests);
    /// none while the royalty read, sleeping census, plan, rooms or construction census is unknown.
    /// </summary>
    internal static IReadOnlyList<QuestId> TitleClaimQuests(Observation.ColonyProjection facts)
    {
        var rok = facts.Royalty.TryGetValue(out var royalty);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!rok || !sk || !pk || !rk || !ck || !census!.Colony)
        {
            return [];
        }
        var impressiveness = new Dictionary<string, double>(StringComparer.Ordinal);
        if (sleeping!.Rooms.TryGetValue(out var upkeep))
        {
            foreach (var room in upkeep)
            {
                if (room.Quality.TryGetValue(out var q))
                {
                    impressiveness[room.Id] = q.Impressiveness;
                }
            }
        }
        var claim = NextTitleClaim(new Pol.TitleClaimFacts
        {
            Royalty = royalty!,
            Sleeping = sleeping,
            BedroomPieces = FurnitureRooms(rooms!, census, facts.Cells),
            Plan = plan!,
            Rooms = rooms!,
            Built = census.Buildings,
            Impressiveness = impressiveness,
        });
        return ClaimQuests(royalty!, claim);
    }

    /// <summary>The projection's next throne step; none while the plan, the room census or the construction census is unknown.</summary>
    internal static Pol.ThroneStep ThroneStepOf(Observation.ColonyProjection facts)
    {
        var (need, owed) = ThroneNeed(facts);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        if (!owed || !pk || !rk || !ck || !census!.Colony)
        {
            return new Pol.ThroneStep();
        }
        var names = need.DefNames();
        var defs = new List<Pol.FurnitureDefinition>(names.Count);
        foreach (var d in facts.Definitions)
        {
            if (names.Contains(d.Name))
            {
                defs.Add(new Pol.FurnitureDefinition { Name = d.Name, Available = d.Available, Size = d.Size });
            }
        }
        var (ground, gk) = ColonyGround(facts);
        if (!gk)
        {
            return new Pol.ThroneStep();
        }
        facts.Royalty.TryGetValue(out var royalty);
        var step = NextThroneStep(plan!, rooms!, plan!.GroundWithRock(ground, NaturalRock(facts)), census.Buildings, need, defs, royalty!.Thrones);
        if (step.Kind != Pol.ThroneKind.None)
        {
            return step;
        }
        // Everything stands and is assigned: keep its braziers lit.
        var lk = facts.Facts.Upkeep.Lighting.TryGetValue(out var lighting);
        var wk = facts.WorkPawns.TryGetValue(out var workers);
        if (!lk || !wk)
        {
            return step;
        }
        return NextThroneRefuel(plan, rooms!, need, lighting!.Lamps, workers!);
    }

    /// <summary>Adds the throne room's impressiveness target to the room quality targets, so the room upgrade and the beauty upgrade furnish it.</summary>
    internal static Dictionary<string, Pol.RoomTarget>? WithThroneTargets(Observation.ColonyProjection facts, Dictionary<string, Pol.RoomTarget>? targets)
    {
        var (need, owed) = ThroneNeed(facts);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        if (!owed || !pk || !rk)
        {
            return targets;
        }
        targets ??= new Dictionary<string, Pol.RoomTarget>(StringComparer.Ordinal);
        foreach (var (id, t) in ThroneRoomTargets(plan!, rooms!, need))
        {
            targets[id] = t;
        }
        return targets;
    }

    /// <summary>
    /// Marks the standing throne room in the flooring census with the title's flooring requirement, so the
    /// flooring review measures it against the required terrain tags. The census rooms are copied: the
    /// projection's own stay untouched. No requirement, no standing room or an unlisted room leaves the census
    /// as read.
    /// </summary>
    internal static Pol.FlooringObservation WithThroneFloor(Observation.ColonyProjection facts, Pol.FlooringObservation v)
    {
        var (need, owed) = ThroneNeed(facts);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        if (!owed || !pk || !rk || need.FloorTags.Count == 0)
        {
            return v;
        }
        var (room, ok) = plan!.ThroneRoomFor(need.MinArea);
        if (!ok)
        {
            return v;
        }
        // Census: the floor requirement is keyed by the room's id.
        var (standing, found) = CensusRoomIn(room, rooms!);
        if (!found)
        {
            return v;
        }
        var copy = v.Rooms.ToList();
        for (var i = 0; i < copy.Count; i++)
        {
            if (copy[i].Id == standing.Id)
            {
                copy[i] = copy[i] with { RequiredTags = need.FloorTags, RequiredLabel = need.FloorLabel };
            }
        }
        return v with { Rooms = [.. copy] };
    }

    /// <summary>Every terrain the ladder's titles accept as a throne floor, from the mirror's terrain tags: the definitions the flooring read must carry for the throne tier to choose among them.</summary>
    internal static IReadOnlyList<string> ThroneFloorTerrains(Bridge.DefinitionCatalog catalog, Pol.RoyaltyFacts f)
    {
        var tags = new List<string>();
        foreach (var rung in f.Ladder)
        {
            rung.Throne.TryGetValue(out var req);
            tags.AddRange(req?.FloorTags ?? []);
        }
        return catalog.TerrainsWithTags(tags);
    }
}

public sealed partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>
    /// Answers a due throne step: the room through the shared build side (ReconcileRoom: the ring, floor and
    /// furniture the template and the plan still owe, installed from packed stock first), the assignment, the
    /// refuel. Furnishing beyond the template follows through the room upgrade.
    /// </summary>
    internal RoundsBuildingResult StageThrone(CancellationToken call, CancellationToken epoch, StepArbiter arbiter, PackedStock stock, ControlState state, Sto.Rounds review, Sto.StandardState goal, Observation.RoundsReading reading, Pol.ThroneStep step)
    {
        switch (step.Kind)
        {
            case Pol.ThroneKind.Reconcile:
                var inside = step.Room.Region.Bounds();
                return Building.ReconcileRoom(call, epoch, state, review, goal, reading, stock, new RoomReconcile
                {
                    Room = step.Room,
                    Template = step.Template,
                    Forbidden = step.Need.Forbids,
                    Tags = step.Need.FloorTags,
                    Name = string.Create(CultureInfo.InvariantCulture, $"throne-{inside.X}-{inside.Z}"),
                    Reason = "throne room",
                });
            case Pol.ThroneKind.Refuel:
                return RefuelThrone(call, epoch, arbiter, state, review, goal, reading, step);
            case Pol.ThroneKind.Assign:
                return AssignThrone(call, epoch, state, review, goal, step);
        }
        return new RoundsBuildingResult { Verdict = FieldUnavailable("throne_step") };
    }

    /// <summary>
    /// Commits one Assign of the standing throne to its holder, once per holder and throne per Episode
    /// (refused attempts retry within AssignMethod's bound). The royalty read refreshes on its own cadence, so
    /// a throne assigned this epoch reads unowned until then and the method is used.
    /// </summary>
    internal RoundsBuildingResult AssignThrone(CancellationToken call, CancellationToken epoch, ControlState state, Sto.Rounds review, Sto.StandardState goal, Pol.ThroneStep step)
    {
        var p = Reviewer.Player;
        var (method, barred) = AssignMethod(call, goal, $"throne-assign-{step.Need.Holder}-{step.Throne}", state.Snapshot);
        if (!barred.IsZero)
        {
            return new RoundsBuildingResult { Verdict = barred };
        }
        if (method.Value == "")
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "throne_assignment") };
        }
        var previous = step.PreviousThrone != "" ? PreviousAssignment.Known(step.PreviousThrone) : PreviousAssignment.Clear();
        var assign = new Domain.Assign(new PawnId(step.Need.Holder), step.Throne, previous);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewAssignAction(new ActionId($"{id}-0"), assign);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("assignThrone: p.session.State() != state");
        }
        var latest = p.Journal.LoadRounds(call);
        if (latest.Revision != review.Revision || !latest.Enabled)
        {
            throw new ControlException("assignThrone: latest.Revision != review.Revision || !latest.Enabled");
        }
        p.Journal.CommitOwnerMethod(call, goal, method, "", plan);
        return new RoundsBuildingResult { Verdict = BuildingReasonAdmitted };
    }
}
