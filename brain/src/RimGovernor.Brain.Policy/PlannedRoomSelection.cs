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
    /// <summary>Started, or a ringless role (Unfenced: no ring to open): the reconcile places it as before.</summary>
    Place = 0,

    /// <summary>
    /// Unstarted and ringed with none of its sites selected, whether the record's comparison assessed it or none did
    /// (#3754): nothing is placed.
    /// </summary>
    Hold,

    /// <summary>Unstarted with selected sites: exactly those are placed, in their stuff.</summary>
    Selected,
}

public static partial class PlannedRules
{
    /// <summary>
    /// The planned-room selection of one comparison (#3278, #3342, #3651); null when the review compared nothing (no
    /// candidates or no selection). The selected sites are the planned sites the selected portfolio's plan starts inside
    /// the horizon (#3744): any of a site's activities (ForecastPlannedSite.Activities and Finishes) allocated, whichever
    /// selected roots carried them: a site root, a room alternative or a shelter. A ring that outlasts the horizon is
    /// placed from the sites it starts, valued by its projected closure. They are read from the plan, not the development
    /// count, so a site of a priced room is placed though it counts no development. Root ids play no part (#3342), so
    /// equivalent root sets that share one schedule give one record, and a site the plan does not start is not selected
    /// (it is placed once its room has started). The sites are joined to the round's planned-site facts for their
    /// operation, def, stuff and anchors; one the facts no longer name is left out.
    /// </summary>
    public static PlannedSelection? PlannedSelectionOf(ForecastCoupledCandidates? candidates, ForecastCoupledSelection? selection,
        Fact<PlannedSiteFacts> planned)
    {
        if (candidates is null || selection is null)
        {
            return null;
        }
        var allocated = selection.Selected.Plan.Allocations.Select(a => a.Activity).ToHashSet(StringComparer.Ordinal);
        var chosen = new SortedSet<string>(candidates.Context.PlannedSites
            .Where(s => s.Activities.Concat(s.Finishes).Any(allocated.Contains)).Select(s => s.Site), StringComparer.Ordinal);
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
    /// (<paramref name="started"/>) clear: it is placed, and the reconcile continues it without the record. A wall swap
    /// (WallUp, #3657) is a standing room's own and stays: it is what the reconcile swaps the room's walls to. An unstarted
    /// room of the plan (<paramref name="planned"/>) the previous record assessed and the latest comparison did not keeps
    /// its previous verdict and sites (#3676): a review whose free colonists could not reach it (a running method holds
    /// them) has not reassessed it, and treating it as never assessed would hold a room it had selected.
    /// </summary>
    public static PlannedSelection PlannedSelectionMemory(PlannedSelection previous, PlannedSelection? latest, Func<int, bool> started,
        Func<int, bool>? planned = null)
    {
        var next = latest ?? previous;
        if (latest is not null)
        {
            var carried = previous.Assessed.Where(r => !latest.Assessed.Contains(r) && (planned?.Invoke(r) ?? true) && !started(r)).ToHashSet();
            next = latest with
            {
                Assessed = EquatableArray.From(latest.Assessed.Concat(carried).Order()),
                Sites = EquatableArray.From(latest.Sites.Concat(previous.Sites.Where(s => carried.Contains(s.Room)))),
            };
        }
        return next with { Sites = EquatableArray.From(next.Sites.Where(s => s.Op == OpKind.WallUp || !started(s.Room))) };
    }

    /// <summary>
    /// The stuff the latest comparison selected a standing room's walls swapped to (#3657): its selected WallUp site's, null
    /// when none is. A wall stands in the stuff it was built in unless a priced upgrade was selected.
    /// </summary>
    public static string? SelectedWallUpgrade(PlannedSelection record, int room) =>
        record.Sites.FirstOrDefault(s => s.Room == room && s.Op == OpKind.WallUp)?.Stuff;

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
    /// The gate of one planned room (#3278, #3754): a started room, or a ringless role (Unfenced: range, ring, yard,
    /// airlock), is placed as before. An unstarted ringed room opens its ring only on the portfolio's verdict: it is held
    /// unless the record's comparison selected some of its sites, which are returned. A room no comparison the record
    /// keeps assessed is held too (the restart window before the first comparison, urgent reviews, rooms the comparison's
    /// budget cut), so no ring opens unpriced.
    /// </summary>
    public static (PlannedRoomGate Gate, IReadOnlyList<SelectedPlannedSite> Sites) PlannedRoomGateOf(PlannedSelection record, PlannedRoom room, bool started)
    {
        if (started || room.Unfenced())
        {
            return (PlannedRoomGate.Place, []);
        }
        var sites = record.Sites.Where(s => s.Room == room.Id).ToArray();
        return sites.Length == 0 ? (PlannedRoomGate.Hold, []) : (PlannedRoomGate.Selected, sites);
    }

    /// <summary>
    /// Whether a started room is done (#3754, epic #3738 D3): its reconcile owes nothing a builder can do now. Every owed
    /// operation (<see cref="Reconciliation.Owed"/>: ring, fence, roof, vent, floor, template pieces, removals and
    /// clearing) keeps it open, except template pieces to build that are all <paramref name="blocked"/> (missing research,
    /// or no stuff or cost-list material the colony stocks or can obtain from the map, #3839, on read evidence only:
    /// RoundsBuildingPlanner.PieceBlockedOf, #3819; the
    /// service-day projection reads the same predicate, ForecastCoupledContext.BlockedItems, #3827): while only those remain the
    /// room frees its builders, and it is open again once one of them can be worked. A started room is open while it is
    /// not done; the open rings close first in the ring-closure projection (ForecastCoupledContext.OpenRooms).
    /// </summary>
    public static bool PlannedRoomDone(IReadOnlyList<Operation> owed, Func<WantedPiece, bool> blocked) =>
        owed.All(op => op.Kind == OpKind.Build && op.Pieces.All(blocked));

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
