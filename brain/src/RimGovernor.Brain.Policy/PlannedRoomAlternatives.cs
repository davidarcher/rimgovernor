namespace RimGovernor.Brain.Policy;

/// <summary>How much of a planned room an alternative builds: its operations, filtered by kind from what the reconciler owes.</summary>
public enum PlannedScope
{
    /// <summary>The ring: walls, doors and vents.</summary>
    Shell,
    /// <summary>The ring and the floors.</summary>
    ShellFloor,
    /// <summary>Everything the plan owes the room: the ring, floors and stored stations.</summary>
    Full,
}

/// <summary>
/// A candidate Method alternative for building one planned room: a temporary, mutually exclusive proposal with
/// the exact material it would claim (Claims, the same commitment shape the baseline planned claim has). Alternatives
/// of one Room exclude each other. It is not an outcome: it has no need, activity, deadline or progress units, so
/// the shared outcome comparator cannot take it yet, and nothing here ranks or picks among them.
/// </summary>
public sealed record PlannedRoomAlternative(int Room, string Id, string Stuff, PlannedScope Scope, ForecastCommitment Claims);

public static partial class PolicyFunctions
{
    /// <summary>The kinds of operation a scope builds; Full is every kind the plan owes.</summary>
    private static bool InScope(PlannedScope scope, OpKind kind) => kind switch
    {
        OpKind.FloorIn => scope != PlannedScope.Shell,
        OpKind.Build => scope == PlannedScope.Full,
        _ => true,
    };

    /// <summary>
    /// The candidate Method alternatives for each planned room that still owes material, from the same owed diff the
    /// baseline claim prices (PlannedOwedCost). For every stuff that can make the room's wall and door (stuffs; the
    /// caller passes the catalog's options, shellPriceOf giving the ring's price list for that stuff) there is one
    /// alternative per scope, named <c>planned/room{id}/{stuff}</c> for the full scope and
    /// <c>planned/room{id}/{stuff}/{scope}</c> for a partial one. A stuff-less ring (stuffs empty) has only the
    /// scope alternatives, priced by shellPriceOf(""). An alternative whose claim is empty is left out, as the
    /// baseline does. Postponement is not generated: the shared comparator owns it. The alternatives are
    /// not ranked and this function does not select.
    /// </summary>
    public static IReadOnlyList<PlannedRoomAlternative> PlannedRoomAlternatives(
        IReadOnlyList<(PlannedRoom Room, IReadOnlyList<Operation> Owed)> rooms,
        IReadOnlyList<string> stuffs,
        Func<string, Func<string, IReadOnlyList<Amount>?>> shellPriceOf,
        Func<string, IReadOnlyList<Amount>?> piecePrice,
        Func<string, IReadOnlyList<Amount>?> floorPrice,
        string ventDef,
        IReadOnlySet<(string Def, Cell Cell)> placed)
    {
        var alternatives = new List<PlannedRoomAlternative>();
        foreach (var stuff in stuffs.Count == 0 ? new[] { "" } : stuffs.Order(StringComparer.Ordinal).ToArray())
        {
            foreach (var scope in new[] { PlannedScope.Shell, PlannedScope.ShellFloor, PlannedScope.Full })
            {
                var scoped = rooms.Select(r => (r.Room, (IReadOnlyList<Operation>)[.. r.Owed.Where(o => InScope(scope, o.Kind))])).ToList();
                var cost = PlannedOwedCost(scoped, shellPriceOf(stuff), piecePrice, floorPrice, ventDef, placed);
                foreach (var (room, needs) in cost.OrderBy(c => c.Key))
                {
                    var commitments = PlannedRoomCommitments(Known<IReadOnlyDictionary<int, IReadOnlyDictionary<Resource, long>>>(
                        new Dictionary<int, IReadOnlyDictionary<Resource, long>> { [room] = needs }));
                    if (commitments.Value.Count == 0)
                    {
                        continue;
                    }
                    var id = "planned/room" + room + (stuff.Length > 0 ? "/" + stuff : "") + (scope == PlannedScope.Full ? "" : "/" + scope);
                    alternatives.Add(new PlannedRoomAlternative(room, id, stuff, scope, commitments.Value[0] with { Id = id }));
                }
            }
        }
        return alternatives;
    }
}
