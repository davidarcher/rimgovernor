namespace RimGovernor.Brain.Policy;

// Room quality ranking: a Jealous colonist holds the colony's best bedroom and an Ascetic colonist the
// plainest. Both are met by swapping existing bedrooms one AssignIntent at a time: the mover takes the other
// room's bed, the native claim evicts its owner, and the sleeping planner then assigns the evicted owner a
// vacant bed (the mover's old one among them; plainest first for an ascetic, see SelectSleepingMethod).
//
// Only solo bedrooms swap: a room whose one owned humanlike bed has exactly one owner, so no move ever pairs
// non-partners or splits a couple.

/// <summary>One solo bedroom with a known impressiveness.</summary>
internal sealed record SoloBedroom
{
    public string Room { get; init; } = "";

    public string Bed { get; init; } = "";

    public PawnId Owner { get; init; }

    public double Impressiveness { get; init; }
}

/// <summary>One AssignIntent moving Pawn out of PreviousBed into Bed.</summary>
public sealed record BedroomSwap
{
    public PawnId Pawn { get; init; }

    public string Bed { get; init; } = "";

    public string PreviousBed { get; init; } = "";
}

public static partial class PolicyFunctions
{
    internal static List<SoloBedroom> SoloBedrooms(SleepingObservation obs)
    {
        if (!obs.Rooms.TryGetValue(out var rooms))
        {
            return [];
        }
        var impressiveness = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            if (room.Quality.TryGetValue(out var q))
            {
                impressiveness[room.Id] = q.Impressiveness;
            }
        }
        var owned = new Dictionary<string, List<SleepingBed>>(StringComparer.Ordinal);
        foreach (var bed in obs.Beds)
        {
            var ok = bed.Room.TryGetValue(out var room);
            bed.Humanlike.TryGetValue(out var humanlike);
            bed.Medical.TryGetValue(out var medical);
            bed.Prisoners.TryGetValue(out var prisoners);
            if (!ok || string.IsNullOrEmpty(room) || !humanlike || medical || prisoners || bed.Slaves || bed.Owners.Count == 0)
            {
                continue;
            }
            if (!owned.TryGetValue(room, out var list))
            {
                owned[room] = list = [];
            }
            list.Add(bed);
        }
        var output = new List<SoloBedroom>();
        foreach (var (room, beds) in owned)
        {
            if (!impressiveness.TryGetValue(room, out var v) || beds.Count != 1 || beds[0].Owners.Count != 1)
            {
                continue;
            }
            output.Add(new SoloBedroom { Room = room, Bed = beds[0].Id, Owner = beds[0].Owners[0], Impressiveness = v });
        }
        return output.OrderBy(r => r.Room, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The next swap the ranking wants, false when none. Jealous colonists go first: one in a solo bedroom
    /// less impressive than the best solo bedroom takes it from a non-jealous owner. Then an ascetic in a solo
    /// bedroom more impressive than the plainest takes that one from an owner neither ascetic nor jealous.
    /// Each swap strictly moves the better room to the colonist who wants it, so the ranking settles; two
    /// jealous colonists are never displaced for each other. Suites (the census ids in suites) never swap:
    /// each is its claimant's, so a Jealous colonist outdone by a suite earns a suite of their own
    /// (SuiteClaims) and an ascetic is never moved into one.
    /// </summary>
    public static (BedroomSwap Swap, bool Ok) NextBedroomSwap(
        SleepingObservation obs, IReadOnlyDictionary<PawnId, TraitEffects> traits, ISet<string> suites)
    {
        var rooms = SoloBedrooms(obs).Where(r => !suites.Contains(r.Room)).ToList();
        var accessible = new Dictionary<string, EquatableArray<PawnId>>(StringComparer.Ordinal);
        foreach (var b in obs.Beds)
        {
            accessible[b.Id] = b.AccessibleTo;
        }
        TraitEffects TraitsOf(PawnId pawn) => traits.TryGetValue(pawn, out var e) ? e : new TraitEffects();

        (BedroomSwap, bool) Pick(SoloBedroom mover, Func<double, double, bool> better, Func<TraitEffects, bool> blocked)
        {
            SoloBedroom? best = null;
            foreach (var r in rooms)
            {
                if (r.Owner == mover.Owner || blocked(TraitsOf(r.Owner)) || !ContainsPawn(accessible.GetValueOrDefault(r.Bed), mover.Owner))
                {
                    continue;
                }
                if (better(r.Impressiveness, mover.Impressiveness) && (best is null || better(r.Impressiveness, best.Impressiveness)))
                {
                    best = r;
                }
            }
            if (best is null)
            {
                return (new BedroomSwap(), false);
            }
            return (new BedroomSwap { Pawn = mover.Owner, Bed = best.Bed, PreviousBed = mover.Bed }, true);
        }

        static bool Higher(double a, double b) => a > b;
        static bool Lower(double a, double b) => a < b;
        foreach (var r in rooms)
        {
            if (TraitsOf(r.Owner).Jealous)
            {
                var (s, ok) = Pick(r, Higher, e => e.Jealous);
                if (ok)
                {
                    return (s, true);
                }
            }
        }
        foreach (var r in rooms)
        {
            var e = TraitsOf(r.Owner);
            if (e.Ascetic && !e.Jealous)
            {
                var (s, ok) = Pick(r, Lower, x => x.Ascetic || x.Jealous);
                if (ok)
                {
                    return (s, true);
                }
            }
        }
        return (new BedroomSwap(), false);
    }
}
