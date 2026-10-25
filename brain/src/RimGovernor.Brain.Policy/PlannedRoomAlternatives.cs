namespace RimGovernor.Brain.Policy;

/// <summary>How much of a planned room an alternative builds: its operations, filtered by kind from what the reconciler owes.</summary>
public enum PlannedScope
{
    /// <summary>The ring (walls, doors and vents) and the roof it carries.</summary>
    Shell,
    /// <summary>The shell and the floors.</summary>
    ShellFloor,
    /// <summary>Everything the plan owes the room: the shell, floors and stored stations.</summary>
    Full,
}

/// <summary>
/// One way of building a planned room (#3276): the planned sites (PlannedBuildSite ids) of one ring stuff and one scope.
/// Alternatives of one Room exclude each other; their sites are shared, each its own comparison root, so this names
/// what a whole room needs and nothing ranks or picks among the alternatives here.
/// </summary>
public sealed record PlannedRoomAlternative(int Room, string Id, string Stuff, EquatableArray<string> Sites);

public static partial class PlannedRules
{
    /// <summary>The kinds of operation a scope builds; Full is every kind the plan owes.</summary>
    private static bool InScope(PlannedScope scope, OpKind kind) =>
        kind == OpKind.FloorIn ? scope != PlannedScope.Shell
        : kind == OpKind.Build ? scope == PlannedScope.Full
        : true;

    private static bool RingOp(OpKind kind) => kind is OpKind.WallIn or OpKind.DoorIn or OpKind.VentIn or OpKind.WallUp;

    /// <summary>
    /// The alternatives of each planned room, from its planned sites (PlannedSitesOf; shelf candidates, Room 0, are not
    /// rooms): every scope in the room's first ring stuff, then each further stuff at Full, at most
    /// PlannedAlternativesPerRoom (finding planned_room_alternatives_truncated past it). A ring stuff is one the ring sites
    /// were read in, in their order (cheapest first), a standing room's wall swaps (WallUp, #3657) its upgrade stuffs, each
    /// an alternative to leaving the walls as built; a ring site belongs to its stuff's alternatives, a stuff-less
    /// site (a vent made from no stuff, floors, pieces, the roof) to every alternative its scope covers. An alternative
    /// is named <c>planned/room{id}/{stuff}/{scope}</c> (<c>planned/room{id}/{scope}</c> for a stuff-less ring); one
    /// with no site, or the same sites as an earlier one of its room, is left out. Nothing here ranks or selects.
    /// </summary>
    public static (IReadOnlyList<PlannedRoomAlternative> Alternatives, IReadOnlyList<string> Findings) PlannedRoomAlternatives(IEnumerable<PlannedBuildSite> sites)
    {
        var alternatives = new List<PlannedRoomAlternative>();
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var room in sites.Where(s => s.Room > 0 && s.Zone.Length == 0).GroupBy(s => s.Room).OrderBy(g => g.Key))
        {
            var stuffs = room.Where(s => RingOp(s.Op) && s.Stuff.Length > 0).Select(s => s.Stuff).Distinct().ToList();
            if (stuffs.Count == 0)
            {
                stuffs.Add("");
            }
            var wanted = new List<(string Stuff, PlannedScope Scope)>();
            wanted.AddRange(new[] { PlannedScope.Shell, PlannedScope.ShellFloor, PlannedScope.Full }.Select(scope => (stuffs[0], scope)));
            wanted.AddRange(stuffs.Skip(1).Select(stuff => (stuff, PlannedScope.Full)));
            if (wanted.Count > PlannedAlternativesPerRoom)
            {
                findings.Add("planned_room_alternatives_truncated");
            }
            var seen = new List<EquatableArray<string>>();
            foreach (var (stuff, scope) in wanted.Take(PlannedAlternativesPerRoom))
            {
                var members = EquatableArray.From(room
                    .Where(s => InScope(scope, s.Op) && (!RingOp(s.Op) || s.Stuff.Length == 0 || s.Stuff == stuff))
                    .Select(s => s.Id).Order(StringComparer.Ordinal));
                if (members.Count == 0 || seen.Contains(members))
                {
                    continue;
                }
                seen.Add(members);
                alternatives.Add(new PlannedRoomAlternative(room.Key, "planned/room" + room.Key + (stuff.Length > 0 ? "/" + stuff : "") + "/" + scope,
                    stuff, members));
            }
        }
        return (alternatives, [.. findings]);
    }

    /// <summary>
    /// The work a planned room owes (#3276): the catalog work per piece (work; unknown for any site leaves the sum
    /// unknown) times the pieces, over the sites of its first-stuff Full alternative (PlannedRoomAlternatives). A roof
    /// has no catalog work: Roof says one is owed and left out of Work. A room that owes nothing has Known(0).
    /// </summary>
    public static (double Work, bool Roof) PlannedRoomWork(IReadOnlyList<PlannedBuildSite> sites, int room, Func<PlannedBuildSite, double> work)
    {
        var own = sites.Where(s => s.Room == room && s.Zone.Length == 0).ToList();
        var alternatives = PlannedRoomAlternatives(own).Alternatives;
        if (alternatives.Count == 0)
        {
            return (0.0, false);
        }
        var chosen = alternatives.Last(a => a.Stuff == alternatives[0].Stuff);
        var members = own.Where(s => chosen.Sites.Contains(s.Id)).ToList();
        var total = 0.0;
        foreach (var site in members.Where(s => s.Op != OpKind.RoofIn))
        {
            var each = work(site);
            total += each * site.Positions.Count;
        }
        return (total, members.Any(s => s.Op == OpKind.RoofIn));
    }
}
