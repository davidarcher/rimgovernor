using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

// The throne room: the shelter family's sleeping planner (shelter/sleeping) shells the planned room, places the title's throne and
// furnishes it to the title's impressiveness (Policy NextThroneStep, ThroneRoomTargets). The layout review
// (ReplanLayout) plans the room from the title's minimum area. Once it stands, the throne is assigned to its
// holder through the generic Assign action; the sleeping Need stays unmet until the royalty read lists the
// holder as its owner.
public sealed partial class Rounder
{
    /// <summary>Remembers the review's royalty read for the planners' throne definitions and the defense round's calls.</summary>
    internal void ReviewRoyalty(Observation.RoundsReading reading) => Census.RememberRoyalty(reading.Projection.Facts.Royalty);

    /// <summary>The throne definitions a planner's read names beside its own: the ladder's, from the review's royalty read.</summary>
    internal IReadOnlyList<string> RememberedThrones() =>
        Census.Remembered() is { } facts ? RoundsSleepingUpkeepPlanner.ThroneThings(facts) : [];
}

partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>Every throne definition the ladder names, sorted.</summary>
    internal static List<string> ThroneThings(Pol.RoyaltyFacts f)
    {
        var names = new List<string>();
        foreach (var rung in f.Ladder)
        {
            foreach (var thing in rung.Throne.Things)
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

    /// <summary>The throne room the colony owes: a colony without Royalty owes none.</summary>
    internal static (Pol.ThroneNeed Need, bool Owed) ThroneNeed(Observation.ColonyProjection facts)
    {
        if (facts.Facts.Royalty is not Pol.RoyaltyState.Active { Facts: var royalty })
        {
            return (new Pol.ThroneNeed(), false);
        }
        if (CeremonyRules.CeremonyThroneNeed(royalty) is { } ceremony)
        {
            return (ceremony, true);
        }
        return ThroneRules.NextThroneNeed(royalty);
    }

    /// <summary>
    /// The bestowing-ceremony quests the title claim gate allows to accept now (NextTitleClaim, ClaimQuests);
    /// none without Royalty or while the sleeping census, plan, rooms or construction census is unknown.
    /// </summary>
    internal static IReadOnlyList<QuestId> TitleClaimQuests(Observation.ColonyProjection facts)
    {
        if (facts.Facts.Royalty is not Pol.RoyaltyState.Active { Facts: var royalty })
        {
            return [];
        }
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var census = facts.Facts.CurrentConstruction;
        if (!sk || !pk || !rk || census is null)
        {
            return [];
        }
        var impressiveness = new Dictionary<string, double>(StringComparer.Ordinal);
        if (sleeping!.Rooms.TryGetValue(out var upkeep))
        {
            foreach (var room in upkeep)
            {
                impressiveness[room.Id] = room.Quality.Impressiveness;
            }
        }
        var claim = TitleClaimRules.NextTitleClaim(new Pol.TitleClaimFacts
        {
            Royalty = royalty,
            Sleeping = sleeping,
            BedroomPieces = FurnitureRoomsRules.FurnitureRooms(rooms!, census, facts.Cells),
            Plan = plan!,
            Rooms = rooms!,
            Built = census.Buildings,
            Impressiveness = impressiveness,
        });
        return CeremonyRules.ClaimQuests(royalty!, claim);
    }

    /// <summary>The projection's next throne step; none while the plan, the room census or the construction census is unknown.</summary>
    internal static Pol.ThroneStep ThroneStepOf(Observation.ColonyProjection facts)
    {
        var (need, owed) = ThroneNeed(facts);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var census = facts.Facts.CurrentConstruction;
        if (!owed || !pk || !rk || census is null)
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
        var (ground, gk) = RoundsBuildingPlanner.ColonyGround(facts);
        if (!gk)
        {
            return new Pol.ThroneStep();
        }
        // ThroneNeed owes a room only with Royalty active.
        var thrones = facts.Facts.Royalty is Pol.RoyaltyState.Active { Facts: var royalty } ? royalty.Thrones : [];
        var step = ThroneRules.NextThroneStep(plan!, rooms!, plan!.GroundWithRock(ground, RoundsBuildingPlanner.NaturalRock(facts)), census.Buildings, need, defs, thrones);
        if (step.Kind != Pol.ThroneStepKind.None)
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
        return ThroneRules.NextThroneRefuel(plan, rooms!, need, lighting!.Lamps, workers!);
    }

    /// <summary>
    /// Adds the throne room's and the worship room's impressiveness targets (the title's, the presence demands', #3808) to
    /// the room quality targets, so the room upgrade and the beauty upgrade furnish them.
    /// </summary>
    internal static Dictionary<string, Pol.RoomTarget>? WithRequirementTargets(Observation.ColonyProjection facts, Dictionary<string, Pol.RoomTarget>? targets)
    {
        var (need, owed) = ThroneNeed(facts);
        var pk = facts.LayoutPlan.TryGetValue(out var plan);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        if (!pk || !rk)
        {
            return targets;
        }
        var throne = owed ? ThroneRules.ThroneRoomTargets(plan!, rooms!, need) : new Dictionary<string, Pol.RoomTarget>();
        var worship = Pol.WorshipRules.WorshipRoomTargets(plan!, rooms!, facts.Facts.Worship);
        if (throne.Count == 0 && worship.Count == 0)
        {
            return targets;
        }
        targets ??= new Dictionary<string, Pol.RoomTarget>(StringComparer.Ordinal);
        foreach (var (id, t) in throne.Concat(worship))
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
        if (!owed || !facts.LayoutPlan.TryGetValue(out var plan) || !facts.Rooms.TryGetValue(out var rooms) || need.FloorTags.Count == 0)
        {
            return v;
        }
        var (room, ok) = plan.ThroneRoomFor(need.MinArea);
        if (!ok)
        {
            return v;
        }
        // Census: the floor requirement is keyed by the room's id.
        var (standing, found) = PlannedRoom.CensusRoomIn(room, rooms);
        if (!found || standing is null)
        {
            return v;
        }
        var copy = v.Rooms.ToList();
        for (var i = 0; i < copy.Count; i++)
        {
            if (copy[i].Id == standing.Id)
            {
                copy[i] = copy[i] with { RequiredTags = need.FloorTags };
            }
        }
        return v with { Rooms = [.. copy] };
    }
}

public sealed partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>
    /// Answers a due throne step: the room through the shared build side (ReconcileRoom: the ring, floor and
    /// furniture the template and the plan still owe, installed from packed stock first), the assignment, the
    /// refuel. Furnishing beyond the template follows through the room upgrade.
    /// </summary>
    internal async Task<RoundsBuildingResult> StageThroneAsync(StepArbiter arbiter, PackedStock stock, ControlState state, Sto.Rounds review, Sto.NeedOwner goal, Observation.RoundsReading reading, Pol.ThroneStep step, CancellationToken call, CancellationToken epoch)
    {
        switch (step.Kind)
        {
            case Pol.ThroneStepKind.Reconcile:
                var inside = step.Room.Region.Bounds();
                return await Building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile
                {
                    Room = step.Room,
                    Template = step.Template,
                    Forbidden = step.Need.Forbids,
                    Tags = step.Need.FloorTags,
                    Name = string.Create(CultureInfo.InvariantCulture, $"throne-{inside.X}-{inside.Z}"),
                    Reason = "throne room",
                }, call, epoch).ConfigureAwait(false);
            case Pol.ThroneStepKind.Refuel:
                return RefuelThrone(arbiter, state, review, goal, reading, step, call, epoch);
            case Pol.ThroneStepKind.Assign:
                return AssignThrone(state, review, goal, step, call, epoch);
        }
        return new RoundsBuildingResult { Verdict = FieldUnavailable("throne_step") };
    }

    /// <summary>
    /// Commits one Assign of the standing throne to its holder, once per holder and throne per Episode
    /// (refused attempts retry within AssignMethod's bound). The royalty read refreshes on its own cadence, so
    /// a throne assigned this epoch reads unowned until then and the method is used.
    /// </summary>
    internal RoundsBuildingResult AssignThrone(ControlState state, Sto.Rounds review, Sto.NeedOwner goal, Pol.ThroneStep step, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var (method, barred) = AssignMethod(goal, $"throne-assign-{step.Need.Holder}-{step.Throne}", state.Snapshot, call);
        if (!barred.IsZero)
        {
            return new RoundsBuildingResult { Verdict = barred };
        }
        if (method.Value == "")
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "throne_assignment") };
        }
        var previous = step.PreviousThrone != "" ? PreviousAssignment.KnownPrevious(step.PreviousThrone) : PreviousAssignment.ClearPrevious();
        var assign = new Domain.Assign(step.Need.Holder, step.Throne, previous);
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
        var commit = p.Journal.CommitOwnerWork(goal, method, "", plan, call);
        return new RoundsBuildingResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(commit) };
    }
}
