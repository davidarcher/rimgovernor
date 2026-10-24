using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The formation hunt the plan opened, in memory: its prey are admitted when the plan opens them (HuntRequest, which
/// makes the military/combat Need unmet), and the formation channel of admitted prey is Designated from then, so the
/// delivery credit commits it and moves the hunt group to Delivering at the first kill. It is held, not re-compared,
/// until a release trigger (<see cref="Release"/>): then the next review compares again from fresh estimates, and nothing
/// extends the hunt without that comparison. A restart forgets it; the next plan admits again.
/// A null reference stands for no admission: callers use <c>admission?.Apply(channels) ?? channels</c>.
/// </summary>
public sealed class HuntAdmission
{
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    private Held? _committed;
    private Held? _offered;

    /// <summary>A chosen formation and the hunt read it was chosen from: the gunners its estimate described then.</summary>
    private sealed record Held(HuntAssessment Assessment, HuntRoster Roster, HuntActivityRead Read);

    /// <summary>
    /// The formation channel to offer and why a held one was released (empty when none was): the one the plan opened
    /// while no release trigger holds, else the plan's own choice. <paramref name="read"/> is the current hunt read and
    /// <paramref name="hungry"/> the colonists whose hunger takes them off a hunt (<see cref="Release"/>).
    /// </summary>
    public (SupplyCandidate? Channel, string Released) Formation(SupplyCandidate? chosen, HuntPlan plan, IReadOnlyList<AcquisitionSource> sources, HuntActivityRead? read, IReadOnlyCollection<string>? hungry = null)
    {
        var released = "";
        if (_committed is { } held)
        {
            released = Release(read, sources, hungry);
            if (released.Length == 0)
            {
                _offered = null;
                return (HuntPlanningRules.HuntRosterChannel(held.Assessment, held.Roster, sources), "");
            }
            _committed = null;
            _admitted.Clear();
        }
        _offered = plan.Selection?.Selected is { Roster: { } roster } selected && chosen is not null && read is not null ? new(selected, roster, read) : null;
        return (chosen, released);
    }

    /// <summary>
    /// Why the held formation is released now, or empty while it stands (or nothing is held). The triggers: a rostered
    /// gunner left the hunt read, is downed or in a mental state, or is among <paramref name="hungry"/> (a drafted pawn
    /// does not eat, so his hunger breaks the hunt); an admitted animal is gone from the sources, downed, out of the
    /// observed window, or a new animal joined its group; or a gunner is no longer the pawn his estimate was made from
    /// (<see cref="StillDescribesGunner"/>). An unknown hunt read decides nothing about the gunners but their hunger: the
    /// prey rules still apply. A drafted gunner is not a trigger: the hunt's own draft and the player's read the same.
    /// </summary>
    public string Release(HuntActivityRead? read, IReadOnlyList<AcquisitionSource> sources, IReadOnlyCollection<string>? hungry = null)
    {
        if (_committed is not { } held)
        {
            return "";
        }
        foreach (var id in held.Roster.Prey)
        {
            var source = sources.FirstOrDefault(s => s.Id == id && s.Hunt && s.Food);
            if (source is null)
            {
                return "prey_gone:" + id;
            }
            if (source.Downed)
            {
                return "prey_downed:" + id;
            }
        }
        foreach (var id in held.Roster.Gunners)
        {
            if (hungry?.Contains(id) == true)
            {
                return "gunner_hungry:" + id;
            }
        }
        if (read is null)
        {
            return "";
        }
        foreach (var id in held.Roster.Prey)
        {
            if (read.Prey.All(p => p.Pawn != id))
            {
                return "prey_unobserved:" + id;
            }
        }
        var prey = held.Roster.Prey.Select(id => read.Prey.First(p => p.Pawn == id)).ToArray();
        foreach (var animal in read.Prey)
        {
            if (!animal.Downed && !animal.Mental && !held.Roster.Prey.Contains(animal.Pawn) && prey.Any(p => HuntAlternatives.Grouped(animal, p)))
            {
                return "prey_joined:" + animal.Pawn;
            }
        }
        foreach (var id in held.Roster.Gunners)
        {
            var gunner = read.Hunters.FirstOrDefault(h => h.Pawn == id);
            if (gunner is null)
            {
                return "gunner_unobserved:" + id;
            }
            if (gunner.Downed)
            {
                return "gunner_downed:" + id;
            }
            if (gunner.Mental)
            {
                return "gunner_mental:" + id;
            }
            if (!StillDescribesGunner(held.Read, read, id))
            {
                return "gunner_changed:" + id + ":" + GunnerDifferences(held.Read, read, id);
            }
        }
        return "";
    }

    /// <summary>Biological age grows every tick, so a read still describes a gunner while he has aged less than this many years.</summary>
    internal const double AgeTolerance = 0.25;

    /// <summary>
    /// Whether the gunner is still the pawn the estimate described, apart from where the hunt has walked him and that it
    /// drafted him (nor where his own corpse would be stored, nor his meat): his kind, xenotype, weapon and its range, apparel (by def, stuff and quality, not wear), traits, health set (each hediff by definition and part, not its
    /// severity) and missing parts, under the same weather. His skill levels are not judged either: they move with every
    /// shot and would release the hunt that moved them (#3525); nor are the shot and body numbers derived from skill,
    /// pain and injury severity. An executing formation moves and drafts its gunners, so neither stales it; a downed or
    /// breaking gunner is the formation's own release.
    /// </summary>
    internal static bool StillDescribesGunner(HuntActivityRead made, HuntActivityRead now, string gunner) =>
        made.WeatherAccuracy == now.WeatherAccuracy && SameGunner(Unplaced(made.Hunters.FirstOrDefault(h => h.Pawn == gunner)), Unplaced(now.Hunters.FirstOrDefault(h => h.Pawn == gunner)));

    /// <summary>What no longer describes the gunner (<see cref="StillDescribesGunner"/>): the weather, or the fields of <see cref="Differences"/>.</summary>
    internal static string GunnerDifferences(HuntActivityRead made, HuntActivityRead now, string gunner)
    {
        if (made.WeatherAccuracy != now.WeatherAccuracy) return "weather";
        var a = Unplaced(made.Hunters.FirstOrDefault(h => h.Pawn == gunner));
        var n = Unplaced(now.Hunters.FirstOrDefault(h => h.Pawn == gunner));
        return a is null || n is null ? "unobserved" : Differences(a, n);
    }

    /// <summary>
    /// What differs between two reads of one gunner, apart from where he stands, for the release reason that names a
    /// changed gunner: the field names, and for health each hediff gained (<c>+</c>) or lost (<c>-</c>), by definition
    /// and part. Skill levels and hediff severities are not differences (<see cref="StillDescribesGunner"/>).
    /// </summary>
    internal static string Differences(HuntActorRead a, HuntActorRead n)
    {
        var fields = new List<string>();
        if (a.Kind != n.Kind) fields.Add("kind");
        if (a.Xenotype != n.Xenotype) fields.Add("xenotype");
        if (Math.Abs(a.Age - n.Age) >= AgeTolerance) fields.Add("age");
        if (Gear(a.Weapon) != Gear(n.Weapon)) fields.Add("weapon");
        if (a.Gun?.Range != n.Gun?.Range) fields.Add("weapon_range");
        if (Outfit(a) != Outfit(n)) fields.Add("apparel");
        if (Character(a) != Character(n)) fields.Add("traits");
        var before = HealthSet(a);
        var after = HealthSet(n);
        if (!before.SetEquals(after))
        {
            fields.Add("health(" + string.Join(",", before.Union(after).Order(StringComparer.Ordinal).Select(h => before.Contains(h) ? after.Contains(h) ? "" : "-" + h : "+" + h).Where(s => s.Length > 0)) + ")");
        }
        if (a.MissingParts != n.MissingParts) fields.Add("missing_parts");
        if (a.Available != n.Available) fields.Add("available");
        if (a.Hauls != n.Hauls) fields.Add("hauls");
        if (a.Downed != n.Downed) fields.Add("downed");
        if (a.Mental != n.Mental) fields.Add("mental");
        return string.Join(",", fields);
    }

    private static HuntActorRead? Unplaced(HuntActorRead? a) => a is null ? null : a with { Available = false, Downed = false, Mental = false };

    // His age is judged against its own tolerance; his skill levels, his hediffs' severities and the shot and body numbers
    // derived from them move with every shot and every hour of healing, so none is a change: the health set is compared by hediff and part.
    private static bool SameGunner(HuntActorRead? x, HuntActorRead? y) => x is not null && y is not null
        && Math.Abs(x.Age - y.Age) < AgeTolerance
        && HealthSet(x).SetEquals(HealthSet(y))
        && Gear(x.Weapon) == Gear(y.Weapon) && Outfit(x) == Outfit(y) && Character(x) == Character(y)
        && x.Gun?.Range == y.Gun?.Range && Judged(x) == Judged(y);

    private static HuntActorRead Judged(HuntActorRead a) => a with { Location = default, Age = 0, Skills = [], Health = [], Gun = null, Body = null, Weapon = null, Apparel = [], Traits = [], CorpseStore = new HuntCorpseStore.NoStore(), Meat = 0, MeatNutrition = 0 };

    private static string Gear(HuntGearRead? g) => g is null ? "" : g.Definition + "/" + g.Stuff + "/" + g.Quality;

    private static string Outfit(HuntActorRead a) => string.Join(",", a.Apparel.Select(Gear).Order(StringComparer.Ordinal));

    private static string Character(HuntActorRead a) => string.Join(",", a.Traits.Select(t => t.Definition + ":" + t.Degree).Order(StringComparer.Ordinal));

    private static HashSet<string> HealthSet(HuntActorRead a) =>
        a.Health.Select(h => h.Definition + (h.Part.Length == 0 ? "" : "/" + h.Part)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Records the prey the plan opened; call it with HuntRequest of each plan.</summary>
    public void Admit(IEnumerable<Domain.PawnId> prey)
    {
        var opened = prey.ToArray();
        foreach (var id in opened)
        {
            _admitted.Add(id.Value);
        }
        if (_offered is { } offered && opened.Any(id => offered.Roster.Prey.Contains(id.Value)))
        {
            _committed = offered;
        }
        _offered = null;
    }

    /// <summary>Marks the formation channels that hold admitted prey Designated; lone channels and channels of other prey are returned as they are.</summary>
    public IReadOnlyList<SupplyCandidate> Apply(IReadOnlyList<SupplyCandidate> channels)
    {
        if (_admitted.Count == 0)
        {
            return channels;
        }
        var @out = new List<SupplyCandidate>(channels);
        for (var i = 0; i < @out.Count; i++)
        {
            var c = @out[i];
            if (c.Kind == CandidateKind.Hunt && c.Mode() == HuntMode.Formation && c.Prey.Any(_admitted.Contains))
            {
                @out[i] = c with { State = CandidateRules.FoodState(Known(false), Known(true)) };
            }
        }
        return @out;
    }
}
