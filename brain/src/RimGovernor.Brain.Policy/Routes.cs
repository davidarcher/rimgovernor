using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

// MaintainRoutes keeps every colony facility reachable by the colonists who use it. It reasons only from
// observed travel: the native census answers, for each bed, work bench, storage, dining surface and defence
// emplacement, whether each mobile colonist can reach it from where that colonist stands right now (the
// game's own pathing with the doors that colonist may open) and what the game's chosen path costs. A facility
// no colonist reaches is a deficit, latched by facility until a measured census reads it reachable. The
// method opens the enclosing wall with a door on one of the breach cells native lists for it: a player wall
// on the facility room's border whose outer neighbour some colonist can stand on. Nothing here flood-fills or
// measures straight lines.

public sealed record RoutesPolicy
{
    /// <summary>The definition admitted on a breach cell.</summary>
    public string Door { get; init; } = "";

    /// <summary>The door's material.</summary>
    public string Stuff { get; init; } = "";

    /// <summary>Bounds the breach cells the planner previews per facility.</summary>
    public int MaxBreaches { get; init; }

    internal bool Valid() =>
        PolicyFunctions.FoodId(Door) && PolicyFunctions.FoodId(Stuff) && MaxBreaches >= 1 && MaxBreaches <= 16;
}

/// <summary>The native routes census.</summary>
public sealed record RoutesObservation
{
    /// <summary>The mobile colonists measured; an empty census with no pawns cannot declare anything unreachable.</summary>
    public EquatableArray<string> Pawns { get; init; }

    public EquatableArray<RouteFacility> Facilities { get; init; }

    /// <summary>The most-travelled cells observed since TrafficSince with TrafficSamples samples in all.</summary>
    public EquatableArray<TrafficCell> Traffic { get; init; }

    public uint TrafficSamples { get; init; }

    public Fact<Tick> TrafficSince { get; init; }

    public void Validate()
    {
        var pawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in Pawns)
        {
            if (!PolicyFunctions.FoodId(id) || !pawns.Add(id))
            {
                throw new DomainException("invalid routes pawn");
            }
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Facilities)
        {
            if (!PolicyFunctions.FoodId(f.Id) || !ids.Add(f.Id) || !PolicyFunctions.FoodId(f.Kind))
            {
                throw new DomainException("invalid routes facility");
            }
            if (f.Room.TryGetValue(out var room) && !PolicyFunctions.FoodId(room))
            {
                throw new DomainException("invalid routes facility room");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in f.Travel)
            {
                if (!pawns.Contains(t.Pawn) || !seen.Add(t.Pawn))
                {
                    throw new DomainException("invalid routes travel");
                }
                if (t.Cost.TryGetValue(out var cost) && (cost < 0 || !t.Reachable))
                {
                    throw new DomainException("invalid routes travel cost");
                }
                if (t.Cells.TryGetValue(out var cells) && (cells < 0 || !t.Reachable))
                {
                    throw new DomainException("invalid routes travel cells");
                }
                if (t.Skipped && (!t.Reachable || t.Cost.IsKnown || t.Cells.IsKnown))
                {
                    throw new DomainException("invalid routes skipped travel");
                }
            }
            var breachCells = new HashSet<Cell>();
            foreach (var b in f.Breaches)
            {
                if (!PolicyFunctions.FoodId(b.Edifice) || (b.Pending != "" && !PolicyFunctions.FoodId(b.Pending)) || b.Distance < 0 || !breachCells.Add(b.Cell))
                {
                    throw new DomainException("invalid routes breach");
                }
            }
        }
        var trafficKeys = new HashSet<(TrafficLayer, Cell)>();
        foreach (var t in Traffic)
        {
            if (!t.Layer.Valid() || !PolicyFunctions.FoodId(t.Terrain) || !trafficKeys.Add((t.Layer, t.Cell)) || (t.Pending != "" && !PolicyFunctions.FoodId(t.Pending)))
            {
                throw new DomainException("invalid traffic cell");
            }
        }
    }
}

public sealed record RouteFacility
{
    public string Id { get; init; } = "";

    public string Kind { get; init; } = "";

    public Cell Cell { get; init; }

    public Fact<string> Room { get; init; }

    public EquatableArray<RouteTravel> Travel { get; init; }

    public EquatableArray<RouteBreach> Breaches { get; init; }
}

public sealed record RouteTravel
{
    public string Pawn { get; init; } = "";

    public bool Reachable { get; init; }

    /// <summary>The game's own path cost for the reachable pair; unknown when Skipped.</summary>
    public Fact<int> Cost { get; init; }

    /// <summary>The game's own path length in cells for the reachable pair; unknown when Skipped.</summary>
    public Fact<int> Cells { get; init; }

    /// <summary>
    /// Marks a reachable pair native did not path (the stream's route_path_budget_ms ran out): the doors its
    /// path crosses are unknown, so it can neither raise nor release a door wait.
    /// </summary>
    public bool Skipped { get; init; }
}

public sealed record RouteBreach
{
    public Cell Cell { get; init; }

    public string Edifice { get; init; } = "";

    /// <summary>Names the door already ordered on the wall cell, empty when none.</summary>
    public string Pending { get; init; } = "";

    public int Distance { get; init; }
}

public sealed record TrafficCell
{
    public Cell Cell { get; init; }

    /// <summary>The pawn class the count belongs to.</summary>
    public TrafficLayer Layer { get; init; }

    public uint Samples { get; init; }

    public string Terrain { get; init; } = "";

    public bool Home { get; init; }

    /// <summary>Names the floor already ordered on the cell, empty when none.</summary>
    public string Pending { get; init; } = "";
}

/// <summary>Names a traffic counter layer: each counts cell changes of one pawn class and decays on its own half-life.</summary>
public enum TrafficLayer
{
    /// <summary>Snapshot replays may carry an empty layer: the old sampler counted colonists only.</summary>
    [WireName("")]
    None = 0,

    [WireName("colonist")]
    Colonist,

    /// <summary>Counts any pawn stepping from soil or sand onto a built floor: the dirt entry points.</summary>
    [WireName("crossing")]
    Crossing,

    [WireName("animal")]
    Animal,

    [WireName("visitor")]
    Visitor,

    [WireName("hostile")]
    Hostile,
}

public static class TrafficLayerExtensions
{
    extension(TrafficLayer l)
    {
        public bool Valid() => Enum.IsDefined(l);

        /// <summary>Reports a colonist-layer row, including unlayered snapshot rows.</summary>
        public bool Colonist() => l is TrafficLayer.Colonist or TrafficLayer.None;
    }
}

/// <summary>One facility no mobile colonist can reach.</summary>
public sealed record RouteDeficit
{
    public RouteFacility Facility { get; init; } = new();

    /// <summary>The facility's unordered breach cells nearest first.</summary>
    public EquatableArray<RouteBreach> Breaches { get; init; }

    /// <summary>Counts the breach cells with a door already ordered.</summary>
    public int Pending { get; init; }
}

/// <summary>
/// The per-facility latch: Deficits lists every unreachable facility by kind precedence (storage and
/// stockpiles first, then benches, dining, beds, defence) and then id; Latched is the id set persisted
/// between reviews. Known is false when the census was unknown, in which case Latched repeats the previous
/// latch and Deficits is empty.
/// </summary>
public sealed record RoutesReview
{
    public bool Active { get; init; }

    public bool Known { get; init; }

    public EquatableArray<RouteDeficit> Deficits { get; init; }

    public EquatableArray<string> Latched { get; init; }
}

public enum RoutesMethod
{
    [WireName("")]
    None = 0,

    [WireName("unknown")]
    Unknown,

    [WireName("no_deficit")]
    NoMethod,

    [WireName("route_door_pending")]
    DoorPending,

    [WireName("route_no_breach")]
    NoBreach,

    [WireName("route_door_unavailable")]
    DoorUnavailable,

    [WireName("open_route")]
    Build,
}

public sealed record RoutesProposal
{
    public RoutesMethod Method { get; init; }

    public MethodId Key { get; init; }

    /// <summary>The unreachable facility served.</summary>
    public string Facility { get; init; } = "";

    /// <summary>The door definition.</summary>
    public string Definition { get; init; } = "";

    public string Stuff { get; init; } = "";

    /// <summary>The candidate wall cells nearest first, each to be validated by a native placement preview before one is admitted.</summary>
    public EquatableArray<Cell> Breaches { get; init; }
}

/// <summary>What SelectRoutesMethod needs beyond the review: the planning census row for the door definition.</summary>
public sealed record RoutesFacts
{
    public Fact<bool> DoorAvailable { get; init; }
}

public static partial class PolicyFunctions
{
    public static readonly ConcernId MaintainRoutes = new("MaintainRoutes");

    public static RoutesPolicy DefaultRoutesPolicy() => new() { Door = "Door", Stuff = "WoodLog", MaxBreaches = 4 };

    private static int RouteKindOrder(string kind) => kind switch
    {
        "stockpile" or "storage" => 0,
        "bench" => 1,
        "dining" => 2,
        "bed" => 3,
        "defense" => 4,
        _ => 0,
    };

    /// <summary>
    /// Measures every facility against the colonists' observed reachability. A facility is deficient when the
    /// census lists at least one mobile colonist and none of them reaches it; a colony with no mobile colonist
    /// cannot declare anything unreachable. There is no hysteresis: reachability is the game's own answer and
    /// does not flap. A latched facility whose border still carries an ordered door stays deficient until the
    /// door lands: a door frame is walkable, so the census reads the room reachable before the door stands,
    /// and releasing then would strand the open plan on a half-built wall (as flooring holds on an ordered cell).
    /// </summary>
    public static RoutesReview ReviewRoutes(Fact<RoutesObservation> fact, IReadOnlyList<string> previous, RoutesPolicy p)
    {
        if (!p.Valid())
        {
            throw new DomainException("invalid routes policy");
        }
        if (!fact.TryGetValue(out var v))
        {
            var held0 = previous.Order(StringComparer.Ordinal).ToList();
            return new RoutesReview { Active = held0.Count > 0, Latched = EquatableArray.From(held0) };
        }
        v.Validate();
        if (v.Pawns.Count == 0)
        {
            return new RoutesReview { Known = true };
        }
        var held = new HashSet<string>(previous, StringComparer.Ordinal);
        var deficits = new List<RouteDeficit>();
        var latched = new List<string>();
        foreach (var f in v.Facilities)
        {
            bool reached = false, ordered = false, unpathed = false;
            foreach (var t in f.Travel)
            {
                if (t.Reachable)
                {
                    reached = true;
                    unpathed = unpathed || t.Skipped;
                }
            }
            foreach (var b in f.Breaches)
            {
                ordered = ordered || b.Pending != "";
            }
            if (reached && !(held.Contains(f.Id) && ordered))
            {
                // A skipped pair never showed which ordered doors its path crosses, so it cannot release a
                // latch: the facility stays held, without a deficit, until a census paths every reachable pair.
                if (held.Contains(f.Id) && unpathed)
                {
                    latched.Add(f.Id);
                }
                continue;
            }
            // A facility already reachable waits for its ordered door only; no second breach is proposed.
            var pending = 0;
            var breaches = new List<RouteBreach>();
            foreach (var b in f.Breaches)
            {
                if (b.Pending != "")
                {
                    pending++;
                    continue;
                }
                if (!reached)
                {
                    breaches.Add(b);
                }
            }
            deficits.Add(new RouteDeficit
            {
                Facility = f,
                Pending = pending,
                Breaches = EquatableArray.From(breaches.OrderBy(b => b.Distance)),
            });
            latched.Add(f.Id);
        }
        var sorted = deficits
            .OrderBy(d => RouteKindOrder(d.Facility.Kind))
            .ThenBy(d => d.Facility.Id, StringComparer.Ordinal)
            .ToList();
        return new RoutesReview
        {
            Known = true,
            Deficits = EquatableArray.From(sorted),
            Latched = EquatableArray.From(latched.Order(StringComparer.Ordinal)),
            Active = sorted.Count > 0,
        };
    }

    /// <summary>
    /// Resolves the first deficit that has a breach to open. A facility whose breaches are all ordered already
    /// waits for them; one with no breach at all (no player wall borders its room, or no colonist stands
    /// anywhere outside it) is reported so the deficit stays visible.
    /// </summary>
    public static RoutesProposal SelectRoutesMethod(RoutesReview review, RoutesFacts facts, RoutesPolicy p)
    {
        if (!p.Valid())
        {
            throw new DomainException("invalid routes policy");
        }
        if (!review.Active)
        {
            return new RoutesProposal { Method = RoutesMethod.NoMethod };
        }
        if (!facts.DoorAvailable.TryGetValue(out var available))
        {
            return new RoutesProposal { Method = RoutesMethod.Unknown };
        }
        if (!available)
        {
            return new RoutesProposal { Method = RoutesMethod.DoorUnavailable };
        }
        var deferred = RoutesMethod.None;
        foreach (var d in review.Deficits)
        {
            if (d.Breaches.Count == 0)
            {
                var next = d.Pending > 0 ? RoutesMethod.DoorPending : RoutesMethod.NoBreach;
                if (deferred == RoutesMethod.None || deferred == RoutesMethod.NoBreach)
                {
                    deferred = next;
                }
                continue;
            }
            var cells = d.Breaches.Take(Math.Min(d.Breaches.Count, p.MaxBreaches)).Select(b => b.Cell).ToList();
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{d.Facility.Id}/{p.Door}/{p.Stuff}/{cells[0].X},{cells[0].Z}"));
            return new RoutesProposal
            {
                Method = RoutesMethod.Build,
                Key = new MethodId("routes-" + Convert.ToHexStringLower(digest.AsSpan(0, 12))),
                Facility = d.Facility.Id,
                Definition = p.Door,
                Stuff = p.Stuff,
                Breaches = EquatableArray.From(cells),
            };
        }
        return new RoutesProposal { Method = deferred == RoutesMethod.None ? RoutesMethod.NoMethod : deferred };
    }
}
