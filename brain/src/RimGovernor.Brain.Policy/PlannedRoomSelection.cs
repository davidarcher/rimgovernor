namespace RimGovernor.Brain.Policy;

/// <summary>
/// One planned site the latest coupled comparison selected (#3278): its room, owed operation, def, stuff and placement
/// anchors, as the round's planned-site facts (PlannedBuildSite) name them.
/// </summary>
public sealed record SelectedPlannedSite(int Room, OpKind Op, string Definition, string Stuff, EquatableArray<Cell> Positions);

/// <summary>
/// What the latest known coupled comparison decided for unstarted planned rooms (#3278): the rooms it assessed (a valued
/// alternative in <c>Context.PlannedRooms</c>) and the planned sites it selected. Derived and in memory only.
/// </summary>
public sealed record PlannedSelection
{
    public EquatableArray<int> Assessed { get; init; }

    public EquatableArray<SelectedPlannedSite> Sites { get; init; }
}

/// <summary>How the room reconcile treats a planned room (#3278).</summary>
public enum PlannedRoomGate
{
    /// <summary>Started, or never assessed by the comparison: the reconcile places it as before.</summary>
    Place = 0,

    /// <summary>Unstarted, assessed, and none of its sites selected: nothing is placed.</summary>
    Hold,

    /// <summary>Unstarted with selected sites: exactly those are placed, in their stuff.</summary>
    Selected,
}

public static partial class PlannedRules
{
    /// <summary>
    /// The planned-room selection of one comparison (#3278, #3342); null when the review compared nothing (no candidates
    /// or no selection). The selected sites are the planned sites the selected plan completes inside the horizon, as its
    /// evaluated outcome counts them (ForecastCoupledOutcome.RequestedDevelopment), whichever roots carried them: a site
    /// root, a room alternative or a shelter. Root ids play no part, so equivalent root sets that share one schedule give
    /// one record, and a site the plan schedules but does not finish inside the horizon is not selected (it is placed
    /// once its room has started). The sites are joined to the round's planned-site facts for their operation, def,
    /// stuff and anchors; one the facts no longer name is left out.
    /// </summary>
    public static PlannedSelection? PlannedSelectionOf(ForecastCoupledCandidates? candidates, ForecastCoupledSelection? selection,
        Fact<PlannedSiteFacts> planned)
    {
        if (candidates is null || selection is null)
        {
            return null;
        }
        var chosen = new SortedSet<string>(selection.Selected.RequestedDevelopment, StringComparer.Ordinal);
        var facts = planned.TryGetValue(out var known)
            ? known.Sites.Where(s => s.Room > 0 && s.Zone.Length == 0).ToDictionary(s => s.Id, StringComparer.Ordinal)
            : [];
        return new PlannedSelection
        {
            Assessed = EquatableArray.From(candidates.Context.PlannedRooms.Select(a => a.Room).Distinct().Order()),
            Sites = EquatableArray.From(chosen.Where(facts.ContainsKey).Select(id => facts[id])
                .Select(s => new SelectedPlannedSite(s.Room, s.Op, s.Definition, s.Stuff, s.Positions))),
        };
    }

    /// <summary>
    /// The record after a review (#3278): the latest known selection replaces the previous one (a later selection that
    /// drops a site drops it), a review without one keeps the previous, and the sites of a room observed started
    /// (<paramref name="started"/>) clear: it is placed, and the reconcile continues it without the record.
    /// </summary>
    public static PlannedSelection PlannedSelectionMemory(PlannedSelection previous, PlannedSelection? latest, Func<int, bool> started)
    {
        var next = latest ?? previous;
        return next with { Sites = EquatableArray.From(next.Sites.Where(s => !started(s.Room))) };
    }

    /// <summary>
    /// Whether a planned room is started (#3278): a colony-built wall, door, fence or gate stands on a ring cell that is
    /// its own alone among the plan's built rooms (PlannedBuiltRooms), a blueprint or frame (<paramref name="sites"/>, by
    /// cell) stands on its interior or own ring, or a template piece of the plan stands at its anchor
    /// (<paramref name="standing"/>, def at cell). A shared ring cell and natural rock (not in GroundOf) do not count.
    /// </summary>
    public static bool PlannedRoomStarted(LayoutPlan plan, PlannedRoom room, GroundCensus colony, IEnumerable<Cell> sites,
        IReadOnlySet<(string Def, Cell Cell)> standing)
    {
        var shared = PlannedBuiltRooms(plan).Where(r => r.Id != room.Id).SelectMany(r => r.WallRing().Cells()).ToHashSet();
        var own = room.WallRing().Cells().Where(c => !shared.Contains(c)).ToHashSet();
        if (own.Any(c => colony.Walls.Contains(c) || colony.Doors.Contains(c) || colony.Fences.Contains(c) || colony.Gates.Contains(c)))
        {
            return true;
        }
        if (sites.Any(c => own.Contains(c) || room.Region.Contains(c)))
        {
            return true;
        }
        return plan.Pieces.TryGetValue(room.Id, out var pieces) && pieces.Any(p => standing.Contains((p.Def, p.Wanted().Anchor())));
    }

    /// <summary>
    /// The gate of one planned room (#3278): a started room, or one the latest comparison did not assess, is placed as
    /// before; an assessed unstarted room is held, unless the comparison selected some of its sites, which are returned.
    /// </summary>
    public static (PlannedRoomGate Gate, IReadOnlyList<SelectedPlannedSite> Sites) PlannedRoomGateOf(PlannedSelection record, int room, bool started)
    {
        if (started || !record.Assessed.Contains(room))
        {
            return (PlannedRoomGate.Place, []);
        }
        var sites = record.Sites.Where(s => s.Room == room).ToArray();
        return sites.Length == 0 ? (PlannedRoomGate.Hold, []) : (PlannedRoomGate.Selected, sites);
    }

    /// <summary>
    /// The reconcile's operations narrowed to the selected sites (#3278): ring, floor and roof cells a selected site of the
    /// same operation names, template pieces (built or installed) a selected piece site names by def and anchor, and the
    /// foreign-thing and removal targets over what is kept. Every other operation is left for later, once the room has
    /// started.
    /// </summary>
    public static IReadOnlyList<Operation> SelectedPlannedOps(IReadOnlyList<Operation> ops, IReadOnlyList<SelectedPlannedSite> sites)
    {
        var cells = sites.Where(s => s.Op != OpKind.FloorIn && s.Op != OpKind.Build)
            .SelectMany(s => s.Positions.Select(c => (s.Op, Cell: c))).ToHashSet();
        var floors = sites.Where(s => s.Op == OpKind.FloorIn).SelectMany(s => s.Positions.Select(c => (s.Definition, Cell: c))).ToHashSet();
        var pieces = sites.Where(s => s.Op == OpKind.Build).SelectMany(s => s.Positions.Select(c => (s.Definition, Cell: c))).ToHashSet();
        var kept = new List<Operation>();
        foreach (var op in ops)
        {
            var next = op.Kind switch
            {
                OpKind.WallIn or OpKind.DoorIn or OpKind.VentIn or OpKind.RoofIn => op with { Cells = [.. op.Cells.Where(c => cells.Contains((op.Kind, c)))] },
                OpKind.FloorIn => op with { Floors = [.. op.Floors.Where(f => floors.Contains((f.DefName, f.Cell)))] },
                OpKind.Build or OpKind.Install => op with { Pieces = [.. op.Pieces.Where(p => pieces.Contains((p.DefName, p.Anchor())))] },
                _ => null,
            };
            if (next is not null && (next.Cells.Count > 0 || next.Floors.Count > 0 || next.Pieces.Count > 0))
            {
                kept.Add(next);
            }
        }
        var footprint = kept.SelectMany(op => op.Cells.Concat(op.Floors.Select(f => f.Cell))
            .Concat(op.Pieces.SelectMany(p => RectCells(new Rectangle(p.Minimum.X, p.Minimum.Z, p.Maximum.X - p.Minimum.X + 1, p.Maximum.Z - p.Minimum.Z + 1)))))
            .ToHashSet();
        var removals = new List<Operation>();
        foreach (var op in ops.Where(o => o.Kind is OpKind.Claim or OpKind.Cut or OpKind.HaulOut or OpKind.FurnitureOut or OpKind.Pack or OpKind.PackInUse or OpKind.FloorOut))
        {
            var next = op with
            {
                Targets = [.. op.Targets.Where(t => RectCells(new Rectangle(t.Minimum.X, t.Minimum.Z, t.Maximum.X - t.Minimum.X + 1, t.Maximum.Z - t.Minimum.Z + 1)).Any(footprint.Contains))],
                Floors = [.. op.Floors.Where(f => footprint.Contains(f.Cell))],
            };
            if (next.Targets.Count > 0 || next.Floors.Count > 0)
            {
                removals.Add(next);
            }
        }
        return [.. removals, .. kept];
    }

    /// <summary>
    /// The ring stuff the selected sites were compared in (#3278): the selected wall's and door's, each standing in for
    /// the other when only one is selected (a planned ring stuff is one the door is made from too); null when neither is.
    /// </summary>
    public static (string Wall, string Door)? SelectedRingStuff(IReadOnlyList<SelectedPlannedSite> sites)
    {
        var wall = sites.FirstOrDefault(s => s.Op == OpKind.WallIn)?.Stuff;
        var door = sites.FirstOrDefault(s => s.Op == OpKind.DoorIn)?.Stuff;
        return wall is null && door is null ? null : (wall ?? door!, door ?? wall!);
    }
}
