using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

// The layout/routes Need keeps every colony facility reachable by the colonists who use it. It reasons only from
// observed travel: the native census answers, for each bed, work bench, storage, dining surface and defence
// emplacement, whether each mobile colonist can reach it from where that colonist stands right now (the
// game's own pathing with the doors that colonist may open) and what the game's chosen path costs. A facility
// no colonist reaches is a deficit while the census reads it unreachable. The
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
        FoodForecastRules.FoodId(Door) && FoodForecastRules.FoodId(Stuff) && MaxBreaches >= 1 && MaxBreaches <= 16;
}

/// <summary>Pure validation of the native routes census.</summary>
public static partial class RoutesRules
{
    public static void ValidateRoutes(RimGovernor.Contracts.Wire.Observations.RoutesFacts census)
    {
        var pawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in census.PawnIds)
        {
            if (!FoodForecastRules.FoodId(id) || !pawns.Add(id))
            {
                throw new DomainException("invalid routes pawn");
            }
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in census.Facilities)
        {
            if (!FoodForecastRules.FoodId(f.Id) || !ids.Add(f.Id))
            {
                throw new DomainException("invalid routes facility");
            }
            if (f.Room is { } room && !FoodForecastRules.FoodId(room))
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
                if (t is RouteTravel.Measured { Cost: < 0 } or RouteTravel.Measured { Cells: < 0 })
                {
                    throw new DomainException("invalid routes travel path");
                }
            }
            var breachCells = new HashSet<Cell>();
            foreach (var b in f.Breaches)
            {
                if (!FoodForecastRules.FoodId(b.Edifice) || (b.Pending is { } pending && !FoodForecastRules.FoodId(pending)) || b.Distance < 0 || !breachCells.Add(b.Cell))
                {
                    throw new DomainException("invalid routes breach");
                }
            }
        }
        var trafficKeys = new HashSet<(TrafficLayer, Cell)>();
        foreach (var t in census.Traffic)
        {
            if (!FoodForecastRules.FoodId(t.Terrain) || !trafficKeys.Add((t.Layer, t.Cell)) || (t.Pending is { } pending && !FoodForecastRules.FoodId(pending)))
            {
                throw new DomainException("invalid traffic cell");
            }
        }
    }
}

public static class RouteTravelExtensions
{
    extension(RouteTravel t)
    {
        /// <summary>Reports a pair the game says the colonist can reach, whether or not its path was measured.</summary>
        public bool Reachable => t is not RouteTravel.Unreachable;
    }
}

/// <summary>One facility no mobile colonist can reach.</summary>
public sealed record RouteDeficit
{
    public required RouteFacility Facility { get; init; }

    /// <summary>The facility's unordered breach cells nearest first.</summary>
    public EquatableArray<RouteBreach> Breaches { get; init; }

    /// <summary>Counts the breach cells with a door already ordered.</summary>
    public int Pending { get; init; }
}

/// <summary>
/// One review of the routes census: Deficits lists every unreachable facility by kind precedence (storage and
/// stockpiles first, then benches, dining, beds, defence) and then id.
/// </summary>
public sealed record RoutesReview
{
    public bool Active { get; init; }

    public EquatableArray<RouteDeficit> Deficits { get; init; }
}

public enum RoutesMethod
{
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
    public required RoutesMethod Method { get; init; }

    public MethodId Key { get; init; }

    /// <summary>The door definition.</summary>
    public string Definition { get; init; } = "";

    public string Stuff { get; init; } = "";

    /// <summary>The candidate wall cells nearest first, each to be validated by a native placement preview before one is admitted.</summary>
    public EquatableArray<Cell> Breaches { get; init; }
}

/// <summary>What SelectRoutesMethod needs beyond the review: the planning census row for the door definition.</summary>
public sealed record RoutesFacts
{
    public bool DoorAvailable { get; init; }
}

public static partial class RoutesRules
{
    public static RoutesPolicy DefaultRoutesPolicy() => new() { Door = "Door", Stuff = "WoodLog", MaxBreaches = 4 };

    private static int RouteKindOrder(RouteFacilityKind kind)
    {
        switch (kind)
        {
            case RouteFacilityKind.Stockpile:
            case RouteFacilityKind.Storage:
                return 0;
            case RouteFacilityKind.Bench:
                return 1;
            case RouteFacilityKind.Dining:
                return 2;
            case RouteFacilityKind.Bed:
                return 3;
            case RouteFacilityKind.Defense:
                return 4;
        }
        throw new DomainException($"route facility kind {(int)kind} is not a RouteFacilityKind member");
    }

    /// <summary>
    /// Measures every facility against the colonists' observed reachability. A facility is deficient when the
    /// census lists at least one mobile colonist and none of them reaches it; a colony with no mobile colonist
    /// cannot declare anything unreachable. There is no hysteresis (#3231 decision 16): reachability is the
    /// game's own answer, read fresh every review. A door frame is walkable, so a facility reads reachable once its
    /// ordered door is framed; the open door Method then continues on its own and nothing holds the facility.
    /// </summary>
    public static RoutesReview ReviewRoutes(RimGovernor.Contracts.Wire.Observations.RoutesFacts v, RoutesPolicy p)
    {
        if (!p.Valid())
        {
            throw new DomainException("invalid routes policy");
        }
        ValidateRoutes(v);
        if (v.PawnIds.Count == 0)
        {
            return new RoutesReview();
        }
        var deficits = new List<RouteDeficit>();
        foreach (var f in v.Facilities)
        {
            if (f.Travel.Any(t => t.Reachable))
            {
                continue;
            }
            var pending = 0;
            var breaches = new List<RouteBreach>();
            foreach (var b in f.Breaches)
            {
                if (b.Pending is not null)
                {
                    pending++;
                    continue;
                }
                breaches.Add(b);
            }
            deficits.Add(new RouteDeficit
            {
                Facility = f,
                Pending = pending,
                Breaches = EquatableArray.From(breaches.OrderBy(b => b.Distance)),
            });
        }
        var sorted = deficits
            .OrderBy(d => RouteKindOrder(d.Facility.Kind))
            .ThenBy(d => d.Facility.Id, StringComparer.Ordinal)
            .ToList();
        return new RoutesReview
        {
            Deficits = EquatableArray.From(sorted),
            Active = sorted.Count > 0,
        };
    }

    /// <summary>
    /// Resolves the first deficit that has a breach to open. A facility whose breaches are all ordered already
    /// waits for them; one with no breach at all (no player wall borders its room, or no colonist stands
    /// anywhere outside it) is reported so the deficit stays visible. The door's availability is a required fact:
    /// a deficit with the door definition unread raises <see cref="RequiredFactException"/>.
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
        var available = facts.DoorAvailable;
        if (!available)
        {
            return new RoutesProposal { Method = RoutesMethod.DoorUnavailable };
        }
        RoutesMethod? deferred = null;
        foreach (var d in review.Deficits)
        {
            if (d.Breaches.Count == 0)
            {
                var next = d.Pending > 0 ? RoutesMethod.DoorPending : RoutesMethod.NoBreach;
                if (deferred is null or RoutesMethod.NoBreach)
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
                Definition = p.Door,
                Stuff = p.Stuff,
                Breaches = EquatableArray.From(cells),
            };
        }
        return new RoutesProposal { Method = deferred ?? RoutesMethod.NoMethod };
    }
}
