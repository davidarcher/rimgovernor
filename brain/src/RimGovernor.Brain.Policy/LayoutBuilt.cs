namespace RimGovernor.Brain.Policy;

// The built world in the embedding (#2877). A room that stands is a fixed block: the group MatchGroupRooms
// gives it keeps its interior, and the embedding places the other groups around it. The only way a built room
// moves is the relocation pass (Embedder.Relocate), which prices the move the way the replan always did
// (layout_switch.go) and so adds no constant:
//
//   - score gain: the room's best other position, of the same shape, must beat staying by switchThreshold:
//     ReplanGain plus BuiltSwitch and BuiltSwitchCell per floor cell (the walls, floor and furniture built
//     twice). That price already counts the demolished walls and built furniture, so no second wall or
//     furniture term is added; what stands inside a room is #2881.
//   - bad ground: the room stands on rich soil and a position has less. The hard tier decides, so the price
//     is waived.
//
// One rebuild at a time: at most one room moves, the best (bad ground before score gain, then the larger
// margin, ties to the lower group), and none while a rebuild is in flight (EmbedInput.InFlight). A pinned room
// (TilePinned), one the colony holds, never moves. Ground the plan retired but has not cleared
// (EmbedInput.RetiredGround) is closed to new blocks. A moved room's block carries Replaces, the successor
// path's id of the room it supersedes; the old room is not in the embedding, it stands until the new one does.
// Conflict pricing (merge, split, lost room effects) is #2878.
public static partial class PolicyConstants
{
    // Verdicts and reasons of built-room decisions.
    public const string EmbedKeep = "keep";
    public const string EmbedMove = "move";

    public const string ReasonEmbedBuiltKept = "embed_built_kept";
    public const string ReasonEmbedBuiltPinned = "embed_built_pinned";
    public const string ReasonEmbedBuiltInFlight = "embed_built_in_flight";
    public const string ReasonEmbedBuiltBelow = "embed_built_below_threshold";
    public const string ReasonEmbedBuiltDeferred = "embed_built_one_rebuild_at_a_time";
    public const string ReasonEmbedBuiltBadGround = "embed_built_moved_bad_ground";
    public const string ReasonEmbedBuiltGrown = "embed_built_grown_by_replacement";
    public const string ReasonEmbedBuiltMovedGain = "embed_built_moved_score_gain";
    public const string ReasonEmbedBuiltOffMap = "embed_built_off_map";
    public const string ReasonEmbedBuiltNotRect = "embed_built_not_a_block";
}

/// <summary>What the relocation pass decided for one run.</summary>
internal sealed class BuiltRun
{
    /// <summary>The group that moved, -1 for none.</summary>
    public int Moved { get; set; } = -1;

    /// <summary>One entry per built group.</summary>
    public List<EmbedDecision> Decisions { get; set; } = [];

    /// <summary>The switching price of the move, in score units (0 for none and for bad ground).</summary>
    public double Price { get; set; }
}

internal sealed class RelocateMove
{
    public int Group { get; init; }

    public Rectangle Rect { get; init; }

    public double Cost { get; init; }

    public double Margin { get; set; }

    /// <summary>Bad ground or an outgrown room: the hard tier decides, no price.</summary>
    public bool Hard { get; set; }

    public bool Grown { get; set; }
}

internal sealed partial class Embedder
{
    internal IReadOnlyList<EmbedDecision> BuiltReport => _builtReport;

    /// <summary>
    /// Keeps the pinned standing rooms that are no group's home block (a surplus room beside the one a group
    /// keeps, a room of a role no group has left, an outdoor room or one with no native role) as obstacles: the
    /// room is a rectangle the blocks avoid, whether or not a group carries it (#3019). A room that is no
    /// rectangle, or whose ring is off the map, is reported and not held.
    /// </summary>
    private void HoldStanding(IReadOnlyList<BuiltRegion> standing)
    {
        foreach (var b in standing)
        {
            if (_homeRoom.Contains(b.Room))
            {
                continue;
            }
            var reported = _builtReport.Any(d => d.Room == b.Room);
            if (!b.Region.IsRect())
            {
                if (!reported)
                {
                    _builtReport.Add(new EmbedDecision { Group = -1, Other = -1, Verdict = PolicyConstants.EmbedKeep, Reason = PolicyConstants.ReasonEmbedBuiltNotRect, Room = b.Room });
                }
            }
            else if (!Base.Inside(PolicyFunctions.Pad(b.Region.Bounds(), 1)))
            {
                _builtReport.Add(new EmbedDecision { Group = -1, Other = -1, Verdict = PolicyConstants.EmbedKeep, Reason = PolicyConstants.ReasonEmbedBuiltOffMap, Room = b.Room });
            }
            else
            {
                _held.Add(new HeldRoom(b.Room, b.Region.Bounds(), b.Planned));
            }
        }
        var sorted = _held.SortedBy((a, b) => a.Room < b.Room);
        _held.Clear();
        _held.AddRange(sorted);
    }

    /// <summary>Reports a block that touches a held room.</summary>
    private bool HitsHeld(Rectangle r) => _held.Any(h => PolicyFunctions.Collides(r, h.Interior));

    /// <summary>
    /// The relocation pass over a placed embedding: the single built room that gains most by moving moves, when
    /// any triggers. blocks is changed in place; the groups that were not built are not touched.
    /// </summary>
    public BuiltRun Relocate(Rectangle[] blocks)
    {
        var output = new BuiltRun { Moved = -1 };
        RelocateMove? best = null;
        var below = new HashSet<int>();
        var trigger = new Dictionary<int, RelocateMove>();
        for (var i = 0; i < _nRooms; i++)
        {
            var h = _home[i];
            if (h.Width == 0 || _pinned[i] || _inFlight)
            {
                continue;
            }
            var old = blocks[i];
            blocks[i] = default;
            var here = BlockCost(i, h, blocks);
            var sizes = new List<(int W, int H)> { (h.Width, h.Height) };
            if (h.Width != h.Height)
            {
                sizes.Add((h.Height, h.Width));
            }
            // A group that outgrew its room is replaced by a block of the size it needs, beside the old room (#3020).
            var grown = _outgrown[i];
            if (grown)
            {
                sizes = [.. _shapes[i]];
            }
            var richHere = Rich.Sum(h);
            var threshold = (double)PolicyFunctions.SwitchThreshold(new PlannedRoom { Region = PolicyFunctions.RectRegion(h) });
            RelocateMove? bestBad = null;
            RelocateMove? bestGain = null;
            var any = false;
            var top = 0.0;
            foreach (var c in _core)
            {
                foreach (var sh in sizes)
                {
                    var r = new Rectangle(c.X, c.Z, sh.W, sh.H);
                    if (r == h || PolicyFunctions.Collides(r, h) || !Fits(i, r, blocks, _sky[i]))
                    {
                        continue;
                    }
                    var cost = BlockCost(i, r, blocks);
                    var cand = new RelocateMove { Group = i, Rect = r, Cost = cost, Margin = here - cost };
                    if (!any || cand.Margin > top)
                    {
                        any = true;
                        top = cand.Margin;
                    }
                    if (grown)
                    {
                        cand.Hard = true;
                        cand.Grown = true;
                        cand.Margin = _groups[i].Area - (h.Width * h.Height); // the floor it lacks
                        if (bestBad is null || PolicyFunctions.Better(cost, r, bestBad.Cost, bestBad.Rect, true))
                        {
                            bestBad = cand;
                        }
                    }
                    else if (richHere > 0 && Rich.Sum(r) < richHere)
                    {
                        cand.Hard = true;
                        if (bestBad is null || PolicyFunctions.Better(cost, r, bestBad.Cost, bestBad.Rect, true))
                        {
                            bestBad = cand;
                        }
                    }
                    if (cand.Margin >= threshold && (bestGain is null || PolicyFunctions.Better(cost, r, bestGain.Cost, bestGain.Rect, true)))
                    {
                        bestGain = cand;
                    }
                }
            }
            blocks[i] = old;
            if (bestBad is not null)
            {
                trigger[i] = bestBad;
            }
            else if (bestGain is not null)
            {
                trigger[i] = bestGain;
            }
            else if (any && top > 0)
            {
                below.Add(i);
            }
        }
        foreach (var i in trigger.Keys.Order())
        {
            var t = trigger[i];
            if (best is null || (t.Hard && !best.Hard) || (t.Hard == best.Hard && t.Margin > best.Margin))
            {
                best = t;
            }
        }
        for (var i = 0; i < _nRooms; i++)
        {
            if (_home[i].Width == 0)
            {
                continue;
            }
            var d = new EmbedDecision { Group = i, Other = -1, Verdict = PolicyConstants.EmbedKeep, Reason = PolicyConstants.ReasonEmbedBuiltKept, Room = _homeRoom[i] };
            if (_pinned[i])
            {
                d = d with { Reason = PolicyConstants.ReasonEmbedBuiltPinned };
            }
            else if (_inFlight)
            {
                d = d with { Reason = PolicyConstants.ReasonEmbedBuiltInFlight };
            }
            else if (best is not null && best.Group == i)
            {
                d = d with { Verdict = PolicyConstants.EmbedMove, Reason = PolicyConstants.ReasonEmbedBuiltMovedGain };
                if (best.Grown)
                {
                    d = d with { Reason = PolicyConstants.ReasonEmbedBuiltGrown };
                }
                else if (best.Hard)
                {
                    d = d with { Reason = PolicyConstants.ReasonEmbedBuiltBadGround };
                }
                else
                {
                    output.Price = PolicyFunctions.SwitchThreshold(new PlannedRoom { Region = PolicyFunctions.RectRegion(_home[i]) });
                }
            }
            else if (trigger.TryGetValue(i, out var tr) && tr.Group == i && tr.Rect.Width > 0)
            {
                d = d with { Reason = PolicyConstants.ReasonEmbedBuiltDeferred };
            }
            else if (below.Contains(i))
            {
                d = d with { Reason = PolicyConstants.ReasonEmbedBuiltBelow };
            }
            output.Decisions.Add(d);
        }
        if (best is not null)
        {
            output.Moved = best.Group;
            blocks[best.Group] = best.Rect;
        }
        return output;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Picks, for each room group, the standing room that is its block: the largest rectangular room
    /// MatchGroupRooms gave it (ties to the lower id). Rooms matched to a group beyond that one are not
    /// modelled (the group has one block), and a room that is no rectangle is no block.
    /// </summary>
    internal static (IReadOnlyList<Rectangle> Home, IReadOnlyList<int> Room, bool[] Outgrown, IReadOnlyList<EmbedDecision> Decisions) BuiltHomes(IReadOnlyList<RoomGroup> groups, IReadOnlyList<BuiltRegion> built)
    {
        var outgrown = new bool[groups.Count];
        var home = new Rectangle[groups.Count];
        var room = new int[groups.Count];
        var decisions = new List<EmbedDecision>();
        var byId = new Dictionary<int, BuiltRegion>();
        foreach (var b in built)
        {
            byId[b.Room] = b;
        }
        foreach (var m in MatchGroupRooms(groups, built))
        {
            if (m.Rooms.Count == 0)
            {
                continue;
            }
            BuiltRegion? pick = null;
            foreach (var id in m.Rooms)
            {
                var b = byId[id];
                if (!b.Region.IsRect())
                {
                    decisions.Add(new EmbedDecision { Group = m.Group, Other = -1, Verdict = EmbedKeep, Reason = ReasonEmbedBuiltNotRect, Room = id });
                    continue;
                }
                if (pick is null || b.Region.Area() > pick.Region.Area())
                {
                    pick = b;
                }
            }
            if (pick is not null)
            {
                home[m.Group] = pick.Region.Bounds();
                room[m.Group] = pick.Room;
                outgrown[m.Group] = GroupOutgrown(groups[m.Group], m);
            }
        }
        return (home, room, outgrown, decisions);
    }
}
