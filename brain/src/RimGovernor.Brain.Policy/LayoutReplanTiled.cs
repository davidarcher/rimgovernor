namespace RimGovernor.Brain.Policy;

/// <summary>
/// One room of the replan diff. Room is the old plan's room (0 for a New one), Tiled the tiled plan's room (0 for a
/// Dropped one; its ids are the tiler's own, a kept built room carries its old id). ShortBy is the floor cells the
/// room's group needs beyond the standing rooms matched to it (GroupOutgrown); 0 when it has enough. The embedding
/// keeps a standing room where it stands and does not grow it, so a raised suite target or a bigger bedroom group
/// shows only here (#3004).
/// </summary>
public sealed record TileRoomDiff
{
    public int Room { get; init; }
    public int Tiled { get; init; }
    public PlannedRole Role { get; init; }
    public string Verdict { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool Built { get; init; }
    public int ShortBy { get; init; }
}

/// <summary>
/// A standing room with no node in the tiled plan. Reason is ReasonDiffOutdoor or ReasonDiffNoNativeRole
/// (BuiltRegions skips both unless the room is a room node's), ReasonDiffNotBlock (not a rectangle) or
/// ReasonDiffNoNode (a native role whose group had no block for it).
/// </summary>
public sealed record UnmatchedBuilt(int Room, PlannedRole Role, string Reason);

/// <summary>The tiled plan against the plan the colony held.</summary>
public sealed record ReplanTileDiff
{
    public IReadOnlyList<TileRoomDiff> Rooms { get; init; } = [];
    public IReadOnlyList<UnmatchedBuilt> Unmatched { get; init; } = [];

    /// <summary>The number of rooms with the verdict.</summary>
    public int Count(string verdict) => Rooms.Count(r => r.Verdict == verdict);
}

/// <summary>
/// Ports <c>layout_replan_tiled.go</c>: the tiled replan (#3002 to #3005, J1 to J4 of #2965). At a replan the station
/// chain reads the built rooms (Built, Pinned, the plan's RetiredGround); TileStations over ReplanTileFacts tiles them
/// and ReplanLayout takes the tiled plan as the next plan. DiffReplanTile says, room by room, what the tiled plan did
/// with the plan the colony held; it is the layout_replan_tiled row's telemetry.
/// </summary>
public static partial class PolicyFunctions
{
    /// <summary>Verdict of a room in the replan diff: the tiled plan holds the room where the old plan has it.</summary>
    public const string TileDiffSame = "same";

    /// <summary>Verdict: the tiled plan holds it elsewhere, or as the successor of a standing room.</summary>
    public const string TileDiffMoved = "moved";

    /// <summary>Verdict: the tiled plan holds a room the old plan lacks.</summary>
    public const string TileDiffNew = "new";

    /// <summary>Verdict: the old plan holds a room the tiled plan lacks.</summary>
    public const string TileDiffDropped = "dropped";

    public const string ReasonDiffKept = "tile_kept_built";
    public const string ReasonDiffSuccessor = "tile_successor";
    public const string ReasonDiffRetired = "tile_built_retired_by_old";
    public const string ReasonDiffSuperseded = "tile_superseded";
    public const string ReasonDiffNoNode = "tile_no_node";
    public const string ReasonDiffNotBlock = "tile_not_a_block";
    public const string ReasonDiffOutdoor = "tile_outdoor";
    public const string ReasonDiffNoNativeRole = "tile_no_native_role";
    public const string ReasonDiffSited = "tile_sited_again";

    /// <summary>A standing room the chain cannot pin, matched to the room of its role the tiler builds of its own (a rebuild).</summary>
    public const string ReasonDiffUnpinned = "tile_unpinned_built";

    public const string ReasonDiffNoOldRoom = "tile_no_old_room";

    /// <summary>
    /// The facts of a replan review (the saved plan, its Built and Pinned rooms), as the tiler reads them: the ground
    /// a fresh plan is tiled on, as DeriveLayoutPlan lays it, in place of the saved plan's core-free zones (the
    /// survey's zones with the core, less the geothermal enclosures' cells, and the geysers' enclosures reserved),
    /// and no saved site: the power sites are placed again by the chain, and the perimeter, the herd and the yard
    /// sites are laid over the tiled plan after it. f is not changed.
    /// </summary>
    public static StationFacts ReplanTileFacts(StationFacts f, MapSurvey s, IReadOnlyList<PowerGeyser> geysers)
    {
        var zones = Zone(s);
        var footprints = GeyserFootprints(geysers);
        return f with
        {
            Plan = f.Plan with
            {
                Zones = CoreWithout(zones, GeothermalCells(footprints)),
                Reservations = GeothermalReservations(zones, footprints),
                Sites = [],
            },
            Survey = s,
        };
    }

    /// <summary>
    /// The rooms the tiled replan holds where they stand (#3004, decision 3 of #2965): the rooms the colony holds
    /// (HeldRooms) and the pairs of a rebuild in flight. Nothing else is pinned: a standing room with no node in the
    /// new embedding is retirement-eligible, and one that has a node moves only at the embedder's switching price
    /// (#2877).
    /// </summary>
    public static HashSet<int> TilePinned(LayoutPlan plan, RoomGrowth growth)
    {
        var pins = SuccessorPinned(plan);
        pins.UnionWith(HeldRooms(plan, growth));
        return pins;
    }

    /// <summary>
    /// The rooms the colony holds: the rooms an open journal plan works on (growth.InFlight), the superseded bedrooms
    /// and suites a pawn still owns a bed in (growth.Slept) and the rooms of an ended role still in use (growth.InUse).
    /// With the census unknown (growth.Fixed null) every room is held.
    /// </summary>
    internal static HashSet<int> HeldRooms(LayoutPlan plan, RoomGrowth growth)
    {
        if (growth.Fixed is null)
        {
            return AllPins(plan);
        }
        var held = new HashSet<int>();
        held.UnionWith(growth.InFlight);
        held.UnionWith(growth.Slept);
        held.UnionWith(growth.InUse);
        return held;
    }

    private sealed record Claim(int Tiled, bool Successor);

    private sealed record PendingRoom(PlannedRoom Room, string Unmatched);

    /// <summary>
    /// Compares the tiled plan of advice with old, the plan the colony held, whose standing rooms are built. A
    /// standing room the embedding kept or superseded is claimed through its block (Built, Replaces); the unbuilt
    /// rooms are matched to the blocks left over by role, the identical interior first and the nearest after.
    /// Attached rooms (airlocks, porches) are the tiler's own and not diffed.
    /// </summary>
    public static ReplanTileDiff DiffReplanTile(LayoutPlan old, IReadOnlySet<int> built, StationAdvice advice)
    {
        var tiled = new List<PlannedRoom>();
        var blocks = new List<EmbeddedBlock>();
        if (advice.Tiled is { } tiledResult)
        {
            var all = tiledResult.Plan.Rooms.ToList();
            var (_, held) = HeldBlocks(advice.Clustering.Groups.Count, advice.Embedding.Held);
            foreach (var b in advice.Embedding.Blocks.Concat(held))
            {
                if (!tiledResult.Rooms.TryGetValue(b.Group, out var id))
                {
                    continue;
                }
                var i = all.FindIndex(r => r.Id == id);
                if (i >= 0)
                {
                    blocks.Add(b);
                    tiled.Add(all[i]);
                }
            }
        }
        var claims = new Dictionary<int, Claim>();
        var free = new List<int>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Built != 0)
            {
                claims[b.Built] = new Claim(i, false);
            }
            else if (b.Replaces != 0)
            {
                claims[b.Replaces] = new Claim(i, true);
            }
            else
            {
                free.Add(i);
            }
        }
        var superseded = new HashSet<int>();
        foreach (var r in old.Rooms)
        {
            if (built.Contains(r.Id) && r.Replaces != 0)
            {
                superseded.Add(r.Replaces);
            }
        }
        var shortBy = new Dictionary<int, int>();
        foreach (var g in advice.Groups)
        {
            if (g.Outgrown)
            {
                shortBy[g.Group.Id] = g.Group.Area - g.Capacity;
            }
        }
        var notBlock = new HashSet<int>();
        foreach (var d in advice.Embedding.Decisions)
        {
            if (d.Reason == ReasonEmbedBuiltNotRect)
            {
                notBlock.Add(d.Room);
            }
        }
        var rows = new List<TileRoomDiff>();
        var unmatched = new List<UnmatchedBuilt>();
        var rooms = old.Rooms.Where(r => !r.Attached()).OrderBy(r => r.Id).ToList();
        var seen = new HashSet<int>();

        // pending are the rooms no block claims: the unbuilt rooms and the standing ones the chain cannot pin
        // (unmatched, with the reason).
        var pending = new List<PendingRoom>();
        foreach (var r in rooms)
        {
            seen.Add(r.Id);
            if (claims.TryGetValue(r.Id, out var c))
            {
                var row = new TileRoomDiff
                {
                    Room = r.Id,
                    Tiled = tiled[c.Tiled].Id,
                    Role = r.Role,
                    Built = built.Contains(r.Id),
                    Verdict = TileDiffSame,
                    Reason = ReasonDiffKept,
                    ShortBy = shortBy.GetValueOrDefault(blocks[c.Tiled].Group),
                };
                if (c.Successor)
                {
                    row = row with { Verdict = TileDiffMoved, Reason = ReasonDiffSuccessor };
                }
                else if (tiled[c.Tiled].Region.Bounds() != r.Region.Bounds())
                {
                    row = row with { Verdict = TileDiffMoved };
                }
                rows.Add(row);
                continue;
            }
            if (!built.Contains(r.Id))
            {
                pending.Add(new PendingRoom(r, ""));
                continue;
            }
            if (superseded.Contains(r.Id))
            {
                rows.Add(new TileRoomDiff { Room = r.Id, Role = r.Role, Verdict = TileDiffDropped, Reason = ReasonDiffSuperseded, Built = true });
                continue;
            }
            var reason = UnmatchedReason(r, notBlock.Contains(r.Id));
            unmatched.Add(new UnmatchedBuilt(r.Id, r.Role, reason));
            pending.Add(new PendingRoom(r, reason));
        }

        // The pending rooms take the free blocks of their role: the same interior first, then the nearest. A standing
        // room takes one only as the room the tiler would build of its own, since nothing pins it.
        var taken = new HashSet<int>();
        var match = new Dictionary<int, int>();
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var p in pending)
            {
                var r = p.Room;
                if (match.ContainsKey(r.Id))
                {
                    continue;
                }
                var best = -1;
                var bestDist = 0.0;
                foreach (var i in free)
                {
                    if (taken.Contains(i) || tiled[i].Role != r.Role)
                    {
                        continue;
                    }
                    if (pass == 0)
                    {
                        if (tiled[i].Region.Bounds() == r.Region.Bounds())
                        {
                            best = i;
                            break;
                        }
                        continue;
                    }
                    var d = RectCentreDistance(tiled[i].Region.Bounds(), r.Region.Bounds());
                    if (best < 0 || d < bestDist)
                    {
                        best = i;
                        bestDist = d;
                    }
                }
                if (best >= 0)
                {
                    taken.Add(best);
                    match[r.Id] = best;
                }
            }
        }
        foreach (var p in pending)
        {
            var r = p.Room;
            var row = new TileRoomDiff { Room = r.Id, Role = r.Role, Built = p.Unmatched != "", Verdict = TileDiffDropped, Reason = p.Unmatched };
            if (row.Reason == "")
            {
                row = row with { Reason = ReasonDiffNoNode };
            }
            if (match.TryGetValue(r.Id, out var i))
            {
                row = row with { Tiled = tiled[i].Id, Verdict = TileDiffMoved, Reason = ReasonDiffSited };
                if (tiled[i].Region.Bounds() == r.Region.Bounds())
                {
                    row = row with { Verdict = TileDiffSame };
                }
                if (p.Unmatched != "")
                {
                    row = row with { Reason = ReasonDiffUnpinned };
                }
            }
            rows.Add(row);
        }
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (taken.Contains(i))
            {
                continue;
            }
            if ((b.Built != 0 && !seen.Contains(b.Built)) || (b.Replaces != 0 && !seen.Contains(b.Replaces)))
            {
                rows.Add(new TileRoomDiff { Tiled = tiled[i].Id, Role = tiled[i].Role, Verdict = TileDiffNew, Reason = ReasonDiffRetired });
            }
            else if (b.Built == 0 && b.Replaces == 0)
            {
                rows.Add(new TileRoomDiff { Tiled = tiled[i].Id, Role = tiled[i].Role, Verdict = TileDiffNew, Reason = ReasonDiffNoOldRoom });
            }
        }
        return new ReplanTileDiff { Rooms = rows, Unmatched = unmatched };
    }

    /// <summary>
    /// Why a standing room has no block: the chain does not see outdoor rooms or rooms with no native role
    /// (BuiltRegions), a room that is no rectangle is no block, and any other had no block in its group.
    /// </summary>
    internal static string UnmatchedReason(PlannedRoom r, bool notBlock)
    {
        var native = ModuleRoomRoles.ContainsKey(r.Role);
        var special = MatchedPlanned.ContainsKey(r.Role);
        if (RoomNodeRoles.Contains(r.Role))
        {
            // BuiltRegions pins it (a room node's room); it has no block only because no node of its role stands in
            // the graph or it is no rectangle.
            return notBlock ? ReasonDiffNotBlock : ReasonDiffNoNode;
        }
        if (r.Outdoor)
        {
            return ReasonDiffOutdoor;
        }
        if (!native && !special)
        {
            return ReasonDiffNoNativeRole;
        }
        if (notBlock)
        {
            return ReasonDiffNotBlock;
        }
        return ReasonDiffNoNode;
    }

    /// <summary>The straight distance between two rectangles' centres.</summary>
    internal static double RectCentreDistance(Rectangle a, Rectangle b)
    {
        var dx = (double)((2 * a.X) + a.Width - (2 * b.X) - b.Width) / 2;
        var dz = (double)((2 * a.Z) + a.Height - (2 * b.Z) - b.Height) / 2;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }
}
