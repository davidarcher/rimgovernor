using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RimGovernor.Brain.BuildingRuntime;

// The build side of the reconciler as a shared library: an owning concern calls ReconcileRoom for its own room
// and the reconciler decides how. It reads the room's ground (clearance census on the room's rectangle, the
// packed stock), asks the flooring review for the wanted floor, diffs (Pol ReconcileRoom) and commits the next
// wave: removals first (pack, deconstruct, floor out), then installs from packed stock, then everything built
// on site (doors, walls, floors, furniture), each wave one method. A placement the native preview refuses is
// not ready this pass: the room waits. The ring's removals (door and wall out, roof off) are the plan-wide
// clear side's.

/// <summary>Chooses the wall's and the door's stuff for a ring; refusal names what is unavailable.</summary>
internal delegate (string WallStuff, string DoorStuff, Verdict Refusal, bool Ok) ShellStuff(Observation.PlanningDefinition wall, Observation.PlanningDefinition door);

/// <summary>
/// One owner's room: the PlannedRoom, its furniture template and what the owner forbids in it. Name prefixes
/// the methods, Reason is the admission's short why and Tags the title's floor tags (the throne room's).
/// </summary>
internal sealed class RoomReconcile
{
    public Pol.PlannedRoom Room { get; init; } = new();
    public IReadOnlyList<Pol.WantedPiece> Template { get; init; } = [];
    public Func<string, bool>? Forbidden { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Name { get; init; } = "";
    public string Reason { get; init; } = "";

    /// <summary>
    /// Reconciles the ring and its doors alone: no furniture template, floors or wall-stuff swaps, and no
    /// clearance read, so a caller that holds no packed stock (dining, kitchen, the gear rooms, the
    /// incinerator) can use it. A ring cell the native refuses is no_space, as for an outdoor ring.
    /// </summary>
    public bool RingOnly { get; init; }

    /// <summary>
    /// Narrows a RingOnly reconcile to the room's planned vents and roofs (EnvelopeOps): the plan-wide
    /// climate envelope's call, which owns nothing else of the room (#2962).
    /// </summary>
    public bool ClimateOnly { get; init; }

    /// <summary>Overrides the wall's and door's stuff (the incinerator's fireproof choice); null builds both from the one shared shell stuff.</summary>
    public ShellStuff? Stuff { get; init; }
}

/// <summary>A furniture or ring cell the native preview refused and the thing it reported in the way.</summary>
internal sealed record RefusedPlacement(string Def, Cell Cell, string Blocker)
{
    public string Key() => string.Create(CultureInfo.InvariantCulture, $"{Def}@{Cell.X},{Cell.Z}");
}

/// <summary>One room of a reconcile with the operations its diff leaves.</summary>
internal sealed class RoomWork
{
    public RoomReconcile Rr { get; init; } = new();
    public IReadOnlyList<Pol.Operation> Ops { get; init; } = [];

    /// <summary>The foreign things the room leaves standing.</summary>
    public IReadOnlyList<Pol.ReconcileHold> Holds { get; init; } = [];

    /// <summary>Names the blueprints and frames the room has standing (AwaitingKey); empty when none.</summary>
    public string Awaiting { get; init; } = "";

    /// <summary>Names the foreign things on cells the room waits to have cleared (RoomObstructions).</summary>
    public IReadOnlyList<string> Obstructed { get; init; } = [];
}

/// <summary>One building a room is built from on site.</summary>
internal sealed record RoomBuild
{
    public string Def { get; init; } = "";
    public string Stuff { get; init; } = "";
    public Cell Cell { get; init; }
    public Rotation Rot { get; init; }

    /// <summary>Marks a wall, fence, door or gate of the room's ring.</summary>
    public bool Ring { get; init; }

    /// <summary>Marks a planned vent standing in a ring wall: the ring's optional piece, so a refusal never stops the room's owner (#2962).</summary>
    public bool Vent { get; init; }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The first <paramref name="count"/> bytes of the SHA-256 of <paramref name="text"/>, as lowercase hex (Go's %x of digest[:n]).</summary>
    internal static string DigestHex(string text, int count) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, count).ToLowerInvariant();

    /// <summary>
    /// The ring-only diff input for room: the plan, the colony's walls and doors with natural rock counted as
    /// wall, and obstructions on the ring alone; false while the construction census is unknown. Stored goods
    /// and furniture inside the room do not obstruct its shell.
    /// </summary>
    internal static (Pol.ReconcileInput Input, bool Known) RoomRingInput(Observation.ColonyProjection facts, Pol.LayoutPlan plan, Pol.PlannedRoom room)
    {
        var (buildings, known) = ColonyGround(facts);
        if (!known)
        {
            return (new Pol.ReconcileInput(), false);
        }
        var cells = CellsOn(facts.Cells, room.RoomGround());
        // The cells open to the sky: the room's ground and its airlocks' vestibules.
        var unroofed = new HashSet<Cell>();
        var vestibules = new HashSet<Cell>();
        foreach (var child in plan.ChildrenOf(room.Id))
        {
            if (child.Role == Pol.PlannedRole.Airlock)
            {
                vestibules.Add(child.Vestibule());
            }
        }
        if (!plan.Roofed.Empty())
        {
            foreach (var c in facts.Cells)
            {
                if (c.Roofed.TryGetValue(out var roofed) && !roofed && vestibules.Contains(c.Cell))
                {
                    unroofed.Add(c.Cell);
                }
            }
            foreach (var c in cells)
            {
                if (c.Roofed.TryGetValue(out var roofed) && !roofed)
                {
                    unroofed.Add(c.Cell);
                }
            }
        }
        var inside = room.Region.Bounds();
        cells = [.. cells.Where(c => !(c.Cell.X >= inside.X && c.Cell.X < inside.X + inside.Width && c.Cell.Z >= inside.Z && c.Cell.Z < inside.Z + inside.Height))];
        var vent = facts.DefinitionAvailable(ClimateDefs().VentDef).TryGetValue(out var ventAvailable);
        return (new Pol.ReconcileInput
        {
            Plan = plan,
            Room = room,
            Ground = plan.GroundWithRock(buildings, NaturalRock(facts)),
            Rooms = ColonyRooms(facts),
            Cells = cells,
            VentAvailable = vent && ventAvailable,
            Unroofed = unroofed,
        }, true);
    }

    /// <summary>The mirror cells inside ground: the room's foreign things are read from them.</summary>
    internal static List<Pol.SiteCell> CellsOn(IEnumerable<Pol.SiteCell> cells, Pol.Rectangle ground) =>
        [.. cells.Where(c => c.Cell.X >= ground.X && c.Cell.X < ground.X + ground.Width && c.Cell.Z >= ground.Z && c.Cell.Z < ground.Z + ground.Height)];

    /// <summary>True while room's ring has a wall or door still to raise.</summary>
    internal static bool RoomRingOwed(Observation.ColonyProjection facts, Pol.LayoutPlan plan, Pol.PlannedRoom room)
    {
        var (input, known) = RoomRingInput(facts, plan, room);
        if (!known)
        {
            return false;
        }
        foreach (var op in Reconcile(input).Owed)
        {
            if (op.Kind == Pol.OpKind.WallIn || op.Kind == Pol.OpKind.DoorIn)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The flooring review's view of the colony's floors: every definition the mirror describes, the accessible stock and the tier style.</summary>
    internal static Pol.FlooringFacts FlooringFactsOf(Observation.ColonyProjection facts)
    {
        var definitions = new Dictionary<string, Pol.FloorDefinition>(StringComparer.Ordinal);
        var flooring = new Pol.FlooringFacts
        {
            Definitions = definitions,
            Stock = facts.Resources,
            Style = FloorStyle(facts),
            Layout = facts.LayoutPlan,
            Observation = facts.Facts.Upkeep.Flooring,
        };
        if (facts.Facts.Containment.Defs.TryGetValue(out var defs))
        {
            flooring = flooring with { ContainmentFloor = defs.Floor };
        }
        foreach (var d in facts.Definitions)
        {
            definitions[d.Name] = new Pol.FloorDefinition
            {
                Available = d.Available,
                Terrain = d.Terrain,
                Cleanliness = d.Cleanliness,
                Beauty = d.Beauty,
                Flammability = d.Flammability,
                PathCost = d.PathCost,
                Costs = d.Costs,
                WorkToBuild = d.WorkToBuild,
                Tags = d.FloorTags,
            };
        }
        return flooring;
    }

    /// <summary>The census cells that are natural rock.</summary>
    internal static List<Cell> NaturalRock(Observation.ColonyProjection facts) =>
        [.. facts.Cells.Where(c => c.NaturalRock()).Select(c => c.Cell)];

    /// <summary>The wait of a room whose next operation has no ready cell: a prerequisite is outstanding or the native preview refused the placements.</summary>
    internal static RoundsBuildingResult RoomWaiting(string name, params RefusedPlacement[] refused)
    {
        var subject = name + "_reconcile";
        if (refused.Length > 0)
        {
            subject += ":blocked:" + refused[0].Key();
        }
        return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.ExistingWork, subject) };
    }

    /// <summary>Names what the preview reported in the way.</summary>
    internal static string DescribeBlockers(IReadOnlyList<Pol.PlacementBlocker> blockers)
    {
        if (blockers.Count == 0)
        {
            return "unreported";
        }
        var parts = new List<string>(blockers.Count);
        foreach (var b in blockers)
        {
            var name = b.DefName;
            if (name == "")
            {
                name = b.Category;
            }
            if (b.Blueprint)
            {
                name += " blueprint";
            }
            else if (b.Frame)
            {
                name += " frame";
            }
            parts.Add(name);
        }
        return string.Join(", ", parts);
    }

    /// <summary>Records a refused furniture or ring cell in the service log and returns it for the wait key; a ring cell (wall, door) reads as one.</summary>
    internal static RefusedPlacement ReportRefused(CancellationToken ct, RoomBuild build, Pol.Preview v)
    {
        var r = new RefusedPlacement(build.Def, build.Cell, DescribeBlockers(v.Blockers));
        var kindWord = build.Ring ? "ring" : "furniture";
        TelemetryPackage.Event(ct, LogLevel.Information, "building-planner", "placement_refused", kindWord + " cell refused: " + r.Key() + " blocked by " + r.Blocker);
        return r;
    }

    /// <summary>Names the first held foreign thing for a wait key and logs every one: the blocker rides in the wait, not a new enum.</summary>
    internal static string HoldKey(CancellationToken ct, RoomWork w)
    {
        var first = "";
        foreach (var h in w.Holds)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{h.Def}@{h.Cell.X},{h.Cell.Z}:{h.Reason}");
            TelemetryPackage.Event(ct, LogLevel.Information, "building-planner", "foreign_held", "foreign thing held: " + key);
            if (first == "")
            {
                first = key;
            }
        }
        return first;
    }

    /// <summary>RoomWaiting with the first held foreign thing named.</summary>
    internal static RoundsBuildingResult WaitingHeld(CancellationToken ct, string name, RoomWork w, params RefusedPlacement[] refused)
    {
        var result = RoomWaiting(name, refused);
        var hold = HoldKey(ct, w);
        if (refused.Length > 0)
        {
            return result;
        }
        if (hold != "")
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.ExistingWork, name + "_reconcile:held:" + hold) };
        }
        if (w.Obstructed.Count > 0)
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.ExistingWork, name + "_reconcile:obstructed:" + w.Obstructed[0]) };
        }
        if (w.Awaiting != "")
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.ExistingWork, name + "_reconcile:" + w.Awaiting) };
        }
        return result;
    }

    /// <summary>The facing the plan sited the vent on cell with.</summary>
    internal static Rotation VentRotation(Pol.LayoutPlan plan, Cell cell)
    {
        foreach (var s in plan.LiveSites(Pol.NodeKind.Vent))
        {
            if (s.Origin == cell && s.Rotation != null)
            {
                return s.Rotation.Value;
            }
        }
        return Rotation.North;
    }

    /// <summary>The order a wave's buildings are admitted in: doors first, then walls, floors and furniture, the vents last.</summary>
    internal static readonly Pol.OpKind[] RoomBuildKinds = [Pol.OpKind.DoorIn, Pol.OpKind.WallIn, Pol.OpKind.WallUp, Pol.OpKind.FloorIn, Pol.OpKind.Build, Pol.OpKind.VentIn];

    /// <summary>The foreign-thing waves in the order they are committed.</summary>
    internal static readonly Pol.OpKind[] ObstructionKinds = [Pol.OpKind.Claim, Pol.OpKind.Cut, Pol.OpKind.HaulOut];

    /// <summary>Maps a foreign-thing target to an existing action: a claim, or a cover clearance in the cut-plant or haul mode.</summary>
    internal static DomainAction ObstructionAction(ActionId id, Pol.OpKind kind, Pol.ClearanceTarget t)
    {
        if (kind == Pol.OpKind.Claim)
        {
            return DomainAction.NewClaimBuildingAction(id, new ClaimBuilding(t.EntityId));
        }
        var mode = kind == Pol.OpKind.Cut ? CoverClearanceMode.CutPlant : CoverClearanceMode.Haul;
        return DomainAction.NewCoverClearanceAction(id, new CoverClearance(t.EntityId, t.DefName, mode, t.Minimum));
    }

    /// <summary>
    /// Chooses the wall's and the door's stuff from the one stuff the colony can raise a shell from; refusal
    /// names what is unavailable.
    /// </summary>
    internal static (string WallStuff, string DoorStuff, Verdict Refusal, bool Ok) ShellMaterials(Observation.ColonyProjection facts, string wall, string door, ShellStuff? choose)
    {
        var (wallDef, wok) = AnimalContainmentDefinition(facts.Definitions, wall);
        var (doorDef, dok) = AnimalContainmentDefinition(facts.Definitions, door);
        if (!wok || !dok)
        {
            return ("", "", FieldUnavailable("wall_door_definitions"), false);
        }
        var wak = wallDef.Available.TryGetValue(out var wa);
        var dak = doorDef.Available.TryGetValue(out var da);
        if (!wak || !dak || !wa || !da)
        {
            return ("", "", FieldUnavailable("wall_door_availability"), false);
        }
        var (wallStuff, doorStuff, refusal, ok) = choose is not null ? choose(wallDef, doorDef) : SharedShellStuff(facts, wallDef, doorDef);
        if (ok && UnstockedUntilKitAllowed(facts, wallStuff, doorStuff))
        {
            return ("", "", AwaitingPlan("start_kit", "forbidden"), false);
        }
        return (wallStuff, doorStuff, refusal, ok);
    }

    /// <summary>
    /// Whether a stuff the ring would be placed in has no accessible stock while the start kit is still
    /// forbidden: every start item begins forbidden, so the stock census reads empty until the supply planner
    /// allows the kit, and the stuff then falls to the best ranked one (stone blocks the map does not have). The
    /// ring waits for the kit instead of laying blueprints nothing can build (#2814).
    /// </summary>
    internal static bool UnstockedUntilKitAllowed(Observation.ColonyProjection facts, params string[] stuffs)
    {
        var (stock, known) = facts.Stock();
        if (!known)
        {
            return false;
        }
        if (!ForbiddenSupplies(facts.Facts.EventLoot).TryGetValue(out var forbidden) || !forbidden)
        {
            return false;
        }
        foreach (var stuff in stuffs)
        {
            if (stuff != "" && stock.GetValueOrDefault(Pol.Resource.Of(stuff)) <= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Drops the blockers that are a blueprint of def, the ring cell's own wanted piece, and reports whether any was dropped.</summary>
    internal static (List<Pol.PlacementBlocker> Remaining, bool Dropped) WithoutOwnBlueprint(IEnumerable<Pol.PlacementBlocker> blockers, string def)
    {
        var rest = new List<Pol.PlacementBlocker>();
        var dropped = false;
        foreach (var b in blockers)
        {
            if (b.Blueprint && (b.DefName.StartsWith("Blueprint_", StringComparison.Ordinal) ? b.DefName["Blueprint_".Length..] : b.DefName) == def)
            {
                dropped = true;
                continue;
            }
            rest.Add(b);
        }
        return (rest, dropped);
    }
}

public sealed partial class Rounder
{
    /// <summary>The clear side's wanted floor per planned room: the flooring review's choice, with the throne room's title tags.</summary>
    internal Pol.RoomFloors ClearFloors(Observation.ColonyProjection facts)
    {
        var throne = new Pol.PlannedRoom();
        IReadOnlyList<string> tags = [];
        var (need, owed) = ThroneNeed(facts);
        if (owed && need.FloorTags.Count > 0 && facts.LayoutPlan.TryGetValue(out var plan))
        {
            (throne, _) = plan.ThroneRoomFor(need.MinArea);
            tags = need.FloorTags;
        }
        return FlooringRoomFloors(room => room.Same(throne) ? tags : [], FlooringFactsOf(facts), Pol.Policy.Flooring);
    }
}

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Reconciles one owner's room: it is diffed, then the next wave is committed: foreign obstructions,
    /// removals, installs from packed stock, then the on-site builds (CommitBuilds).
    /// </summary>
    internal RoundsBuildingResult ReconcileRoom(CancellationToken call, CancellationToken epoch, ControlState state, Sto.Rounds review, Sto.StandardState goal, Observation.RoundsReading reading, PackedStock stock, RoomReconcile rr)
    {
        var facts = reading.Projection;
        if (!facts.LayoutPlan.TryGetValue(out var plan))
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("room_ground") };
        }
        Dictionary<string, int>? left = null;
        var (input, ground) = RoomRingInput(facts, plan, rr.Room);
        if (!ground)
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("room_ground") };
        }
        if (!rr.RingOnly)
        {
            input = input with { Cells = CellsOn(facts.Cells, rr.Room.RoomGround()) };
            if (Native is not Observation.IClearanceSource source)
            {
                return new RoundsBuildingResult { Verdict = FieldUnavailable("room_ground") };
            }
            var read = ObservationPackage.ObserveClearanceCensusOnGround(call, source, facts.Identity, false, [rr.Room.RoomGround()]);
            if (!read.TryGetValue(out var census))
            {
                return new RoundsBuildingResult { Verdict = FieldUnavailable("room_ground") };
            }
            var (_, player) = SplitGroundRows(census.Targets);
            input = input with { Rows = OwnRows(StampPacking(player, facts), rr.Template, rr.Forbidden), Floors = census.Floors, Furniture = rr.Template };
            var want = ShellStyleOf(facts).WallStuff;
            if (want != "" && !rr.Room.Outdoor)
            {
                input = input with { WallUpgrade = have => facts.StuffUpgrade(ShellWallDefinition, have, want) };
            }
            if (!rr.Room.Outdoor)
            {
                var (wanted, kept) = FlooringRoomFloors(_ => rr.Tags, FlooringFactsOf(facts), Reviewer.Policy.Flooring)(rr.Room);
                input = input with { WantedFloor = wanted, FloorKept = kept };
            }
            var (items, readable) = stock.Items(call, PackedFurnitureDefinition);
            if (readable)
            {
                left = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    left[item.InnerDef] = left.GetValueOrDefault(item.InnerDef) + 1;
                }
            }
            input = input with { Stock = left };
        }
        var (ops, holds) = ReconcileRoomHolds(input);
        if (rr.ClimateOnly)
        {
            ops = EnvelopeOps(ops);
            holds = [];
        }
        IReadOnlyList<Pol.ConstructionSite> standing = [];
        if (facts.Facts.CurrentConstruction.TryGetValue(out var census2))
        {
            standing = RoomSites(census2.Sites, rr.Room.RoomGround());
        }
        var obstructed = RoomObstructions(ops);
        Reviewer.NoteStall(call, rr.Name, facts.Identity.Tick, standing, obstructed);
        var w = new RoomWork { Rr = rr, Ops = ops, Holds = holds, Awaiting = AwaitingKey(standing), Obstructed = obstructed };
        // Foreign obstructions first (claim, cut, haul), then removals, then installs from stock, then what is built on site.
        var (obstruction, done) = CommitObstructions(call, epoch, state, goal, w);
        if (done)
        {
            return obstruction;
        }
        foreach (var kind in new[] { Pol.OpKind.FurnitureOut, Pol.OpKind.Pack, Pol.OpKind.PackInUse, Pol.OpKind.FloorOut })
        {
            foreach (var op in ops)
            {
                if (op.Kind == kind)
                {
                    return CommitRemoval(call, epoch, state, goal, rr, op);
                }
            }
        }
        var installs = new List<Pol.WantedPiece>();
        foreach (var op in ops)
        {
            if (op.Kind == Pol.OpKind.Install)
            {
                installs.AddRange(op.Pieces);
            }
        }
        if (installs.Count > 0)
        {
            var (installed, installDone) = CommitInstalls(call, epoch, state, goal, stock, rr, installs);
            if (installDone)
            {
                return installed;
            }
        }
        var (roof, roofDone) = CommitRoof(call, epoch, state, goal, w);
        if (roofDone)
        {
            return roof;
        }
        return CommitBuilds(call, epoch, state, review, goal, reading, plan, w);
    }

    /// <summary>
    /// Designates the Build Roof area over the cells of the plan's roofed set the room owes once its ring
    /// stands (OpRoofIn), one method per cell set so a designation pawns have not finished is not repeated
    /// (#2962). done is false when nothing is owed or the set was designated before.
    /// </summary>
    internal (RoundsBuildingResult Result, bool Done) CommitRoof(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState goal, RoomWork w)
    {
        IReadOnlyList<Cell> cells = [];
        foreach (var op in w.Ops)
        {
            if (op.Kind == Pol.OpKind.RoofIn)
            {
                cells = op.Cells;
            }
        }
        if (cells.Count == 0)
        {
            return (new RoundsBuildingResult(), false);
        }
        var roof = new BuildRoof(cells);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewBuildRoofAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-0")), roof);
        // Go prints the cell slice with fmt.Sprint: [{x z} {x z}].
        var printed = "[" + string.Join(" ", roof.Cells.Select(c => string.Create(CultureInfo.InvariantCulture, $"{{{c.X} {c.Z}}}"))) + "]";
        var method = new MethodId($"{w.Rr.Name}-roof-{DigestHex(printed, 8)}");
        if (!MethodOnce(call, goal, method))
        {
            return (new RoundsBuildingResult(), false);
        }
        var result = CommitOwnerActions(call, epoch, state, goal, method, id, [action]);
        return (result, true);
    }

    /// <summary>True when the owner has not committed method yet.</summary>
    internal bool MethodOnce(CancellationToken call, Sto.StandardState goal, MethodId method)
    {
        try
        {
            Reviewer.Player.Journal.LoadOwnerMethod(call, goal, method);
            return false;
        }
        catch (Sto.NotFoundException)
        {
            return true;
        }
    }

    /// <summary>Commits actions as one owner method.</summary>
    internal RoundsBuildingResult CommitOwnerActions(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState goal, MethodId method, PlanId id, IReadOnlyList<DomainAction> actions)
    {
        var p = Reviewer.Player;
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("commitOwnerActions: p.session.State() != state");
        }
        p.Journal.CommitOwnerMethod(call, goal, method, "", plan);
        return new RoundsBuildingResult { Verdict = BuildingReasonAdmitted };
    }

    /// <summary>
    /// Takes down what the room holds that it should not: packable buildings are packed (uninstalled), the
    /// rest deconstructed one at a time, the wrong floors removed, one method per wave.
    /// </summary>
    internal RoundsBuildingResult CommitRemoval(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState goal, RoomReconcile rr, Pol.Operation op)
    {
        var step = new Pol.GroundStep { Phase = op.Label, Targets = op.Targets, Floors = op.Floors };
        if (op.Kind == Pol.OpKind.FurnitureOut)
        {
            step = step with { Targets = [.. step.Targets.Take(1)] };
        }
        var id = PlanIds.MintPlanId();
        var (prefix, actions) = GroundStepMethod(id, step);
        var method = new MethodId(rr.Name + "-" + prefix.TrimEnd('-'));
        if (!MethodOnce(call, goal, method))
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, rr.Name + "_removal") };
        }
        return CommitOwnerActions(call, epoch, state, goal, method, id, actions);
    }

    /// <summary>
    /// Commits the next foreign-thing wave of the room: ruins claimed as wall, impassable plants cut and
    /// haulable items moved, one method per kind whose name carries the targets, so an order the game has not
    /// finished is not repeated. Packing and deconstruction of foreign buildings go through CommitRemoval like
    /// the room's own. A wave already committed is skipped, not waited on: the cells it covers are blocked in
    /// the diff, so the rest of the room proceeds. done is false when nothing was committed.
    /// </summary>
    internal (RoundsBuildingResult Result, bool Done) CommitObstructions(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState goal, RoomWork w)
    {
        foreach (var kind in ObstructionKinds)
        {
            var targets = new List<Pol.ClearanceTarget>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var op in w.Ops)
            {
                if (op.Kind != kind)
                {
                    continue;
                }
                foreach (var t in op.Targets)
                {
                    var key = string.Create(CultureInfo.InvariantCulture, $"{t.EntityId}@{t.Minimum.X},{t.Minimum.Z}");
                    if (seen.Add(key))
                    {
                        targets.Add(t);
                    }
                }
            }
            if (targets.Count == 0)
            {
                continue;
            }
            var (result, done) = CommitObstructionWave(call, epoch, state, goal, w.Rr.Name, kind, targets);
            if (done)
            {
                return (result, true);
            }
        }
        return (new RoundsBuildingResult(), false);
    }

    /// <summary>Commits one wave of kind over targets as an owner method named for them; done is false when that wave was committed before.</summary>
    internal (RoundsBuildingResult Result, bool Done) CommitObstructionWave(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState goal, string name, Pol.OpKind kind, IReadOnlyList<Pol.ClearanceTarget> targets)
    {
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        var key = new StringBuilder();
        foreach (var t in targets)
        {
            actions.Add(ObstructionAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), kind, t));
            key.Append(CultureInfo.InvariantCulture, $"{t.EntityId}@{t.Minimum.X},{t.Minimum.Z};");
        }
        var method = new MethodId($"{name}-{Wire.Name(kind)}-{DigestHex(key.ToString(), 8)}");
        if (!MethodOnce(call, goal, method))
        {
            return (new RoundsBuildingResult(), false);
        }
        var result = CommitOwnerActions(call, epoch, state, goal, method, id, actions);
        return (result, true);
    }

    /// <summary>Moves the pieces the packed stock holds to their slots; done is false when none is stored after all, so the pieces are built on site.</summary>
    internal (RoundsBuildingResult Result, bool Done) CommitInstalls(CancellationToken call, CancellationToken epoch, ControlState state, Sto.StandardState goal, PackedStock stock, RoomReconcile rr, IReadOnlyList<Pol.WantedPiece> pieces)
    {
        var actions = new List<DomainAction>();
        var key = new StringBuilder();
        var id = PlanIds.MintPlanId();
        foreach (var piece in pieces)
        {
            var (move, stored) = stock.Install(call, PackedFurnitureDefinition, piece.DefName, piece.Anchor(), piece.Rot);
            if (!stored)
            {
                continue;
            }
            actions.Add(DomainAction.NewMoveBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), move));
            key.Append(CultureInfo.InvariantCulture, $"{move.Thing}@{piece.Anchor().X},{piece.Anchor().Z};");
        }
        if (actions.Count == 0)
        {
            return (new RoundsBuildingResult(), false);
        }
        var method = new MethodId($"{rr.Name}-install-{DigestHex(key.ToString(), 8)}");
        if (!MethodOnce(call, goal, method))
        {
            return (new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, rr.Name + "_install") }, true);
        }
        var result = CommitOwnerActions(call, epoch, state, goal, method, id, actions);
        return (result, true);
    }

    /// <summary>
    /// Previews every ready on-site building of the room, one kind at a time, and admits those native accepts
    /// as one method; a refused placement is left for a later pass. There is no cap on cells and no funding
    /// limit: the native tier gate orders material delivery.
    /// </summary>
    internal RoundsBuildingResult CommitBuilds(CancellationToken call, CancellationToken epoch, ControlState state, Sto.Rounds review, Sto.StandardState goal, Observation.RoundsReading reading, Pol.LayoutPlan plan, RoomWork w)
    {
        var facts = reading.Projection;
        var rr = w.Rr;
        var wantWalls = false;
        foreach (var op in w.Ops)
        {
            wantWalls = wantWalls || op.Kind == Pol.OpKind.WallIn || op.Kind == Pol.OpKind.WallUp || op.Kind == Pol.OpKind.DoorIn;
        }
        var builds = new List<RoomBuild>();
        var (wallDef, doorDef) = rr.Room.RingDefs();
        string wallStuff = "", doorStuff = "";
        if (wantWalls)
        {
            var (ws, ds, refusal, ok) = ShellMaterials(facts, wallDef, doorDef, rr.Stuff);
            if (!ok)
            {
                return new RoundsBuildingResult { Verdict = refusal };
            }
            wallStuff = ws;
            doorStuff = ds;
        }
        foreach (var kind in RoomBuildKinds)
        {
            foreach (var op in w.Ops)
            {
                if (op.Kind != kind)
                {
                    continue;
                }
                switch (kind)
                {
                    case Pol.OpKind.DoorIn:
                        var flaps = plan.FlapCells(rr.Room);
                        foreach (var c in op.Cells)
                        {
                            if (flaps.Contains(c))
                            {
                                // The shared wall of a pen and its barn takes the animal flap.
                                var flap = facts.Shapes.Furniture.AnimalFlap;
                                if (flap == "")
                                {
                                    throw new ControlException("commitBuilds: the catalog names no animal flap");
                                }
                                builds.Add(new RoomBuild { Def = flap, Stuff = facts.BuildStuff(flap), Cell = c, Rot = Rotation.North, Ring = true });
                                continue;
                            }
                            builds.Add(new RoomBuild { Def = doorDef, Stuff = doorStuff, Cell = c, Rot = Rotation.North, Ring = true });
                        }
                        break;
                    case Pol.OpKind.WallIn or Pol.OpKind.WallUp:
                        foreach (var c in op.Cells)
                        {
                            builds.Add(new RoomBuild { Def = wallDef, Stuff = wallStuff, Cell = c, Rot = Rotation.North, Ring = true });
                        }
                        break;
                    case Pol.OpKind.FloorIn:
                        foreach (var f in op.Floors)
                        {
                            builds.Add(new RoomBuild { Def = f.DefName, Cell = f.Cell, Rot = Rotation.North });
                        }
                        break;
                    case Pol.OpKind.Build:
                        foreach (var piece in op.Pieces)
                        {
                            builds.Add(new RoomBuild { Def = piece.DefName, Stuff = facts.BulkBuildStuff(piece.DefName, 1), Cell = piece.Anchor(), Rot = piece.Rot });
                        }
                        break;
                    case Pol.OpKind.VentIn:
                        foreach (var c in op.Cells)
                        {
                            builds.Add(new RoomBuild { Def = ClimateDefs().VentDef, Cell = c, Rot = VentRotation(plan, c), Ring = true, Vent = true });
                        }
                        break;
                }
            }
        }
        if (builds.Count == 0)
        {
            if (rr.RingOnly)
            {
                // Nothing of the ring is ready (a door awaits the clear side's wall removal): the owner goes on to its own placement.
                return new RoundsBuildingResult { Verdict = NoSpace("room_ring") };
            }
            return WaitingHeld(call, rr.Name, w);
        }
        var p = Reviewer.Player;
        var snapshot = state.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };
        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("commitBuilds: p.session.State() != state");
            }
        }
        // A room planned into rock is mined out before its ring.
        if (wantWalls && rr.Room.Dug)
        {
            var (dug, handled) = DigPlannedRoom(call, epoch, new ExcavationStep { State = state, Review = review, Owner = goal, Facts = facts, Read = reading.ColonyReading }, plan, rr.Room, Check);
            if (handled)
            {
                return dug;
            }
        }
        // Every ready building is admitted: a blueprint holds no materials until they are delivered, and the
        // native tier gate orders the delivery.
        var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var selected = new List<Pol.Preview>();
        var key = new StringBuilder();
        var previewed = 0;
        var refused = new List<RefusedPlacement>();
        foreach (var build in builds)
        {
            Check();
            var building = new Domain.Building(build.Def, build.Cell, build.Rot, build.Stuff);
            var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-{selected.Count}")), building, RoomTier(rr.Room.Role));
            var (preview, _) = ((IRoundsBuildingSource)Native).PreviewBuilding(call, action, snapshot);
            var v = preview.Preview;
            var ck = v.CanPlace.TryGetValue(out var can);
            var sk = v.SafeToPlace.TryGetValue(out var safe);
            if (!ck || !can || !sk || !safe)
            {
                if (build.Ring)
                {
                    var (rest, own) = WithoutOwnBlueprint(v.Blockers, build.Def);
                    if (own)
                    {
                        if (rest.Count == 0)
                        {
                            // Our own blueprint already stands on the cell: it is placed.
                            continue;
                        }
                        v = v with { Blockers = rest };
                    }
                }
                if (build.Ring && !build.Vent && (rr.Room.Outdoor || rr.RingOnly))
                {
                    // The planner sited this ring: a cell the native refuses is reported and left to the plan's replan, never moved to another site.
                    return new RoundsBuildingResult { Verdict = NoSpace("pen_enclosure") };
                }
                var r = ReportRefused(call, build, v);
                if (refused.Count == 0)
                {
                    refused.Add(r);
                }
                continue;
            }
            stock = MergeRoundsStock(stock, preview.Stock, previewed == 0);
            previewed++;
            selected.Add(v);
            key.Append(CultureInfo.InvariantCulture, $"{build.Def}@{build.Cell.X},{build.Cell.Z};");
        }
        if (selected.Count == 0)
        {
            if (rr.RingOnly && !builds.Any(b => !b.Vent))
            {
                // Only vents are owed and the native refused them: the owner goes on to its own placement.
                return new RoundsBuildingResult { Verdict = NoSpace("room_ring") };
            }
            return WaitingHeld(call, rr.Name, w, [.. refused]);
        }
        var method = new MethodId($"{rr.Name}-build-{DigestHex(key.ToString(), 8)}");
        if (!MethodOnce(call, goal, method))
        {
            return new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, rr.Name + "_build") };
        }
        return AdmitPreviews(call, epoch, new RoundsAdmission
        {
            State = state,
            Review = review,
            Owner = goal,
            Facts = facts,
            Method = method,
            Reason = rr.Reason,
            Snapshot = snapshot,
            Selected = selected,
            Stock = stock,
            Purpose = Pol.Purpose.Shelter,
        });
    }
}
