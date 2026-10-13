namespace RimGovernor.Brain.BuildingRuntime;

// The initial shelter is raised around its bunks. A fresh starter shell is three rungs under the one goal,
// each a method of its epoch:
//
//  1. shelter-spots: sleeping spots on the shelter template's bunk slots (PlanShelterBunks), placed before
//     anything is roofed. They are an interim only: a pawn on a spot still sleeps on the ground.
//  2. shelter-beds: the beds, the first construction on the site, on the same slots, which keep off the
//     ring's corners, the entrance aisle and the starter storage patch. The native refuses a bed blueprint
//     over a standing spot, so the standing spots are deleted first (the clearing method, UpgradeBunks; a
//     bedroll is packed to storage instead) and the beds go on the freed cells.
//  3. the ring itself: the planned room's ring through one reconcile. The ring stands on the plan's
//     footprint, which the bunk slots lie inside and keep off the corners of.
//
// The room is one continuous reconcile (StepShelterRoom): the first review admits the spots and the ring
// together, and every later review goes on up the ladder (the clearing, then the beds) while the ring's wave
// is open, the walls and the bunks standing on disjoint cells (the store's ShelterOpenWorkExempt admits them
// side by side). A bed rung refused whole (no wood at all) never holds the ring; the sleeping family
// furnishes the room later.

/// <summary>One bunk slot: the anchor and rotation a building order names (the footprint is BunkRect).</summary>
internal readonly record struct ShelterBunk(Cell Anchor, Rotation Rot)
{
    public Pol.Rectangle Rect() => BunkRect(Anchor, Rot);
}

/// <summary>One review's context for siting the initial shelter.</summary>
internal sealed class ShelterSite
{
    public ControlState State { get; init; }
    public Sto.Rounds Review { get; init; } = new();
    public Sto.StandardState Owner { get; init; } = new();
    public Observation.ColonyProjection Facts { get; init; } = new();
    public Observation.ColonyReading Read { get; init; } = new();
    public Action Check { get; init; } = () => { };
}

/// <summary>What the Episode already placed: the slots each bound bunk method names, and whether that method is still open.</summary>
internal sealed class ShelterBunkRecord
{
    public Dictionary<MethodId, List<ShelterBunk>> Placed { get; } = [];
    public Dictionary<MethodId, bool> Open { get; } = [];

    public bool Bound(MethodId m) => Placed.ContainsKey(m);

    /// <summary>Whether a placement rung of the ladder is already bound.</summary>
    public bool AnyBound() => Bound(BuildingRuntimePackage.ShelterSpotsMethodId) || Bound(BuildingRuntimePackage.ShelterBedrollsMethodId) || Bound(BuildingRuntimePackage.ShelterBedsMethodId);
}

internal static partial class BuildingRuntimePackage
{
    internal static readonly MethodId ShelterSpotsMethodId = new("shelter-spots");
    internal static readonly MethodId ShelterBedrollsMethodId = new("shelter-bedrolls");
    internal static readonly MethodId ShelterBedsMethodId = new("shelter-beds");
    internal static readonly MethodId ShelterClearBedrollsMethodId = new("shelter-clear-bedrolls");
    internal static readonly MethodId ShelterClearBedsMethodId = new("shelter-clear-beds");
    internal const string ShelterBedDefinition = "Bed";

    /// <summary>Whether m is a bunk rung or clearing step under the initial shelter goal (the store's ShelterBunkMethods names the same set).</summary>
    internal static bool IsShelterBunkMethod(MethodId m) =>
        m == ShelterSpotsMethodId || m == ShelterBedrollsMethodId || m == ShelterBedsMethodId || m == ShelterClearBedrollsMethodId || m == ShelterClearBedsMethodId;

    /// <summary>The placement method and the clearing method of a bunk definition; a spot has no clearing step.</summary>
    internal static (MethodId Place, MethodId Clear) ShelterRung(string definition)
    {
        if (definition == SleepingBedrollDefinition)
        {
            return (ShelterBedrollsMethodId, ShelterClearBedrollsMethodId);
        }
        if (definition == ShelterBedDefinition)
        {
            return (ShelterBedsMethodId, ShelterClearBedsMethodId);
        }
        return (ShelterSpotsMethodId, new MethodId(""));
    }

    /// <summary>Orders the ladder: spot, bedroll, bed.</summary>
    internal static int BunkRank(string definition) =>
        definition == SleepingBedrollDefinition ? 1 : definition == ShelterBedDefinition ? 2 : 0;

    /// <summary>The template's bunk slots for the site as orders.</summary>
    internal static List<ShelterBunk> ShelterBunkSlots(Pol.PlannedRoom room, Observation.ColonyProjection facts, int occupants)
    {
        var output = new List<ShelterBunk>();
        facts.LayoutPlan.TryGetValue(out var plan);
        foreach (var p in PlanShelterBunks(room, plan!.RoomRock(room, facts.Cells).Dig, facts.Shapes, occupants, ShelterCampfires(facts), ShelterCoolers(facts), null))
        {
            output.Add(new ShelterBunk(p.Anchor(), p.Rot));
        }
        return output;
    }

    /// <summary>
    /// The bed rung's definition and slots on the ladder: Bed when it is buildable, else as many bedrolls as
    /// the stock of their first stocked stuff covers, else Bed (which AdmitBunks refuses).
    /// </summary>
    internal static (string Definition, List<ShelterBunk> Slots) ShelterBeds(Observation.ColonyProjection facts, List<ShelterBunk> slots)
    {
        if (facts.DefinitionAvailable(ShelterBedDefinition).TryGetValue(out var bed) && bed)
        {
            return (ShelterBedDefinition, slots);
        }
        facts.DefinitionAvailable(SleepingBedrollDefinition).TryGetValue(out var bedroll);
        if (bedroll)
        {
            var (_, count, ok) = facts.StockedStuff(SleepingBedrollDefinition);
            if (ok)
            {
                return (SleepingBedrollDefinition, slots.Take((int)Math.Min(slots.Count, count)).ToList());
            }
        }
        return (ShelterBedDefinition, slots);
    }

    /// <summary>Reports a planned room's ring method: a build wave of a "role-shell-x-z" reconcile (PlannedRoomMethod), for acceptance tooling reading the journal.</summary>
    public static bool IsShellMethod(MethodId method) => StorePackage.IsRoomShellMethod(method);

    /// <summary>Names the bunk rungs' methods under the initial shelter goal.</summary>
    public static MethodId ShelterSpotsMethod() => ShelterSpotsMethodId;

    public static MethodId ShelterBedsMethod() => ShelterBedsMethodId;

    /// <summary>Names the spot deletion the bed rung runs first.</summary>
    public static MethodId ShelterClearBedsMethod() => ShelterClearBedsMethodId;

    /// <summary>Whether a bed's head cell lies inside one of the slots' cells.</summary>
    internal static bool OnBunkSlot(Cell cell, IEnumerable<ShelterBunk> bunks)
    {
        foreach (var b in bunks)
        {
            if (BunkCells(b.Anchor, b.Rot).Contains(cell))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the native footprint is exactly the cells of the bunk slot's rectangle: the anchor and rotation
    /// the slot names took the footprint the template planned.
    /// </summary>
    internal static bool SameBunkFootprint(Pol.Rectangle want, IReadOnlyList<Cell> footprint)
    {
        if (footprint.Count != (long)want.Width * want.Height)
        {
            return false;
        }
        foreach (var c in footprint)
        {
            if (c.X < want.X || c.X >= want.X + want.Width || c.Z < want.Z || c.Z >= want.Z + want.Height)
            {
                return false;
            }
        }
        return footprint.Count == 2 && footprint[0] != footprint[1];
    }
}

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>Reads the bunk methods bound under the goal's epoch.</summary>
    internal ShelterBunkRecord ShelterBunks(CancellationToken call, Sto.StandardState goal, Fact<Pol.CurrentConstruction> census)
    {
        var journal = Reviewer.Player.Journal;
        var record = new ShelterBunkRecord();
        foreach (var method in new[] { ShelterSpotsMethodId, ShelterBedrollsMethodId, ShelterBedsMethodId, ShelterClearBedrollsMethodId, ShelterClearBedsMethodId })
        {
            Sto.MethodState m;
            try
            {
                m = journal.LoadOwnerMethod(call, goal, method);
            }
            catch (Sto.NotFoundException)
            {
                continue;
            }
            var plan = journal.LoadPlan(call, m.Plan);
            var bunks = new List<ShelterBunk>();
            foreach (var action in plan.Spec.Actions)
            {
                if (action.Building is { } b)
                {
                    bunks.Add(new ShelterBunk(b.Cell, b.Rotation));
                }
            }
            record.Placed[method] = bunks;
            record.Open[method] = Sto.PlanState.PlanWorkOpen(plan, census);
        }
        return record;
    }

    /// <summary>
    /// Raises the layout plan's shelter room for this review. The owed ring and the bunk rungs go through one
    /// reconcile of the planned room in the same review: a rung admitted this review is admitted beside the
    /// ring, and an open ring wave is existing work that still lets the ladder go on. A standing ring waits on
    /// its roof: the window the completed wave lends, else the earlier-shell wait. roofingOnly is an indoor
    /// furnishing still waiting on its roof, which holds the rungs and the ring alike.
    /// </summary>
    internal RoundsBuildingResult StepShelterRoom(CancellationToken call, CancellationToken epoch, ShelterSite s, bool roofingOnly)
    {
        if (!s.Facts.LayoutPlan.TryGetValue(out var plan))
        {
            return new RoundsBuildingResult { Verdict = NoSpace("planned_shell_room") };
        }
        Pol.PlannedRoom? room = null;
        foreach (var candidate in plan.Rooms)
        {
            if (candidate.Role == Pol.PlannedRole.Shelter)
            {
                room = candidate;
                break;
            }
        }
        if (room is null)
        {
            return new RoundsBuildingResult { Verdict = NoSpace("planned_shell_room") };
        }
        var name = PlannedRoomMethod(room).ToString();
        var ring = ShelterRingMethods(call, s.Owner, name);
        if (!PlannedRoomOwed(s.Facts, Pol.PlannedRole.Shelter).Owed)
        {
            // The ring stands: nothing to raise while it waits on its roof.
            if (ring.Count == 0)
            {
                return new RoundsBuildingResult { Verdict = BuildingShellBlocked };
            }
            // The bunk ladder goes on up its rungs (spots deleted, beds placed) under the same reconcile; an
            // admitted rung ends the review, a hold does not displace the roof wait.
            if (!roofingOnly && Phase == HousingPhase.Shelter && plan.RoomRock(room, s.Facts.Cells).Dig.Count == 0)
            {
                var (result, handled) = StepShelterBunks(call, epoch, s, room);
                if (handled && result.Verdict == BuildingReasonAdmitted)
                {
                    return result;
                }
            }
            uint ticks = 0;
            foreach (var built in ring)
            {
                ticks = Math.Max(ticks, ShelterNativeWorkTicks(built, s.State.Snapshot, s.Facts.Identity.Tick));
            }
            s.Check();
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "shelter_method"), NativeWorkTicks = ticks };
        }
        if (roofingOnly)
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "roofing_method") };
        }
        // The planned room is one reconcile: the bunks and the ring are each admitted this review when
        // admissible. The walls stand on cells the bunks never take, so neither waits on the other. The bunks
        // stand on the room once its rock is dug (the ring's reconcile mines it before any wall).
        var owner = s.Owner;
        var bunks = new RoundsBuildingResult();
        if (Phase == HousingPhase.Shelter && plan.RoomRock(room, s.Facts.Cells).Dig.Count == 0)
        {
            var (result, handled) = StepShelterBunks(call, epoch, s, room);
            if (handled && result.Verdict == BuildingReasonAdmitted)
            {
                // The admission moved the goal's revision: the ring is admitted under the goal as it now stands.
                bunks = result;
                var (renewed, workable) = Reviewer.Player.Journal.Workable(call, s.Review, Concern);
                if (!workable)
                {
                    return result;
                }
                owner = renewed;
            }
        }
        if (ring.Any(Sto.PlanState.PlanOpen))
        {
            // The ring's wave is open: existing work, nothing to preview or admit.
            if (bunks.Verdict == BuildingReasonAdmitted)
            {
                return bunks;
            }
            return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
        }
        var reconciled = ReconcileRoom(call, epoch, s.State, s.Review, owner, new Observation.RoundsReading { ColonyReading = s.Read }, null!, new RoomReconcile { RingOnly = true, Room = room, Name = name, Reason = "starter shelter" });
        if (reconciled.Verdict != BuildingReasonAdmitted && bunks.Verdict == BuildingReasonAdmitted)
        {
            return bunks;
        }
        return reconciled;
    }

    /// <summary>The plans of the ring's build waves bound under the goal's epoch, retired ones included: the record of a ring begun.</summary>
    internal List<Sto.PlanState> ShelterRingMethods(CancellationToken call, Sto.StandardState goal, string name)
    {
        var ring = new List<Sto.PlanState>();
        var seen = new HashSet<PlanId>();
        foreach (var m in goal.Methods.Concat(goal.History))
        {
            if (!m.MethodId.Value.StartsWith(name + "-build-", StringComparison.Ordinal) || !seen.Add(m.Plan))
            {
                continue;
            }
            ring.Add(Reviewer.Player.Journal.LoadPlan(call, m.Plan));
        }
        return ring;
    }

    /// <summary>Places the initial shelter's bunk rungs on the planned room's slots. handled is false when the rungs have nothing more to place, so the ring is raised this review.</summary>
    internal (RoundsBuildingResult Result, bool Handled) StepShelterBunks(CancellationToken call, CancellationToken epoch, ShelterSite s, Pol.PlannedRoom room)
    {
        var record = ShelterBunks(call, s.Owner, s.Facts.Facts.CurrentConstruction);
        var indoor = Copy();
        indoor.Shelter = false;
        indoor.Definition = "SleepingSpot";
        var (owed, _, reason) = indoor.Selection(s.Facts);
        if (!reason.IsZero)
        {
            return (new RoundsBuildingResult { Verdict = reason }, true);
        }
        // The slots are shared: beds stand where the spots were placed.
        var slots = record.Placed.GetValueOrDefault(ShelterSpotsMethodId) ?? [];
        if (!record.AnyBound())
        {
            slots = ShelterBunkSlots(room, s.Facts, (int)owed);
            var (spots, admitted) = AdmitBunks(call, epoch, s, ShelterSpotsMethodId, "SleepingSpot", slots);
            if (admitted)
            {
                return (spots, true);
            }
        }
        // The upgrade deletes the spots and places the bed (or bedroll) on the freed slots, never over a standing spot.
        var (definition, bunks) = ShelterBeds(s.Facts, slots);
        if (!record.Bound(ShelterRung(definition).Place))
        {
            var result = UpgradeBunks(call, epoch, s, record, definition, bunks);
            if (result is not null)
            {
                return (result, true);
            }
        }
        return (new RoundsBuildingResult(), false);
    }

    /// <summary>
    /// Swaps the standing lower-rung bunks on the bed rung's slots for the target definition: a sleeping spot
    /// is free and is deconstructed, a bedroll is packed to storage (uninstalled), each as one clearing method,
    /// and the bed or bedroll is placed on the freed cells once none stands. It returns null to leave the rung
    /// to a later review and go on to the ring (nothing to place, the definition refused, a clearing that did
    /// not take), or the handled result: the clearing or the bunks admitted, or a hold while a lower rung or
    /// the clearing is still open.
    /// </summary>
    internal RoundsBuildingResult? UpgradeBunks(CancellationToken call, CancellationToken epoch, ShelterSite s, ShelterBunkRecord record, string definition, List<ShelterBunk> bunks)
    {
        var (place, clear) = ShelterRung(definition);
        if (bunks.Count == 0 || !DefinitionsGate(s.Facts, [definition], false).IsZero)
        {
            return null;
        }
        foreach (var (m, open) in record.Open)
        {
            if (open && m != place)
            {
                return new RoundsBuildingResult { Verdict = BuildingBunksOpen };
            }
        }
        if (!s.Facts.Facts.Sleeping.TryGetValue(out var obs))
        {
            return null;
        }
        var standing = new List<Pol.SleepingBed>();
        foreach (var b in obs.Beds)
        {
            if ((b.Definition == SleepingSpotDefinition || b.Definition == SleepingBedrollDefinition) && BunkRank(b.Definition.ToString()) < BunkRank(definition) && OnBunkSlot(b.Cell, bunks))
            {
                standing.Add(b);
            }
        }
        if (standing.Count == 0)
        {
            var (result, admitted) = AdmitBunks(call, epoch, s, place, definition, bunks);
            return admitted ? result : null;
        }
        if (record.Bound(clear))
        {
            // Cleared once per Episode: a clearing that closed with pieces still standing leaves the upgrade to the sleeping family.
            return null;
        }
        var snapshot = s.State.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };
        var actions = new List<DomainAction>(standing.Count);
        for (var i = 0; i < standing.Count; i++)
        {
            var b = standing[i];
            var id = new ActionId(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-{i}"));
            if (b.Definition == SleepingSpotDefinition)
            {
                actions.Add(DomainAction.NewDeconstructionAction(id, new Deconstruction(b.Id, b.Definition.ToString(), b.Cell)));
            }
            else
            {
                actions.Add(DomainAction.NewUninstallBuildingAction(id, new MoveBuilding(b.Id, b.Definition.ToString(), b.Cell, Rotation.South)));
            }
        }
        var plan = new PlanSpec(snapshot.Plan, new PlanRevision(1), actions);
        var step = new ExcavationStep { State = s.State, Review = s.Review, Owner = s.Owner, Facts = s.Facts, Read = s.Read };
        var admittedResult = AdmitExcavation(call, epoch, step, snapshot, clear, plan, null, new Pol.StockObservation { Snapshot = snapshot, Tick = s.Facts.Identity.Tick }, s.Check);
        return admittedResult.Verdict != BuildingReasonAdmitted ? null : admittedResult;
    }

    /// <summary>
    /// Previews the bunks natively and admits the placeable ones as the rung's method. It reports
    /// admitted=false, without error, when the rung has nothing to place (no candidate, definition
    /// unavailable, none placeable, or the method refused whole), so the next rung is tried in the same review.
    /// </summary>
    internal (RoundsBuildingResult Result, bool Admitted) AdmitBunks(CancellationToken call, CancellationToken epoch, ShelterSite s, MethodId method, string definition, List<ShelterBunk> bunks)
    {
        var none = (new RoundsBuildingResult(), false);
        if (bunks.Count == 0)
        {
            return none;
        }
        if (!DefinitionsGate(s.Facts, [definition], false).IsZero)
        {
            return none;
        }
        var stuff = BedStuff(s.Facts, definition);
        var snapshot = s.State.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };
        var actions = new List<DomainAction>(bunks.Count);
        for (var i = 0; i < bunks.Count; i++)
        {
            var b = new Domain.Building(definition, bunks[i].Anchor, bunks[i].Rot, stuff);
            actions.Add(DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-{i}")), b, BuildTier.Survive));
        }
        s.Check();
        var previews = new List<BuildingPreview>();
        if (Native is IShellBatchPreviewer batch)
        {
            previews = [.. (batch.PreviewBuildings(call, actions, snapshot)).Previews];
        }
        else
        {
            foreach (var action in actions)
            {
                var (preview, _) = ((IRoundsBuildingSource)Native).PreviewBuilding(call, action, snapshot);
                previews.Add(preview);
            }
        }
        if (previews.Count != actions.Count)
        {
            throw new ControlException("admitBunks: len(previews) != len(actions)");
        }
        var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = s.Facts.Identity.Tick };
        var selected = new List<Pol.Preview>();
        for (var i = 0; i < previews.Count; i++)
        {
            var v = previews[i].Preview;
            if (!v.MadeFromStuff.TryGetValue(out var made) || made != (stuff != ""))
            {
                continue;
            }
            var canKnown = v.CanPlace.TryGetValue(out var can);
            var safeKnown = v.SafeToPlace.TryGetValue(out var safe);
            var footprintKnown = v.Footprint.TryGetValue(out var footprint);
            if (!canKnown || !can || !safeKnown || !safe || !footprintKnown || !SameBunkFootprint(bunks[i].Rect(), footprint!))
            {
                continue;
            }
            stock = MergeRoundsStock(stock, previews[i].Stock, selected.Count == 0);
            selected.Add(v);
        }
        if (selected.Count == 0)
        {
            return none;
        }
        var result = AdmitPreviews(call, epoch, new RoundsAdmission
        {
            State = s.State,
            Review = s.Review,
            Owner = s.Owner,
            Facts = s.Facts,
            Method = method,
            Snapshot = snapshot,
            Selected = selected,
            Stock = stock,
            Purpose = Pol.Purpose.Rounds,
        });
        return (result, result.Verdict == BuildingReasonAdmitted);
    }
}
