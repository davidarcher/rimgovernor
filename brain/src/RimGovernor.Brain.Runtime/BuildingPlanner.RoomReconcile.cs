using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

// The build side of the reconciler as a shared library: an owning concern calls ReconcileRoom for its own room
// and the reconciler decides how. It reads the room's ground (clearance census on the room's rectangle, the
// packed stock), asks the flooring review for the wanted floor, diffs (Pol ReconcileRoom) and commits the next
// wave: removals first (pack, deconstruct, floor out), then installs from packed stock, then everything built
// on site (doors, walls, floors, furniture), each wave one method. A placement the native preview refuses is
// not ready this pass: the room waits. The ring's removals (door and wall out, roof off) are the plan-wide
// clear side's.

/// <summary>Chooses the wall's and the door's stuff for a ring; refusal names what is unavailable.</summary>
internal delegate (string WallStuff, string DoorStuff, Verdict Refusal, bool Ok) ShellStuff(RimGovernor.Brain.Policy.PlanningDefinition wall, RimGovernor.Brain.Policy.PlanningDefinition door);

/// <summary>
/// One owner's room: the PlannedRoom, its furniture template and what the owner forbids in it. Name prefixes
/// the methods, Reason is the admission's short why and Tags the title's floor tags (the throne room's).
/// </summary>
internal sealed record RoomReconcile
{
    public Pol.PlannedRoom Room { get; init; } = new();
    public IReadOnlyList<Pol.WantedPiece> Template { get; init; } = [];
    public Func<string, bool>? Forbidden { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Name { get; init; } = "";
    public string Reason { get; init; } = "";

    /// <summary>
    /// Reconciles the ring and its doors alone: no furniture template, floors or wall-stuff swaps, and no
    /// clearance read, so a caller that holds no packed stock (dining, kitchen, the gear rooms) can use it.
    /// A ring cell the native refuses is no_space, as for an outdoor ring. A RingOnly room reads no census, so
    /// it never removes stale furniture: the facility rooms (dining, recreation, lab, workshop, kitchen,
    /// butchery) are explicitly exempt from stale removal (#3487) and FacilityWants is not their template.
    /// </summary>
    public bool RingOnly { get; init; }

    /// <summary>
    /// Narrows a RingOnly reconcile to the room's planned vents and roofs (EnvelopeOps): the plan-wide
    /// climate envelope's call, which owns nothing else of the room (#2962).
    /// </summary>
    public bool ClimateOnly { get; init; }

    /// <summary>Overrides the wall's and door's stuff; null builds both from the ring's base stuff (ShellMaterials).</summary>
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

partial class BuildingPlanner
{
    /// <summary>The first <paramref name="count"/> bytes of the SHA-256 of <paramref name="text"/>, as lowercase hex.</summary>
    internal static string DigestHex(string text, int count) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, count).ToLowerInvariant();

    /// <summary>
    /// The ring-only diff input for room: the plan, the colony's walls and doors with natural rock counted as
    /// wall, and obstructions on the ring alone. Stored goods
    /// and furniture inside the room do not obstruct its shell.
    /// </summary>
    internal static Pol.ReconcileInput RoomRingInput(RimGovernor.Brain.GameAccess.ColonyReading facts, Pol.LayoutPlan plan, Pol.PlannedRoom room)
    {
        var buildings = ColonyGround(facts);
        var cells = CellsOn(facts.PlanningWindow.Cells, room.RoomGround());
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
            foreach (var c in facts.PlanningWindow.Cells)
            {
                if (!c.Roofed && vestibules.Contains(c.Cell))
                {
                    unroofed.Add(c.Cell);
                }
            }
            foreach (var c in cells)
            {
                if (!c.Roofed)
                {
                    unroofed.Add(c.Cell);
                }
            }
        }
        var inside = room.Region.Bounds();
        cells = [.. cells.Where(c => !(c.Cell.X >= inside.X && c.Cell.X < inside.X + inside.Width && c.Cell.Z >= inside.Z && c.Cell.Z < inside.Z + inside.Height))];
        var ventAvailable = ColonyPlanningRules.DefinitionAvailable(facts.Facts, ClimateRules.ClimateDefs().VentDef);
        return new Pol.ReconcileInput
        {
            Plan = plan,
            Room = room,
            Ground = plan.GroundWithRock(buildings, NaturalRock(facts)),
            Rooms = facts.Facts.Upkeep.Rooms,
            Cells = cells,
            VentAvailable = ventAvailable,
            Unroofed = unroofed,
        };
    }

    /// <summary>
    /// Whether room is started (PlannedRoomStarted, #3278) against the colony's construction census: its own ring
    /// built, a blueprint or frame on its ground, or a template piece at its anchor.
    /// </summary>
    internal static bool PlannedRoomStartedOf(RimGovernor.Brain.GameAccess.ColonyReading facts, Pol.LayoutPlan plan, Pol.PlannedRoom room)
    {
        var ground = ColonyGround(facts);
        var census = facts.Facts.CurrentConstruction;
        var sites = census.Sites.Where(s => s.Building is not null).Select(s => s.Building!.Cell);
        var standing = census.Buildings.Where(b => b.Building is not null).Select(b => (b.Building!.Definition, b.Building.Cell)).ToHashSet();
        return PlannedRules.PlannedRoomStarted(plan, room, ground, sites, standing);
    }

    /// <summary>The AwaitingPlan subject of an unstarted ringed room the portfolio assessed and did not select (#3278).</summary>
    internal const string PortfolioHeld = "portfolio_selection";

    /// <summary>The AwaitingPlan subject of an unstarted ringed room no comparison the record keeps assessed (#3754).</summary>
    internal const string PortfolioUnassessed = "portfolio_unassessed";

    /// <summary>
    /// The owed template pieces no builder can work now (#3754, PlannedRoomDone; <see cref="DefinitionBlockedOf"/>).
    /// </summary>
    internal static Func<Pol.WantedPiece, bool> PieceBlockedOf(ReviewReading reading)
    {
        var blocked = DefinitionBlockedOf(reading.Reading);
        return piece => blocked(piece.DefName);
    }

    /// <summary>The one blocked predicate (#3754, #3827): a definition <see cref="PieceReadinessOf"/> reads Blocked.</summary>
    internal static Func<string, bool> DefinitionBlockedOf(RimGovernor.Brain.GameAccess.ColonyReading projection) =>
        name => PieceReadinessOf(projection)(name) == DefinitionReadiness.Blocked;

    /// <summary>
    /// What the facts say of building a template piece of a definition now (#3754, #3827, #3839), on read evidence only
    /// (#3819). Blocked when its definition is read unavailable or its research is read with a prerequisite unfinished
    /// (DefinitionReadinessOf); when its stuff options, the stock and the map's sources are read and the one stuff chooser
    /// (PlannedRingStuffsRead) finds none the colony stocks or the map offers as wood or rock blocks; or when a material of
    /// its cost list other than its stuff (a stuff-less stove's Steel, a research bench's Steel beside its stuff) is read
    /// unstocked and read unobtainable (ColonyReview.PieceCostReadiness). Else Unread while any of those facts is unread, and
    /// Available.
    /// </summary>
    internal static Func<string, DefinitionReadiness> PieceReadinessOf(RimGovernor.Brain.GameAccess.ColonyReading projection) => name =>
    {
        var (stuffs, stuffRead) = ColonyReview.PlannedRingStuffsRead(projection, name, "");
        DefinitionReadiness[] halves =
        [
            DefenseLayoutPlanner.DefinitionReadinessOf(projection, name),
            stuffs.Count > 0 ? DefinitionReadiness.Available : stuffRead ? DefinitionReadiness.Blocked : DefinitionReadiness.Unread,
            ColonyReview.PieceCostReadiness(projection, name),
        ];
        return halves.Contains(DefinitionReadiness.Blocked) ? DefinitionReadiness.Blocked
            : halves.Contains(DefinitionReadiness.Unread) ? DefinitionReadiness.Unread : DefinitionReadiness.Available;
    };

    /// <summary>
    /// The owed items of the coupled comparison's planned rooms that are blocked pieces (#3827), resolved here at the read
    /// boundary so the service-day projection reads plain ids (ForecastCoupledContext.BlockedItems): each placed site a
    /// room holds (ForecastCoupledContext.RoomItems, its definition the construction read's) other than a ring's, whose
    /// definition <see cref="DefinitionBlockedOf"/> blocks. A planned site of a blocked piece is never generated: its
    /// room's alternatives leave it out (PlannedBuildSite.Blocked, #3838).
    /// </summary>
    internal static IReadOnlySet<string> BlockedRoomItems(RimGovernor.Brain.GameAccess.ColonyReading projection, Pol.ForecastCoupledContext context)
    {
        var blocked = DefinitionBlockedOf(projection);
        var ring = context.Closures.SelectMany(c => c.Placed).Select(p => p.Site).ToHashSet(StringComparer.Ordinal);
        var placed = (context.Construction?.Options ?? default).GroupBy(o => o.Site, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Definition, StringComparer.Ordinal);
        var items = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in context.RoomItems.Where(i => !ring.Contains(i.Item)))
        {
            if (placed.TryGetValue(item.Item, out var definition) && blocked(definition))
            {
                items.Add(item.Item);
            }
        }
        return items;
    }

    /// <summary>The mirror cells inside ground: the room's foreign things are read from them.</summary>
    internal static List<Pol.SiteCell> CellsOn(IEnumerable<Pol.SiteCell> cells, Pol.Rectangle ground) =>
        [.. cells.Where(c => c.Cell.X >= ground.X && c.Cell.X < ground.X + ground.Width && c.Cell.Z >= ground.Z && c.Cell.Z < ground.Z + ground.Height)];

    /// <summary>True while room's ring has a wall or door still to raise.</summary>
    internal static bool RoomRingOwed(RimGovernor.Brain.GameAccess.ColonyReading facts, Pol.LayoutPlan plan, Pol.PlannedRoom room)
    {
        var input = RoomRingInput(facts, plan, room);
        foreach (var op in ReconcileRules.Reconcile(input).Owed)
        {
            if (op.Kind == Pol.OpKind.WallIn || op.Kind == Pol.OpKind.DoorIn)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The flooring review's view of the colony's floors: every definition the mirror describes, the accessible stock and the tier style.</summary>
    internal static Pol.FlooringFacts FlooringFactsOf(RimGovernor.Brain.GameAccess.ColonyReading facts)
    {
        var definitions = new Dictionary<string, Pol.FloorDefinition>(StringComparer.Ordinal);
        var flooring = new Pol.FlooringFacts
        {
            Definitions = definitions,
            Stock = ResourceStockRules.Count(facts.Snapshot.Resources),
            Style = TierStyle.FloorStyle(facts),
            Layout = facts.Facts.LayoutPlan,
            Observation = facts.Facts.Upkeep.Flooring,
        };
        if (facts.Facts.Containment.Defs is ContainmentDefsRead.Read { Defs: var defs })
        {
            flooring = flooring with { ContainmentFloor = defs.Floor };
        }
        foreach (var d in facts.Facts.Definitions.Where(d => d.Floor is not null))
        {
            definitions[d.Name] = new Pol.FloorDefinition
            {
                Available = d.Available,
                Terrain = d.Terrain,
                Cleanliness = (d.Floor ?? throw new RequiredFactException("planning Floor")).Cleanliness,
                Beauty = (d.Floor ?? throw new RequiredFactException("planning Floor")).Beauty,
                Flammability = (d.Floor ?? throw new RequiredFactException("planning Floor")).Flammability,
                PathCost = d.PathCost,
                Costs = (d.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).Costs,
                WorkToBuild = (d.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).WorkToBuild,
                Tags = (d.Floor ?? throw new RequiredFactException("planning Floor")).FloorTags,
            };
        }
        return flooring;
    }

    /// <summary>The census cells that are natural rock.</summary>
    internal static List<Cell> NaturalRock(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        [.. facts.PlanningWindow.Cells.Where(c => c.NaturalRock()).Select(c => c.Cell)];

    /// <summary>The wait of a room whose next operation has no ready cell: a prerequisite is outstanding or the native preview refused the placements.</summary>
    internal static BuildingResult RoomWaiting(string name, params RefusedPlacement[] refused)
    {
        var subject = name + "_reconcile";
        if (refused.Length > 0)
        {
            subject += ":blocked:" + refused[0].Key();
        }
        return new BuildingResult { Verdict = WaitFor(Pol.Cause.ExistingWork, subject) };
    }

    /// <summary>Names what the preview reported in the way.</summary>
    internal static string DescribeBlockers(IReadOnlyList<RimGovernor.Brain.GameFacts.Placement.PlacementBlocker> blockers)
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
            if (b.IsBlueprint)
            {
                name += " blueprint";
            }
            else if (b.IsFrame)
            {
                name += " frame";
            }
            parts.Add(name);
        }
        return string.Join(", ", parts);
    }

    /// <summary>Records a refused furniture or ring cell in the service log and returns it for the wait key; a ring cell (wall, door) reads as one.</summary>
    internal static RefusedPlacement ReportRefused(RoomBuild build, IReadOnlyList<RimGovernor.Brain.GameFacts.Placement.PlacementBlocker> blockers, CancellationToken ct)
    {
        var r = new RefusedPlacement(build.Def, build.Cell, DescribeBlockers(blockers));
        var kindWord = build.Ring ? "ring" : "furniture";
        Tel.Event( TelemetryLevel.Info, "building-planner", "placement_refused", kindWord + " cell refused: " + r.Key() + " blocked by " + r.Blocker);
        return r;
    }

    /// <summary>Names the first held foreign thing for a wait key and logs every one: the blocker rides in the wait, not a new enum.</summary>
    internal static string HoldKey(RoomWork w, CancellationToken ct)
    {
        var first = "";
        foreach (var h in w.Holds)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{h.Def}@{h.Cell.X},{h.Cell.Z}:{h.Reason}");
            Tel.Event( TelemetryLevel.Info, "building-planner", "foreign_held", "foreign thing held: " + key);
            if (first == "")
            {
                first = key;
            }
        }
        return first;
    }

    /// <summary>RoomWaiting with the first held foreign thing named.</summary>
    internal static BuildingResult WaitingHeld(string name, RoomWork w, CancellationToken ct) => WaitingHeld(name, w, [], ct);

    internal static BuildingResult WaitingHeld(string name, RoomWork w, IReadOnlyList<RefusedPlacement> refused, CancellationToken ct)
    {
        var result = RoomWaiting(name, [.. refused]);
        var hold = HoldKey(w, ct);
        if (refused.Count > 0)
        {
            return result;
        }
        if (hold != "")
        {
            return new BuildingResult { Verdict = WaitFor(Pol.Cause.ExistingWork, name + "_reconcile:held:" + hold) };
        }
        if (w.Obstructed.Count > 0)
        {
            return new BuildingResult { Verdict = WaitFor(Pol.Cause.ExistingWork, name + "_reconcile:obstructed:" + w.Obstructed[0]) };
        }
        if (w.Awaiting != "")
        {
            return new BuildingResult { Verdict = WaitFor(Pol.Cause.ExistingWork, name + "_reconcile:" + w.Awaiting) };
        }
        return result;
    }

    /// <summary>The facing the plan sited the vent on cell with.</summary>
    internal static Rotation VentRotation(Pol.LayoutPlan plan, Cell cell)
    {
        foreach (var s in plan.LiveSites(Pol.NodeKind.Vent))
        {
            if (s.Origin == cell && s.Rotation is Pol.Heading.Toward toward)
            {
                return toward.Side;
            }
        }
        return Rotation.North;
    }

    /// <summary>The order a wave's buildings are admitted in: doors first, then walls, floors and furniture, the vents last.</summary>
    internal static readonly Pol.OpKind[] RoomBuildKinds = [Pol.OpKind.DoorIn, Pol.OpKind.WallIn, Pol.OpKind.WallUp, Pol.OpKind.FloorIn, Pol.OpKind.Build, Pol.OpKind.VentIn];

    /// <summary>The foreign-thing waves in the order they are committed.</summary>
    internal static readonly Pol.OpKind[] ObstructionKinds = [Pol.OpKind.Claim, Pol.OpKind.Cut, Pol.OpKind.HaulOut];

    /// <summary>
    /// Maps a foreign-thing target to an existing action: a claim, or a cover clearance in the cut-plant or haul mode. A haul
    /// stands the Haul designation on a chunk and, on an ordinary stack the haul designator refuses, a haul-out removal with
    /// no designation (#3625); either is the Haul removal the forecast binds as a Deliver of the stack.
    /// </summary>
    internal static DomainAction ObstructionAction(ActionId id, Pol.OpKind kind, Pol.ClearanceTarget t)
    {
        if (kind == Pol.OpKind.Claim)
        {
            return DomainAction.NewClaimBuildingAction(id, new ClaimBuilding(t.EntityId));
        }
        var mode = kind == Pol.OpKind.Cut ? CoverClearanceDesignation.CutPlant : CoverClearanceDesignation.Haul;
        return DomainAction.NewCoverClearanceAction(id, new CoverClearance(t.EntityId, t.DefName, mode, t.Minimum));
    }

    /// <summary>
    /// Chooses the wall's and the door's stuff; refusal names what is unavailable. A ring no selection chose stuff for
    /// is placed in its base ring stuff (the first of PlannedRingStuffsOf: the cheapest stocked stuff the door can also
    /// be made from, else the map's wood or rock blocks, #3747), the stuff its planned sites are priced in (#3677).
    /// </summary>
    internal static (string WallStuff, string DoorStuff, Verdict Refusal, bool Ok) ShellMaterials(RimGovernor.Brain.GameAccess.ColonyReading facts, string wall, string door, ShellStuff? choose)
    {
        var wallDef = AnimalContainment.AnimalContainmentDefinition(facts.Facts.Definitions, wall);
        var doorDef = AnimalContainment.AnimalContainmentDefinition(facts.Facts.Definitions, door);
        if (wallDef is null || doorDef is null)
        {
            return ("", "", FieldUnavailable("wall_door_definitions"), false);
        }
        var wa = wallDef.Available;
        var da = doorDef.Available;
        if (!wa || !da)
        {
            return ("", "", FieldUnavailable("wall_door_availability"), false);
        }
        var (wallStuff, doorStuff, refusal, ok) = choose is not null ? choose(wallDef, doorDef) : BaseRingStuff(facts, wall, door);
        if (ok && UnstockedUntilKitAllowed(facts, wallStuff, doorStuff))
        {
            return ("", "", AwaitingPlan("start_kit", "forbidden"), false);
        }
        return (wallStuff, doorStuff, refusal, ok);
    }

    /// <summary>Builds both from the ring's base stuff (PlannedRingStuffsOf); refused when the wall and door share no stuff.</summary>
    private static (string WallStuff, string DoorStuff, Verdict Refusal, bool Ok) BaseRingStuff(RimGovernor.Brain.GameAccess.ColonyReading facts, string wall, string door) =>
        ColonyReview.PlannedRingStuffsOf(facts, wall, door) is [{ Length: > 0 } stuff, ..]
            ? (stuff, stuff, new Verdict(), true)
            : ("", "", FieldUnavailable("wall_door_stuff"), false);

    /// <summary>
    /// Whether a stuff the ring would be placed in has no accessible stock while the start kit is still
    /// forbidden: every start item begins forbidden, so the stock census reads empty until the supply planner
    /// allows the kit, and the stuff then falls to the best ranked one (stone blocks the map does not have). The
    /// ring waits for the kit instead of laying blueprints nothing can build (#2814).
    /// </summary>
    internal static bool UnstockedUntilKitAllowed(RimGovernor.Brain.GameAccess.ColonyReading facts, params string[] stuffs)
    {
        var stock = ResourceStockRules.Count(facts.Snapshot.Resources);
        if (!facts.Facts.EventLoot.Any(row => row.IsSafe && row.Forbidden))
        {
            return false;
        }
        foreach (var stuff in stuffs)
        {
            if (stuff != "" && stock.GetValueOrDefault(new Pol.Resource(stuff)) <= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Drops the blockers that are a blueprint of def, the ring cell's own wanted piece, and reports whether any was dropped.</summary>
    internal static (List<RimGovernor.Brain.GameFacts.Placement.PlacementBlocker> Remaining, bool Dropped) WithoutOwnBlueprint(IEnumerable<RimGovernor.Brain.GameFacts.Placement.PlacementBlocker> blockers, string def)
    {
        var rest = new List<RimGovernor.Brain.GameFacts.Placement.PlacementBlocker>();
        var dropped = false;
        foreach (var b in blockers)
        {
            if (b.IsBlueprint && (b.DefName.StartsWith("Blueprint_", StringComparison.Ordinal) ? b.DefName["Blueprint_".Length..] : b.DefName) == def)
            {
                dropped = true;
                continue;
            }
            rest.Add(b);
        }
        return (rest, dropped);
    }
}

public sealed partial class ColonyReview
{
    /// <summary>The clear side's wanted floor per planned room: the flooring review's choice, with the throne room's title tags.</summary>
    internal Pol.RoomFloors ClearFloors(RimGovernor.Brain.GameAccess.ColonyReading facts)
    {
        var throne = new Pol.PlannedRoom();
        IReadOnlyList<string> tags = [];
        var (need, owed) = SleepingUpkeepPlanner.ThroneNeed(facts);
        if (owed && need.FloorTags.Count > 0 && facts.Facts.LayoutPlan is { } plan)
        {
            (throne, _) = plan.ThroneRoomFor(need.MinArea);
            tags = need.FloorTags;
        }
        return WantedFloorRules.FlooringRoomFloors(room => room.Same(throne) ? tags : [], BuildingPlanner.FlooringFactsOf(facts), Policy.Flooring);
    }
}

public sealed partial class BuildingPlanner
{
    /// <summary>The definitions other lanes stand in owned rooms, which an owner never removes as stale (ReconcileRules.CarriedOver).</summary>
    internal Func<string, bool> CarriedOver(ColonyReading facts) =>
        ReconcileRules.CarriedOver(LightingDefinitions(), facts.Facts.RequireBasicComfort(), facts.Facts.Items);

    /// <summary>
    /// Reconciles one owner's room against its full furniture list: it is diffed, then the next wave is committed: foreign obstructions,
    /// removals, installs from packed stock, then the on-site builds (CommitBuilds).
    /// </summary>
    internal async Task<BuildingResult> ReconcileRoomAsync(ControlState state, Sto.ReviewState review, Sto.NeedOwner goal, ReviewReading reading, PackedStock? stock, RoomReconcile rr, CancellationToken call, CancellationToken epoch)
    {
        var facts = reading.Reading;
        if (facts.Facts.LayoutPlan is not { } plan)
        {
            return new BuildingResult { Verdict = FieldUnavailable("room_ground") };
        }
        Dictionary<string, int>? left = null;
        var input = RoomRingInput(facts, plan, rr.Room);
        // An unstarted ringed planned room opens its ring only as the portfolio selected it (#3278, #3754);
        // a room no comparison assessed waits for one.
        var record = Reviewer.RoomSelection.Get(review.Snapshot);
        var (gate, selected) = PlannedRules.PlannedRoomGateOf(record, rr.Room, PlannedRoomStartedOf(facts, plan, rr.Room) != false);
        if (gate == Pol.PlannedRoomGate.Hold)
        {
            return new BuildingResult { Verdict = AwaitingPlan(record.Assessed.Contains(rr.Room.Id) ? PortfolioHeld : PortfolioUnassessed, rr.Name) };
        }
        if (gate == Pol.PlannedRoomGate.Selected && PlannedRules.SelectedRingStuff(selected) is { } ring)
        {
            rr = rr with { Stuff = (_, _) => (ring.Wall, ring.Door, default, true) };
        }
        if (!rr.RingOnly)
        {
            input = input with { Cells = CellsOn(facts.PlanningWindow.Cells, rr.Room.RoomGround()) };
            if (Native is not IClearanceSource source)
            {
                return new BuildingResult { Verdict = FieldUnavailable("room_ground") };
            }
            var census = await source.ReadClearanceAsync(facts.Identity, false, [rr.Room.RoomGround()], call).ConfigureAwait(false);
            var (_, player) = ClearanceGroundRules.SplitGroundRows(ClearanceReads.Targets(census), facts.Facts.Preferences);
            var stamped = HomeUpkeepReader.StampPacking(player, facts);
            input = input with { Rows = ReconcileRules.OwnerRows(rr.Room, stamped, rr.Template, rr.Forbidden, CarriedOver(facts)), Floors = census.Floors, Furniture = rr.Template };
            // A wall stands in the stuff it was built in; it is swapped only to a priced upgrade the comparison selected (#3657).
            if (PlannedRules.SelectedWallUpgrade(Reviewer.RoomSelection.Get(review.Snapshot), rr.Room.Id) is { Length: > 0 } upgrade && !rr.Room.Outdoor)
            {
                // A swap is owed only on a ring with no other work, so no door is built beside it.
                input = input with { WallUpgrade = have => have != upgrade };
                rr = rr with { Stuff = (_, _) => (upgrade, upgrade, default, true) };
            }
            if (!rr.Room.Outdoor)
            {
                var (wanted, kept) = WantedFloorRules.FlooringRoomFloors(_ => rr.Tags, FlooringFactsOf(facts), Reviewer.Policy.Flooring)(rr.Room);
                input = input with { WantedFloor = wanted, FloorKept = kept };
            }
            var items = stock is null ? null : await stock.ItemsAsync(CoupleBedRules.PackedFurnitureDefinition, call).ConfigureAwait(false);
            if (items is not null)
            {
                left = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    var packed = item.Packed ?? throw new ObservationContractException("packed stock lacks its building");
                    left[packed.DefName] = left.GetValueOrDefault(packed.DefName) + 1;
                }
            }
            input = input with { Stock = left };
        }
        var (ops, holds, owed) = ReconcileRules.ReconcileRoomHolds(input);
        if (!rr.ClimateOnly)
        {
            // Done when the diff owes nothing a builder can do now (#3754); the climate envelope's call sees only vents and roofs.
            var finished = PlannedRules.PlannedRoomDone(owed, PieceBlockedOf(reading));
            Reviewer.DoneRooms.Update(review.Snapshot, rooms =>
            {
                var others = rooms.Where(id => id != rr.Room.Id);
                return EquatableArray.From((finished ? others.Append(rr.Room.Id) : others).Order());
            });
        }
        if (gate == Pol.PlannedRoomGate.Selected)
        {
            ops = PlannedRules.SelectedPlannedOps(ops, selected);
        }
        if (rr.ClimateOnly)
        {
            ops = ClimateEnvelopeRules.EnvelopeOps(ops);
            holds = [];
        }
        IReadOnlyList<Pol.ConstructionSite> standing = [];
        var census2 = facts.Facts.CurrentConstruction;
        standing = RoomStall.RoomSites(census2.Sites, rr.Room.RoomGround());
        var obstructed = RoomStall.RoomObstructions(ops);
        Reviewer.NoteStall(rr.Name, facts.Identity.Tick, standing, obstructed);
        var w = new RoomWork { Rr = rr, Ops = ops, Holds = holds, Awaiting = RoomStall.AwaitingKey(standing), Obstructed = obstructed };
        // Foreign obstructions first (claim, cut, haul), then removals, then installs from stock, then what is built on site.
        var (obstruction, done) = CommitObstructions(state, goal, w, call, epoch);
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
                    return CommitRemoval(state, goal, rr, op, call, epoch);
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
            var (installed, installDone) = await CommitInstallsAsync(state, goal, stock, rr, installs, call, epoch).ConfigureAwait(false);
            if (installDone)
            {
                return installed;
            }
        }
        var (roof, roofDone) = CommitRoof(state, goal, w, call, epoch);
        if (roofDone)
        {
            return roof;
        }
        return await CommitBuildsAsync(state, review, goal, reading, plan, w, call, epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// Designates the Build Roof area over the cells of the plan's roofed set the room owes once its ring
    /// stands (OpRoofIn), one method per cell set so a designation pawns have not finished is not repeated
    /// (#2962). done is false when nothing is owed or the set was designated before.
    /// </summary>
    internal (BuildingResult Result, bool Done) CommitRoof(ControlState state, Sto.NeedOwner goal, RoomWork w, CancellationToken call, CancellationToken epoch)
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
            return (new BuildingResult(), false);
        }
        var roof = new BuildRoof(cells);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewBuildRoofAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-0")), roof);
        // The cell list prints as [{x z} {x z}].
        var printed = "[" + string.Join(" ", roof.Cells.Select(c => string.Create(CultureInfo.InvariantCulture, $"{{{c.X} {c.Z}}}"))) + "]";
        var method = new MethodId($"{w.Rr.Name}-roof-{DigestHex(printed, 8)}");
        if (!MethodOnce(goal, method, call))
        {
            return (new BuildingResult(), false);
        }
        var result = CommitOwnerActions(state, goal, method, id, [action], call, epoch);
        return (result, true);
    }

    /// <summary>
    /// True when the owner may commit method now. A Standard: it has not committed it in its current episode. A Need
    /// (decision 19, #3301): it never did, or its latest attempt is released and what that attempt built is gone
    /// in <paramref name="product"/>, the family's construction census, so the same MethodId is admitted again as
    /// the next attempt. An open attempt, a product that stands, a census left unknown (the default) or an
    /// attempt that built nothing (a roof, a removal, a cancelled wave) holds it.
    /// </summary>
    internal bool MethodOnce(Sto.NeedOwner goal, MethodId method, CancellationToken call, Pol.CurrentConstruction? product = null) =>
        MethodOnce(Reviewer.Player.Journal, goal, method, call, product);

    /// <summary><see cref="MethodOnce(Sto.NeedOwner, MethodId, CancellationToken, Fact{Pol.CurrentConstruction})"/> against <paramref name="journal"/>, for a step without a building planner.</summary>
    internal static bool MethodOnce(JournalStore journal, Sto.NeedOwner goal, MethodId method, CancellationToken call, Pol.CurrentConstruction? product = null)
    {
        if (journal.OwnerMethodPlan(goal, method, call) is not { } prior)
        {
            return true;
        }
        if (goal is not Sto.NeedOwner)
        {
            return false;
        }
        var plan = journal.LoadPlan(prior, call);
        return !Sto.StorePackage.PlanWorkOpen(plan, product) && BuildingWorkRules.PlanProductGone(plan.Progress, product);
    }

    /// <summary>Commits actions as one owner method.</summary>
    internal BuildingResult CommitOwnerActions(ControlState state, Sto.NeedOwner goal, MethodId method, PlanId id, IReadOnlyList<DomainAction> actions, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("commitOwnerActions: p.session.State() != state");
        }
        return new BuildingResult { Verdict = OwnerCommitVerdict(p.Journal.CommitOwnerWork(goal, method, "", plan, call)) };
    }

    /// <summary>The verdict an owner commit files: admitted, open work for a continued MethodId, the held claim or the admission refusal.</summary>
    internal static Verdict OwnerCommitVerdict(Sto.WorkCommit commit) => Verdict.Committed(commit);

    /// <summary>
    /// Takes down what the room holds that it should not: packable buildings are packed (uninstalled), the
    /// rest deconstructed one at a time, the wrong floors removed, one method per wave.
    /// </summary>
    internal BuildingResult CommitRemoval(ControlState state, Sto.NeedOwner goal, RoomReconcile rr, Pol.Operation op, CancellationToken call, CancellationToken epoch)
    {
        var step = new Pol.GroundStep { Phase = op.Label, Targets = [.. op.Targets], Floors = [.. op.Floors] };
        if (op.Kind == Pol.OpKind.FurnitureOut)
        {
            step = step with { Targets = [.. step.Targets.Take(1)] };
        }
        var id = PlanIds.MintPlanId();
        var (prefix, actions) = HomeUpkeepReader.GroundStepMethod(id, step);
        var method = new MethodId(rr.Name + "-" + prefix.TrimEnd('-'));
        if (!MethodOnce(goal, method, call))
        {
            return new BuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, rr.Name + "_removal") };
        }
        return CommitOwnerActions(state, goal, method, id, actions, call, epoch);
    }

    /// <summary>
    /// Commits the next foreign-thing wave of the room: ruins claimed as wall, impassable plants cut and
    /// haulable items moved, one method per kind whose name carries the targets, so an order the game has not
    /// finished is not repeated. Packing and deconstruction of foreign buildings go through CommitRemoval like
    /// the room's own. A wave already committed is skipped, not waited on: the cells it covers are blocked in
    /// the diff, so the rest of the room proceeds. done is false when nothing was committed.
    /// </summary>
    internal (BuildingResult Result, bool Done) CommitObstructions(ControlState state, Sto.NeedOwner goal, RoomWork w, CancellationToken call, CancellationToken epoch)
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
            var (result, done) = CommitObstructionWave(state, goal, w.Rr.Name, kind, targets, call, epoch);
            if (done)
            {
                return (result, true);
            }
        }
        return (new BuildingResult(), false);
    }

    /// <summary>Commits one wave of kind over targets as an owner method named for them; done is false when that wave was committed before.</summary>
    internal (BuildingResult Result, bool Done) CommitObstructionWave(ControlState state, Sto.NeedOwner goal, string name, Pol.OpKind kind, IReadOnlyList<Pol.ClearanceTarget> targets, CancellationToken call, CancellationToken epoch)
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
        if (!MethodOnce(goal, method, call))
        {
            return (new BuildingResult(), false);
        }
        var result = CommitOwnerActions(state, goal, method, id, actions, call, epoch);
        return (result, true);
    }

    /// <summary>Moves the pieces the packed stock holds to their slots; done is false when none is stored after all, so the pieces are built on site.</summary>
    internal async Task<(BuildingResult Result, bool Done)> CommitInstallsAsync(ControlState state, Sto.NeedOwner goal, PackedStock? stock, RoomReconcile rr, IReadOnlyList<Pol.WantedPiece> pieces, CancellationToken call, CancellationToken epoch)
    {
        var actions = new List<DomainAction>();
        var key = new StringBuilder();
        var id = PlanIds.MintPlanId();
        foreach (var piece in pieces)
        {
            var move = stock is null ? null : await stock.InstallAsync(CoupleBedRules.PackedFurnitureDefinition, piece.DefName, piece.Anchor(), piece.Rot, call).ConfigureAwait(false);
            if (move is null)
            {
                continue;
            }
            actions.Add(DomainAction.NewMoveBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), move));
            key.Append(CultureInfo.InvariantCulture, $"{move.Thing}@{piece.Anchor().X},{piece.Anchor().Z};");
        }
        if (actions.Count == 0)
        {
            return (new BuildingResult(), false);
        }
        var method = new MethodId($"{rr.Name}-install-{DigestHex(key.ToString(), 8)}");
        if (!MethodOnce(goal, method, call))
        {
            return (new BuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, rr.Name + "_install") }, true);
        }
        var result = CommitOwnerActions(state, goal, method, id, actions, call, epoch);
        return (result, true);
    }

    /// <summary>
    /// Previews every ready on-site building of the room, one kind at a time, and admits those native accepts
    /// as one method; a refused placement is left for a later pass. There is no cap on cells and no funding
    /// limit: the native tier gate orders material delivery.
    /// </summary>
    internal async Task<BuildingResult> CommitBuildsAsync(ControlState state, Sto.ReviewState review, Sto.NeedOwner goal, ReviewReading reading, Pol.LayoutPlan plan, RoomWork w, CancellationToken call, CancellationToken epoch)
    {
        var facts = reading.Reading;
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
                return new BuildingResult { Verdict = refusal };
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
                        foreach (var c in op.Cells)
                        {
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
                            builds.Add(new RoomBuild { Def = piece.DefName, Stuff = piece.Stuff.Length > 0 ? piece.Stuff : ColonyPlanningRules.BulkBuildStuff(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), piece.DefName, 1), Cell = piece.Anchor(), Rot = piece.Rot });
                        }
                        break;
                    case Pol.OpKind.VentIn:
                        foreach (var c in op.Cells)
                        {
                            builds.Add(new RoomBuild { Def = ClimateRules.ClimateDefs().VentDef, Cell = c, Rot = VentRotation(plan, c), Ring = true, Vent = true });
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
                return new BuildingResult { Verdict = NoSpace("room_ring") };
            }
            return WaitingHeld(rr.Name, w, call);
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
            var (dug, handled) = await DigPlannedRoomAsync(new ExcavationStep { State = state, Review = review, Owner = goal, Facts = facts }, plan, rr.Room, Check, call, epoch).ConfigureAwait(false);
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
            var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Plan}-{selected.Count}")), building, ConstructionTierRules.RoomTier(rr.Room.Role));
            var preview = await ((IReviewBuildingSource)Native).PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
            var v = preview.Preview;
            if (!v.CanPlace || !v.SafeToPlace)
            {
                IReadOnlyList<RimGovernor.Brain.GameFacts.Placement.PlacementBlocker> blockers = v.Blockers;
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
                        blockers = rest;
                    }
                }
                if (build.Ring && !build.Vent && (rr.Room.Outdoor || rr.RingOnly))
                {
                    // The planner sited this ring: a cell the native refuses is reported and left to the plan's replan, never moved to another site.
                    return new BuildingResult { Verdict = NoSpace("pen_enclosure") };
                }
                var r = ReportRefused(build, blockers, call);
                if (refused.Count == 0)
                {
                    refused.Add(r);
                }
                continue;
            }
            stock = MergeReviewStock(stock, preview.Stock);
            previewed++;
            selected.Add(v);
            key.Append(CultureInfo.InvariantCulture, $"{build.Def}@{build.Cell.X},{build.Cell.Z};");
        }
        if (selected.Count == 0)
        {
            if (rr.RingOnly && !builds.Any(b => !b.Vent))
            {
                // Only vents are owed and the native refused them: the owner goes on to its own placement.
                return new BuildingResult { Verdict = NoSpace("room_ring") };
            }
            return WaitingHeld(rr.Name, w, [.. refused], call);
        }
        var method = new MethodId($"{rr.Name}-build-{DigestHex(key.ToString(), 8)}");
        if (!MethodOnce(goal, method, call, facts.Facts.CurrentConstruction))
        {
            return new BuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, rr.Name + "_build") };
        }
        return await AdmitPreviewsAsync(new ReviewAdmission
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
        }, call, epoch).ConfigureAwait(false);
    }
}
