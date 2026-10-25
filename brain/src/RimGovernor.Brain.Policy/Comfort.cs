using ComfortFacts = RimGovernor.Brain.GameFacts.Observations.ComfortFacts;
using ComfortHost = RimGovernor.Brain.GameFacts.Observations.ComfortHost;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The native comfort census (<see cref="Census"/>) with the catalog rows the comfort rules plan from.
/// </summary>
public sealed record ComfortObservation
{
    public required ComfortFacts Census { get; init; }

    /// <summary>
    /// The joy building the first recreation facility is (DefinitionCatalog.RecreationFoothold). WatchBuildings
    /// are the defs whose placement needs watch cells every colonist can reach. Both come from the catalog rows.
    /// </summary>
    public required string RecreationFoothold { get; init; }

    public required EquatableArray<string> WatchBuildings { get; init; }

    /// <summary>The dining chair, table and recreation foothold the catalog rows name; RecreationFoothold is its pin.</summary>
    public required DiningFurniture Furniture { get; init; }

    /// <summary>The joy buildings the catalog can build, in its preference order (JoyBuildings).</summary>
    public required EquatableArray<JoyBuildingMethod> JoyMethods { get; init; }

    /// <summary>Reports whether <paramref name="method"/> builds the recreation foothold.</summary>
    public bool IsFoothold(ComfortMethod method) => RecreationFoothold.Length != 0 && method.Value == RecreationFoothold;

    /// <summary>The method that builds the recreation foothold; a census without one cannot name the build.</summary>
    internal ComfortMethod FootholdMethod()
    {
        DomainException.Require(RecreationFoothold.Length != 0, "comfort census names no recreation foothold");
        return new ComfortMethod(RecreationFoothold);
    }
}
public readonly record struct ComfortUse(string Facility, Tick Tick);

public readonly record struct ComfortHistory(ComfortUse Dining, ComfortUse Recreation)
{
    public void Validate()
    {
        foreach (var proof in new[] { Dining, Recreation })
        {
            var facility = proof.Facility ?? "";
            DomainException.Require(
                !(proof.Tick.Value < 0 || (facility.Length == 0 && proof.Tick.Value != 0) || (facility.Length != 0 && !FoodForecastRules.FoodId(facility))),
                "invalid comfort use history");
        }
    }
}

public enum ComfortNeed
{
    Capacity,
    [WireName("use")]
    UseNeeded,
    Recovered,
}

public sealed record ComfortReview
{
    public ComfortHistory History { get; init; }

    public ComfortNeed Dining { get; init; }

    public ComfortNeed Recreation { get; init; }

    public bool Recovered() =>
        Dining == ComfortNeed.Recovered && Recreation == ComfortNeed.Recovered;

    /// <summary>The unmet share: half per short kind. Meaningful only once <see cref="Recovered"/> has read both kinds.</summary>
    public double Deficit()
    {
        // The development ranking counts each unmet facility kind equally, whether it needs capacity or proof of use.
        var value = 0.0;
        if (Dining != ComfortNeed.Recovered)
        {
            value += .5;
        }
        if (Recreation != ComfortNeed.Recovered)
        {
            value += .5;
        }
        return value;
    }
}

/// <summary>A comfort method: one of the named constants, or the def of the building the recreation foothold names.</summary>
public readonly record struct ComfortMethod(string Value)
{
    public static readonly ComfortMethod NoMethod = new("recovered");
    public static readonly ComfortMethod Wait = new("wait_for_native_use");
    public static readonly ComfortMethod AccessBlocked = new("preserve_existing_facility_access");

    public override string ToString() => Value ?? "";
}

public static class ComfortRules
{
    public static ComfortReview ReviewComfort(ComfortObservation v, ComfortHistory previous, Tick tick)
    {
        var r = new ComfortReview { History = previous };
        previous.Validate();
        DomainException.Require(!(tick.Value < 0 || previous.Dining.Tick > tick || previous.Recreation.Tick > tick), "comfort use is from a future tick");
        (ComfortNeed Need, ComfortUse Proof) Assess(IEnumerable<(string Id, EquatableArray<PawnId> AccessibleTo, EquatableArray<PawnId> Users)> facilities, ComfortUse proof)
        {
            var available = new HashSet<PawnId>();
            // A stable tie-break makes simultaneous observed uses replayable.
            foreach (var f in facilities.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var access = new HashSet<PawnId>();
                foreach (var id in f.AccessibleTo)
                {
                    access.Add(id);
                    available.Add(id);
                }
                foreach (var id in f.Users)
                {
                    if (access.Contains(id))
                    {
                        proof = new ComfortUse(f.Id, tick);
                        break;
                    }
                }
            }
            var seenUse = false;
            foreach (var f in facilities)
            {
                seenUse = seenUse || (f.Id == proof.Facility && f.AccessibleTo.Count > 0);
            }
            if (v.Census.People.Count > available.Count)
            {
                return (ComfortNeed.Capacity, proof);
            }
            if (v.Census.People.Count > 0 && !seenUse)
            {
                return (ComfortNeed.UseNeeded, proof);
            }
            return (ComfortNeed.Recovered, proof);
        }
        var (dining, diningProof) = Assess(v.Census.Dining.Select(f => (f.Id, f.AccessibleTo, f.Users)), r.History.Dining);
        var (recreation, recreationProof) = Assess(v.Census.Recreation.Select(f => (f.Id, f.AccessibleTo, f.Users)), r.History.Recreation);
        return r with
        {
            Dining = dining,
            Recreation = recreation,
            History = new ComfortHistory(diningProof, recreationProof),
        };
    }

    public static ComfortMethod SelectComfortMethod(ComfortObservation v, ComfortReview r)
    {
        if (r.Dining == ComfortNeed.Capacity)
        {
            if (v.Census.Dining.Count > 0)
            {
                return ComfortMethod.AccessBlocked;
            }
            if (v.Census.Surfaces.Count == 0)
            {
                return v.Furniture.TableMethod();
            }
            return v.Furniture.ChairMethod();
        }
        if (r.Recreation == ComfortNeed.Capacity)
        {
            if (v.Census.Recreation.Count > 0)
            {
                return ComfortMethod.AccessBlocked;
            }
            return v.FootholdMethod();
        }
        if (r.Dining == ComfortNeed.UseNeeded || r.Recreation == ComfortNeed.UseNeeded)
        {
            return ComfortMethod.Wait;
        }
        return ComfortMethod.NoMethod;
    }

    /// <summary>
    /// Keeps only the facilities and surfaces standing in a room whose native role the facility catalog lets
    /// host that function. A table in a barracks seats nobody for this purpose: the game scores that room a
    /// Barracks, so it never becomes the dining room the colony is missing. Unknown rooms drop the facility
    /// rather than certify it.
    /// </summary>
    public static ComfortObservation HostedComfort(ComfortObservation v, RoomObservation rooms)
    {
        var dining = RoomsRules.Facility(RoomRole.DiningRoom);
        var recreation = RoomsRules.Facility(RoomRole.RecRoom);
        bool Hosted(FacilityRequirement f, string roomId)
        {
            var (room, roomFound) = rooms.Room(roomId);
            if (roomId.Length == 0 || !roomFound)
            {
                return false;
            }
            return f.Hosts(room!.Role);
        }
        return v with
        {
            Census = v.Census with
            {
                Surfaces = EquatableArray.From(v.Census.Surfaces.Where(s => Hosted(dining, s.Room))),
                Dining = EquatableArray.From(v.Census.Dining.Where(f => Hosted(dining, f.Room))),
                Recreation = EquatableArray.From(v.Census.Recreation.Where(f => f.Host is ComfortHost.Indoors { Room: var room } && Hosted(recreation, room))),
            },
        };
    }
}
