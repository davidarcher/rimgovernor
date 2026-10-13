namespace RimGovernor.Brain.Policy;

/// <summary>
/// A dining or recreation facility. RoomId names the proper indoor room hosting the facility, as the native
/// room census identifies it; empty when the facility stands outdoors.
/// </summary>
public sealed record ComfortFacility
{
    public string Id { get; init; } = "";

    public string RoomId { get; init; } = "";

    public string Kind { get; init; } = "";

    public EquatableArray<PawnId> AccessibleTo { get; init; }

    public EquatableArray<PawnId> Users { get; init; }
}

public sealed record DiningSurface
{
    public string Id { get; init; } = "";

    public string RoomId { get; init; } = "";

    public EquatableArray<Cell> Adjacent { get; init; }
}

public sealed partial record ComfortObservation
{
    public EquatableArray<PawnId> People { get; init; }

    public EquatableArray<DiningSurface> Surfaces { get; init; }

    public EquatableArray<ComfortFacility> Dining { get; init; }

    public EquatableArray<ComfortFacility> Recreation { get; init; }

    public RecreationCensus? Joy { get; init; }

    /// <summary>
    /// The joy building the first recreation facility is (DefinitionCatalog.RecreationFoothold). WatchBuildings
    /// are the defs whose placement needs watch cells every colonist can reach. Both come from the catalog rows.
    /// </summary>
    public string RecreationFoothold { get; init; } = "";

    public EquatableArray<string> WatchBuildings { get; init; }

    /// <summary>The dining chair, table and recreation foothold the catalog rows name; RecreationFoothold is its pin.</summary>
    public DiningFurniture Furniture { get; init; } = new();

    /// <summary>Reports whether <paramref name="method"/> builds the recreation foothold.</summary>
    public bool IsFoothold(ComfortMethod method) => RecreationFoothold.Length != 0 && method.Value == RecreationFoothold;

    /// <summary>The method that builds the recreation foothold; a census without one cannot name the build.</summary>
    internal ComfortMethod FootholdMethod()
    {
        DomainException.Require(RecreationFoothold.Length != 0, "comfort census names no recreation foothold");
        return new ComfortMethod(RecreationFoothold);
    }

    public void Validate()
    {
        ValidateJoy();
        var people = new HashSet<PawnId>();
        foreach (var id in People)
        {
            DomainException.Require(FoodId(id.Value) && people.Add(id), "invalid comfort pawn");
        }
        var surfaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in Surfaces)
        {
            DomainException.Require(FoodId(s.Id) && surfaces.Add(s.Id), "invalid dining surface");
            var seen = new HashSet<Cell>();
            foreach (var c in s.Adjacent)
            {
                DomainException.Require(!(c.X < 0 || c.Z < 0 || c.X >= 4096 || c.Z >= 4096 || !seen.Add(c)), "invalid dining adjacency");
            }
        }
        foreach (var facilities in new[] { Dining, Recreation })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in facilities)
            {
                DomainException.Require(FoodId(f.Id) && seen.Add(f.Id), "invalid comfort facility");
                foreach (var ids in new[] { f.AccessibleTo, f.Users })
                {
                    var found = new HashSet<PawnId>();
                    foreach (var id in ids)
                    {
                        DomainException.Require(people.Contains(id) && found.Add(id), "invalid comfort facility pawn");
                    }
                }
            }
        }
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
                !(proof.Tick.Value < 0 || (facility.Length == 0 && proof.Tick.Value != 0) || (facility.Length != 0 && !FoodId(facility))),
                "invalid comfort use history");
        }
    }
}

public enum ComfortNeed
{
    Unknown,
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

    public int MissingDining { get; init; }

    public int MissingRecreation { get; init; }

    public int People { get; init; }

    public Fact<bool> Recovered() =>
        Dining == ComfortNeed.Unknown || Recreation == ComfortNeed.Unknown
            ? Unknown<bool>()
            : Known(Dining == ComfortNeed.Recovered && Recreation == ComfortNeed.Recovered);

    public Fact<double> Deficit()
    {
        if (Dining == ComfortNeed.Unknown || Recreation == ComfortNeed.Unknown)
        {
            return Unknown<double>();
        }
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
        return Known(value);
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

public static partial class PolicyFunctions
{
    public static ComfortReview ReviewComfort(Fact<ComfortObservation> observed, ComfortHistory previous, Tick tick)
    {
        var r = new ComfortReview { History = previous, Dining = ComfortNeed.Unknown, Recreation = ComfortNeed.Unknown };
        previous.Validate();
        DomainException.Require(!(tick.Value < 0 || previous.Dining.Tick > tick || previous.Recreation.Tick > tick), "comfort use is from a future tick");
        if (!observed.TryGetValue(out var v))
        {
            return r;
        }
        v.Validate();
        r = r with { People = v.People.Count };
        (ComfortNeed Need, int Missing, ComfortUse Proof) Assess(IReadOnlyList<ComfortFacility> facilities, ComfortUse proof)
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
            var missing = v.People.Count - available.Count;
            if (missing > 0)
            {
                return (ComfortNeed.Capacity, missing, proof);
            }
            if (v.People.Count > 0 && !seenUse)
            {
                return (ComfortNeed.UseNeeded, 0, proof);
            }
            return (ComfortNeed.Recovered, 0, proof);
        }
        var (dining, missingDining, diningProof) = Assess(v.Dining, r.History.Dining);
        var (recreation, missingRecreation, recreationProof) = Assess(v.Recreation, r.History.Recreation);
        return r with
        {
            Dining = dining,
            MissingDining = missingDining,
            Recreation = recreation,
            MissingRecreation = missingRecreation,
            History = new ComfortHistory(diningProof, recreationProof),
        };
    }

    public static ComfortMethod SelectComfortMethod(ComfortObservation v, ComfortReview r)
    {
        v.Validate();
        DomainException.Require(r.Dining != ComfortNeed.Unknown && r.Recreation != ComfortNeed.Unknown, "comfort evidence unavailable");
        if (r.Dining == ComfortNeed.Capacity)
        {
            if (v.Dining.Count > 0)
            {
                return ComfortMethod.AccessBlocked;
            }
            if (v.Surfaces.Count == 0)
            {
                return v.Furniture.TableMethod();
            }
            return v.Furniture.ChairMethod();
        }
        if (r.Recreation == ComfortNeed.Capacity)
        {
            if (v.Recreation.Count > 0)
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
        var dining = Facility(RoomRole.DiningRoom);
        var recreation = Facility(RoomRole.RecRoom);
        bool Hosted(FacilityRequirement f, string roomId)
        {
            var (room, roomFound) = rooms.Room(roomId);
            if (roomId.Length == 0 || !roomFound)
            {
                return false;
            }
            return room.Role.TryGetValue(out var role) && f.Hosts(role);
        }
        return new ComfortObservation
        {
            People = v.People,
            RecreationFoothold = v.RecreationFoothold,
            WatchBuildings = v.WatchBuildings,
            Furniture = v.Furniture,
            Surfaces = EquatableArray.From(v.Surfaces.Where(s => Hosted(dining, s.RoomId))),
            Dining = EquatableArray.From(v.Dining.Where(f => Hosted(dining, f.RoomId))),
            Recreation = EquatableArray.From(v.Recreation.Where(f => Hosted(recreation, f.RoomId))),
        };
    }
}
