using System.Text;

namespace RimGovernor.Brain.Policy;

// Go: admission.go. Package policy admits explicit building work from complete native observations.
// Admission is not dispatch authorization; Hands must refresh native safety checks.

/// <summary>A native resource definition name. Open set: the game's defs, so a wrapper rather than an enum.</summary>
public readonly partial record struct Resource(string Value) : IComparable<Resource>
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    public bool Equals(Resource other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public int CompareTo(Resource other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(Resource left, Resource right) => left.CompareTo(right) < 0;

    public static bool operator >(Resource left, Resource right) => left.CompareTo(right) > 0;

    public static bool operator <=(Resource left, Resource right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Resource left, Resource right) => left.CompareTo(right) >= 0;
}

public sealed record Amount(Resource Resource, long Count);

public sealed record Stock(Resource Resource, Fact<long> Available);

public readonly record struct Bounds(int Width, int Height);

/// <summary>
/// The spending class a building candidate is admitted under, recorded with the admission. RimWorld places
/// every frame regardless of stock and holds it natively for materials, so admission never refuses for stock:
/// the game settles materials when the work happens.
/// </summary>
public enum Purpose
{
    [WireName("")]
    None,

    [WireName("routine")]
    Rounds,
    Defense,
    Shelter,
}

public sealed record Dependency(ActionId Action, Fact<bool> Completed, GenerationSnapshot Snapshot, Tick Tick);

/// <summary>
/// Bound to the exact action, including resolved anchor/rotation/stuff. SafeToPlace includes normal-game
/// legality and absence of destructive side effects; CanPlace alone does not establish that existing objects
/// will be preserved.
/// </summary>
public sealed record Preview
{
    public required DomainAction Action { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public Fact<bool> CanPlace { get; init; }

    public Fact<bool> SafeToPlace { get; init; }

    public Fact<bool> MadeFromStuff { get; init; }

    public Fact<bool> WatchCellsAccessible { get; init; }

    /// <summary>For a wind turbine, how many cells of its native catch zone are obstructed; unknown for every other definition.</summary>
    public Fact<int> WindBlockedCells { get; init; }

    public Fact<EquatableArray<Cell>> Footprint { get; init; }

    public Fact<EquatableArray<Amount>> Costs { get; init; }

    /// <summary>Distinguishes deliberate replacement from accidental destruction. Admission still requires SafeToPlace.</summary>
    public EquatableArray<PlacementBlocker> Blockers { get; init; }
}

/// <summary>One thing the previewed placement would disturb.</summary>
public sealed record PlacementBlocker
{
    public string Category { get; init; } = "";

    public string DefName { get; init; } = "";

    public bool Wiped { get; init; }

    public bool Blueprint { get; init; }

    public bool Frame { get; init; }

    public bool Cancelled { get; init; }
}

public sealed record Candidate
{
    public required DomainAction Action { get; init; }

    public required Progress Progress { get; init; }

    public int Priority { get; init; }

    public Purpose Purpose { get; init; }

    public EquatableArray<Dependency> Dependencies { get; init; }

    public required Preview Preview { get; init; }
}

public sealed record StockObservation
{
    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    public EquatableArray<Stock> Values { get; init; }

    /// <summary>Available already subtracts the complete native census of remaining blueprint/frame costs, including other controllers.</summary>
    public bool NativeConstruction { get; init; }
}

/// <summary>
/// Retains the original complete costs even when fresh completion evidence releases its budget. Cancelled
/// uncertain effects keep their footprint; completed work yields geometry to fresh native placement observations.
/// </summary>
public sealed record Reservation(
    DomainAction Action,
    Progress Progress,
    GenerationSnapshot Snapshot,
    EquatableArray<Amount> Costs,
    EquatableArray<Cell> Footprint
);

/// <summary>
/// Candidates belong to Current's single plan. Held reservations may be from other plans in the same
/// colony/map/load. Evidence must be at CurrentTick.
/// </summary>
public sealed record Request
{
    public GenerationSnapshot Current { get; init; }

    public Tick CurrentTick { get; init; }

    public Fact<Bounds> Bounds { get; init; }

    public EquatableArray<Candidate> Candidates { get; init; }

    public StockObservation Stock { get; init; } = new();

    public EquatableArray<Reservation> Held { get; init; }
}

/// <summary>Owns deep copies and exposes no mutable observations.</summary>
public sealed class Input
{
    internal Input(Request request) => Request = request;

    internal Request Request { get; }
}

public readonly partial record struct Reason(string Value)
{
    public string Value { get => field ?? string.Empty; } = Value;

    public bool IsEmpty => Value.Length == 0;

    public bool Equals(Reason other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static readonly Reason NotReady = new("not_ready");
    public static readonly Reason AlreadyReserved = new("already_reserved");
    public static readonly Reason UnknownFacts = new("unknown_facts");
    public static readonly Reason StaleFacts = new("stale_facts");
    public static readonly Reason UnsafePlacement = new("unsafe_placement");
    public static readonly Reason UnsafeThreat = new("unsafe_threat");
    public static readonly Reason CriticalMedical = new("critical_medical");
    public static readonly Reason MaterialRequired = new("explicit_material_required");
    public static readonly Reason DependencyBlocked = new("dependency_incomplete");
    public static readonly Reason GeometryBlocked = new("geometry_conflict");
    public static readonly Reason InvalidHeld = new("held_reservation_unverifiable");
}

public sealed record Refusal(ActionId Action, Reason Reason, Resource Resource);

public sealed record Decision
{
    public List<Reservation> Admitted { get; } = [];

    public List<Reservation> Held { get; } = [];

    public List<Refusal> Refused { get; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>Whether <paramref name="p"/> names a spending class.</summary>
    public static bool ValidPurpose(Purpose p) => p is Purpose.Rounds or Purpose.Defense or Purpose.Shelter;

    public static Input NewInput(Request r)
    {
        r.Current.Validate();
        DomainException.Require(r.CurrentTick.Value >= 0, "negative current tick");
        if (r.Bounds.TryGetValue(out var b))
        {
            DomainException.Require(b.Width > 0 && b.Height > 0, "invalid observed map bounds");
        }

        var candidates = new List<Candidate>();
        var seen = new HashSet<ActionId>();
        foreach (var candidate in r.Candidates)
        {
            var c = candidate;
            ValidAction(c.Action, c.Progress);
            DomainException.Require(seen.Add(c.Action.Id), "duplicate candidate identity");
            DomainException.Require(
                c.Progress.View.Plan == r.Current.Plan && c.Progress.View.Revision == r.Current.Revision,
                "candidate belongs to a different plan revision"
            );
            DomainException.Require(ValidPurpose(c.Purpose), "invalid building purpose");
            var deps = new HashSet<ActionId>();
            foreach (var dep in c.Dependencies)
            {
                DomainException.Require(
                    dep.Action.Value.Trim().Length != 0 && dep.Action != c.Action.Id && deps.Add(dep.Action),
                    "invalid or duplicate dependency identity"
                );
            }

            if (c.Preview.Costs.TryGetValue(out var costs))
            {
                ValidAmounts(costs);
                c = c with { Preview = c.Preview with { Costs = Known(CopyAmounts(costs)) } };
            }

            candidates.Add(c);
        }

        var held = new List<Reservation>();
        var heldIds = new HashSet<ActionId>();
        foreach (var h in r.Held)
        {
            ValidAction(h.Action, h.Progress);
            DomainException.Require(heldIds.Add(h.Action.Id), "duplicate held identity");
            h.Snapshot.Validate();
            var v = h.Progress.View;
            DomainException.Require(
                !(h.Snapshot.Plan != v.Plan || h.Snapshot.Revision != v.Revision || ((v.Stage == Stage.Prepared || v.Attempt.Value > 0) && !SameWorld(h.Snapshot, v.Snapshot))),
                "held reservation scope differs from its progress"
            );
            foreach (var candidate in candidates)
            {
                DomainException.Require(
                    !(candidate.Action.Id == h.Action.Id && candidate.Action != h.Action),
                    "held action identity reused for different intent"
                );
            }

            ValidAmounts(h.Costs);
            held.Add(CopyReservation(h));
        }

        var resources = new HashSet<Resource>();
        foreach (var stock in r.Stock.Values)
        {
            DomainException.Require(ValidResource(stock.Resource) && resources.Add(stock.Resource), "invalid or duplicate stock resource");
            if (stock.Available.TryGetValue(out var n))
            {
                DomainException.Require(n >= 0, "negative stock");
            }
        }

        return new Input(r with { Candidates = EquatableArray.From(candidates), Held = EquatableArray.From(held) });
    }

    internal static void ValidAction(DomainAction a, Progress p)
    {
        var b = a.Building;
        if (a.ZoneCreate is { } zone)
        {
            _ = DomainAction.NewZoneCreateAction(a.Id, zone);
            DomainException.Require(p.View.Action == a.Id, "unbound zone progress");
            return;
        }

        DomainException.Require(b is not null, "unsupported action family");
        _ = new Building(b.Definition, b.Cell, b.Rotation, b.Stuff);
        DomainException.Require(p.View.Action == a.Id, "unbound action progress");
    }

    internal static bool ValidResource(Resource r) =>
        AdmissionValidUtf16(r.Value) && r.Value.Trim().Length != 0 && !r.Value.Contains('\0', StringComparison.Ordinal) && Encoding.UTF8.GetByteCount(r.Value) <= 256;

    private static bool AdmissionValidUtf16(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static void ValidAmounts(IEnumerable<Amount> costs)
    {
        var seen = new HashSet<Resource>();
        foreach (var a in costs)
        {
            DomainException.Require(ValidResource(a.Resource) && a.Count >= 0 && seen.Add(a.Resource), $"invalid or duplicate cost \"{a.Resource}\"");
        }
    }

    internal static EquatableArray<Amount> CopyAmounts(IEnumerable<Amount> costs) =>
        EquatableArray.From(costs.OrderBy(a => a.Resource.Value, StringComparer.Ordinal));

    internal static Reservation CopyReservation(Reservation h) =>
        h with
        {
            Costs = CopyAmounts(h.Costs),
            Footprint = EquatableArray.From(h.Footprint.OrderBy(c => c.X).ThenBy(c => c.Z)),
        };

    internal static bool SameWorld(GenerationSnapshot a, GenerationSnapshot b) => a.Colony == b.Colony && a.Map == b.Map && a.Load == b.Load;

    internal static bool Released(Reservation h)
    {
        var v = h.Progress.View;
        var known = v.Effect.TryGetValue(out var effect);
        return v.Stage == Stage.Cancelled && !v.Unresolved && (v.Attempt.Value == 0 || (known && effect == Effect.Absent));
    }

    internal static bool Footprint(IReadOnlyList<Cell> cells, DomainAction a, Bounds bounds)
    {
        if (cells.Count == 0)
        {
            return false;
        }

        var building = a.Building;
        var anchorCell = building?.Cell ?? default;
        var zone = a.ZoneCreate;
        var isZone = zone is not null;
        if (zone is not null)
        {
            anchorCell = zone.Cells[0];
            if (cells.Count != zone.Cells.Count)
            {
                return false;
            }
        }

        var anchor = false;
        var seen = new HashSet<Cell>();
        foreach (var c in cells)
        {
            if (c.X < 0 || c.Z < 0 || c.X >= bounds.Width || c.Z >= bounds.Height || !seen.Add(c))
            {
                return false;
            }

            anchor = anchor || c == anchorCell;
        }

        if (isZone)
        {
            foreach (var cell in zone!.Cells)
            {
                if (!seen.Contains(cell))
                {
                    return false;
                }
            }
        }

        return anchor;
    }

    public static Decision Admit(Input input)
    {
        var r = input.Request;
        var result = new Decision();

        // Reject an unconstructed Input instead of interpreting zero facts as authority.
        if (!r.Current.IsValid)
        {
            return result;
        }

        var candidates = PolicySort.ByLess(r.Candidates, (a, b) => a.Priority != b.Priority ? a.Priority > b.Priority : a.Action.Id < b.Action.Id);
        var boundsKnown = r.Bounds.TryGetValue(out var bounds);
        var stockFresh = r.Stock.Snapshot.Matches(r.Current);
        var occupied = new Dictionary<Cell, int>();
        var heldIds = new HashSet<ActionId>();
        var heldProblem = default(Reason);
        var heldInvalid = false;
        foreach (var h in r.Held)
        {
            if (Released(h))
            {
                continue;
            }

            result.Held.Add(CopyReservation(h));
            heldIds.Add(h.Action.Id);
            if (!SameWorld(h.Snapshot, r.Current) || !boundsKnown || !Footprint(h.Footprint, h.Action, bounds))
            {
                heldInvalid = true;
                continue;
            }

            var v = h.Progress.View;
            var observed = v.Effect.TryGetValue(out var effect);

            // Settled outcomes preserve history. Fresh native facts replace old reservations, accounting for
            // actual remaining stock and placement.
            var terminalStockFresh =
                observed
                && (effect == Effect.Completed || effect == Effect.Unsuccessful)
                && !v.Unresolved
                && stockFresh
                && r.Stock.Tick >= v.Tick
                && SameWorld(v.Snapshot, r.Current);
            if (terminalStockFresh)
            {
                continue;
            }

            foreach (var c in h.Footprint)
            {
                occupied[c] = occupied.GetValueOrDefault(c) + 1;
            }
        }

        if (heldInvalid)
        {
            heldProblem = Reason.InvalidHeld;
        }

        result.Held.Sort((a, b) => a.Action.Id.CompareTo(b.Action.Id));
        foreach (var c in candidates)
        {
            var space = occupied;
            var identities = heldIds;
            var replace = -1;
            if (heldProblem.IsEmpty)
            {
                for (var i = 0; i < result.Held.Count; i++)
                {
                    var h = result.Held[i];
                    var cv = c.Progress.View;
                    var hv = h.Progress.View;

                    // A Prepared attempt 0 has no write outstanding (dispatch is journaled before the native call),
                    // so its own earlier hold -- current or left behind by a generation that moved -- is superseded
                    // by this fresh admission.
                    if (
                        c.Action == h.Action
                        && cv.Stage == Stage.Prepared
                        && cv.Attempt.Value == 0
                        && !cv.Unresolved
                        && (hv.Stage == Stage.Pending || hv.Stage == Stage.Prepared)
                        && hv.Attempt.Value == 0
                        && !hv.Unresolved
                    )
                    {
                        // Replace the hold only if fresh revalidation succeeds. Failed admission must not expose
                        // its geometry to rivals.
                        replace = i;
                        space = new Dictionary<Cell, int>(occupied);
                        identities = [.. heldIds];
                        foreach (var cell in h.Footprint)
                        {
                            space[cell] = space.GetValueOrDefault(cell) - 1;
                        }

                        identities.Remove(h.Action.Id);
                        break;
                    }
                }
            }

            var reason = Assess(c, r, bounds, boundsKnown, stockFresh, space, identities, heldProblem);
            if (!reason.IsEmpty)
            {
                result.Refused.Add(new Refusal(c.Action.Id, reason, default));
                continue;
            }

            occupied = space;
            heldIds = identities;
            if (replace >= 0)
            {
                result.Held.RemoveAt(replace);
            }

            c.Preview.Costs.TryGetValue(out var costs);
            c.Preview.Footprint.TryGetValue(out var cells);
            foreach (var cell in cells)
            {
                occupied[cell] = occupied.GetValueOrDefault(cell) + 1;
            }

            result.Admitted.Add(CopyReservation(new Reservation(c.Action, c.Progress, r.Current, costs, cells)));
        }

        return result;
    }

    internal static Reason Assess(
        Candidate c,
        Request r,
        Bounds bounds,
        bool boundsKnown,
        bool stockFresh,
        Dictionary<Cell, int> occupied,
        HashSet<ActionId> heldIds,
        Reason heldProblem
    )
    {
        var v = c.Progress.View;

        // A Prepared action prepared under an older snapshot is not ready to dispatch as it stands, but an
        // attempt 0 has no write outstanding and is re-prepared under the current one; only a later attempt's
        // stale preparation waits.
        if (
            (v.Stage != Stage.Pending && v.Stage != Stage.Prepared)
            || v.Unresolved
            || v.Tick > r.CurrentTick
            || (v.Stage == Stage.Prepared && v.Attempt.Value > 0 && !v.Snapshot.Matches(r.Current))
        )
        {
            return Reason.NotReady;
        }

        if (heldIds.Contains(c.Action.Id))
        {
            return Reason.AlreadyReserved;
        }

        if (!heldProblem.IsEmpty)
        {
            return heldProblem;
        }

        var p = c.Preview;
        if (!p.Snapshot.Matches(r.Current) || !stockFresh)
        {
            return Reason.StaleFacts;
        }

        if (p.Action != c.Action)
        {
            return Reason.StaleFacts;
        }

        foreach (var dep in c.Dependencies)
        {
            var known = dep.Completed.TryGetValue(out var complete);
            if (!known || !complete || !dep.Snapshot.Matches(r.Current))
            {
                return Reason.DependencyBlocked;
            }
        }

        var canKnown = p.CanPlace.TryGetValue(out var canPlace);
        var safeKnown = p.SafeToPlace.TryGetValue(out var safe);
        var madeKnown = p.MadeFromStuff.TryGetValue(out var made);
        if (!canKnown || !safeKnown || !madeKnown)
        {
            return Reason.UnknownFacts;
        }

        if (!canPlace || !safe)
        {
            return Reason.UnsafePlacement;
        }

        var b = c.Action.Building;
        if (made && (b?.Stuff ?? "").Length == 0)
        {
            return Reason.MaterialRequired;
        }

        var cellsKnown = p.Footprint.TryGetValue(out var cells);
        var costsKnown = p.Costs.TryGetValue(out var costs);
        if (c.Action.Kind == ActionKind.ZoneCreate && (made || costs.Count > 0))
        {
            return Reason.UnknownFacts;
        }

        if (!boundsKnown || !cellsKnown || !costsKnown)
        {
            return Reason.UnknownFacts;
        }

        if (!Footprint(cells, c.Action, bounds))
        {
            return Reason.GeometryBlocked;
        }

        foreach (var cell in cells)
        {
            if (occupied.GetValueOrDefault(cell) > 0)
            {
                return Reason.GeometryBlocked;
            }
        }

        return default;
    }
}
