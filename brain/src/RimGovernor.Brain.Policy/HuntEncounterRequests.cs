using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the runtime knows about the hunt comparison: the read every scene of it was built from (<see cref="Anchor"/>, null
/// before the first ask), the answers built from that read (estimates and refusals, keyed by scene) and the scenes still being
/// predicted. Policy reads this and judges whether the anchor still describes the observed colony; it never calls the service.
/// </summary>
public sealed record HuntEstimateView(HuntActivityRead? Anchor, IReadOnlyDictionary<string, HuntEstimate> Estimates, IReadOnlyDictionary<string, string> Refusals, IReadOnlySet<string> Pending)
{
    public static readonly HuntEstimateView None = new(null, new Dictionary<string, HuntEstimate>(), new Dictionary<string, string>(), new HashSet<string>());

    /// <summary>Names which rosters are answered; a plan built over one revision is stale once another arrives.</summary>
    public string Revision => string.Join(',', Estimates.Keys.Order(StringComparer.Ordinal)) + "|" + string.Join(',', Refusals.Keys.Order(StringComparer.Ordinal));
}

/// <summary>The runtime side of the hunt comparison: asks the prediction service asynchronously and remembers what it answered.</summary>
public interface IHuntEstimates
{
    /// <summary>The current comparison: its anchor, the answers built from it that are within their lifetime at this tick, and the scenes still being predicted.</summary>
    HuntEstimateView Current(long tick);

    /// <summary>
    /// Queues the rosters' scenes, each built from <paramref name="anchor"/>, skipping those answered or pending; never blocks.
    /// An anchor other than the current comparison's starts a new comparison and forgets the old one's answers. Returns how many were newly queued.
    /// </summary>
    int Request(HuntActivityRead anchor, IEnumerable<HuntRoster> rosters, long tick);
}

/// <summary>Builds the stated hypothetical scene a roster is predicted in, from the observed hunt read.</summary>
public static class HuntEncounterRequests
{
    public const string Tactic = "native-hunt/timed-departure";
    /// <summary>The encounter horizon, 2.4 game hours: the window the qualification protocol measured.</summary>
    public static readonly int Horizon = (int)(GameTime.TicksPerHour * 2.4);
    public const int Samples = 16, SeedBase = 2000001;
    /// <summary>A comparison's anchor still describes a read while each rostered gunner has moved no more than this many cells from it; prey are judged by presence and group, never by where they stand.</summary>
    public const int MoveTolerance = 8;
    /// <summary>Biological age grows every tick, so an anchor still describes a read while the gunners have aged less than this many years.</summary>
    public const double AgeTolerance = 0.25;
    /// <summary>An estimate is reused for at most one game day.</summary>
    public const long Lifetime = GameTime.TicksPerDay;

    public static (EncounterRequest? Request, string Refusal) Build(HuntActivityRead read, HuntRoster roster, long tick)
    {
        if (!read.Known || read.Scene is not { } scene) return (null, "scene_unobserved");
        var actors = new List<EncounterActor>();
        var prey = roster.Prey.Select(id => read.Prey.FirstOrDefault(p => p.Pawn == id)).ToArray();
        if (prey.Length == 0 || prey.Any(p => p is null)) return (null, "prey_unobserved");
        foreach (var id in roster.Gunners)
        {
            var hunter = read.Hunters.FirstOrDefault(h => h.Pawn == id);
            if (hunter is null) return (null, "hunter_unobserved");
            if (hunter.Weapon is null) return (null, "hunter_unarmed");
            if (hunter.Xenotype.Length == 0) return (null, "hunter_xenotype_unobserved");
            var target = prey.OrderBy(p => Math.Abs(p!.Location.X - hunter.Location.X) + Math.Abs(p.Location.Z - hunter.Location.Z)).ThenBy(p => p!.Pawn, StringComparer.Ordinal).First()!;
            var actor = Actor(hunter, false, scene, target.Pawn);
            // A gunner-retriever leaves the encounter at the tick the first stage measured; a hauler is not in the scene at all.
            if (roster.GunnerRetriever && roster.Retriever == id)
            {
                if (roster.DepartureTick <= 0 || roster.DepartureTick > Horizon) return (null, "departure_outside_horizon");
                actor = actor with { Departure = new(roster.DepartureTick, EncounterDepartureKind.Leave) };
            }
            actors.Add(actor);
        }
        actors.AddRange(prey.Select(p => Actor(p!, true, scene, "")));
        // Native names a herd by the animals near the prey, but the request holds only the roster's prey: a herd with one member in it is a lone animal, which states none.
        var herds = actors.Where(a => a.Herd.Length > 0).GroupBy(a => a.Herd).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        if (herds.Count > 0) actors = actors.Select(a => herds.Contains(a.Herd) ? a with { Herd = "" } : a).ToList();
        if (actors.Any(a => a.X < 0 || a.Z < 0 || a.X >= scene.Width || a.Z >= scene.Height)) return (null, "actor_outside_scene");
        return (new EncounterRequest
        {
            Identity = new(scene.World, tick, scene.GameBuild, scene.Mods, Tactic),
            Width = scene.Width, Height = scene.Height, Terrain = scene.Terrain, Weather = scene.Weather,
            Environment = new(scene.OutdoorTemperature, scene.SkyGlow),
            Ground = scene.Ground.Select(g => new EncounterCellRegion(g.MinX - scene.MinX, g.MinZ - scene.MinZ, g.MaxX - scene.MinX, g.MaxZ - scene.MinZ, g.Terrain, g.Roof)).ToArray(),
            Obstacles = scene.Obstacles.Select(o => new EncounterObstacle(o.Definition, o.Stuff, o.X - scene.MinX, o.Z - scene.MinZ)).ToArray(),
            Actors = actors, HorizonTicks = Horizon, Seeds = Enumerable.Range(SeedBase, Samples).ToArray(),
            SamplingReason = "hunt roster comparison",
        }, "");
    }

    /// <summary>Whether the anchor saw every gunner of the rosters, so it can state their scenes at all; a hunter it never saw is a new comparison from the current read. Prey are never unseen: the plan proposes only the prey the anchor compares.</summary>
    public static bool StatesGunners(HuntActivityRead anchor, IEnumerable<HuntRoster> rosters) =>
        rosters.All(r => r.Gunners.All(g => anchor.Hunters.Any(h => h.Pawn == g)));

    /// <summary>
    /// Whether the comparison anchored to <paramref name="anchor"/> still describes <paramref name="now"/> at choice time: the
    /// same scene, and every rostered gunner within <see cref="MoveTolerance"/> of where the anchor saw it and otherwise the
    /// gunner it saw. Then every answer built from the anchor is usable, however far the gunners drifted between the reads in
    /// between; otherwise the comparison is asked again from the current read. Prey are not judged here: where they stand
    /// never stales the anchor, and their presence and grouping are judged every round (<see cref="PreyDrift"/>).
    /// </summary>
    public static bool StillAnchored(HuntActivityRead anchor, HuntActivityRead now, IEnumerable<HuntRoster> rosters) =>
        SameScene(anchor, now) && rosters.SelectMany(r => r.Gunners).Distinct(StringComparer.Ordinal)
            .All(g => SameGunner(anchor.Hunters.FirstOrDefault(h => h.Pawn == g), now.Hunters.FirstOrDefault(h => h.Pawn == g), MoveTolerance));

    /// <summary>
    /// How the prey the anchor compares drifted from <paramref name="now"/>, for the finding that names a restart: each anchor
    /// prey the read no longer shows (<c>unobserved</c>: it left the window or died), that is downed or in a mental state, or
    /// that the plan may no longer hunt (<c>gone</c>: not in <paramref name="allowed"/>, so taken or designated), then each
    /// anchor group whose standing members stand grouped otherwise now (<c>group:</c>). Empty while the anchor compares the
    /// same prey groups: where an animal stands is never drift, and an animal the anchor never saw is the next comparison's.
    /// </summary>
    public static IEnumerable<string> PreyDrift(HuntActivityRead anchor, HuntActivityRead now, IReadOnlySet<string> allowed)
    {
        var standing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in anchor.Prey.OrderBy(p => p.Pawn, StringComparer.Ordinal))
        {
            var n = now.Prey.FirstOrDefault(p => p.Pawn == a.Pawn);
            if (n is null) yield return a.Pawn + "=unobserved";
            else if (n.Downed) yield return a.Pawn + "=downed";
            else if (n.Mental) yield return a.Pawn + "=mental";
            else if (!allowed.Contains(a.Pawn)) yield return a.Pawn + "=gone";
            else standing.Add(a.Pawn);
        }
        var before = HuntAlternatives.PreyGroups(anchor.Prey.Where(p => standing.Contains(p.Pawn))).Select(Members);
        var after = HuntAlternatives.PreyGroups(now.Prey.Where(p => standing.Contains(p.Pawn))).Select(Members).ToHashSet(StringComparer.Ordinal);
        foreach (var group in before.Where(g => !after.Contains(g))) yield return "group:" + group;
        static string Members(HuntActorRead[] group) => string.Join('+', group.Select(p => p.Pawn));
    }

    /// <summary>
    /// Why the anchor no longer describes the rostered gunners, for the finding that names the restart: the scene when its
    /// identity or weather changed, and each gunner the anchor never saw (<c>unseen</c>), the read no longer shows
    /// (<c>unobserved</c>), that moved past the tolerance (<c>moved:</c> the larger axis distance) or otherwise changed
    /// (<c>changed:</c>, <see cref="Differences"/>).
    /// </summary>
    public static IEnumerable<string> GunnerDrift(HuntActivityRead anchor, HuntActivityRead now, IEnumerable<HuntRoster> rosters)
    {
        if (!SameScene(anchor, now)) yield return "scene";
        foreach (var gunner in rosters.SelectMany(r => r.Gunners).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var a = anchor.Hunters.FirstOrDefault(h => h.Pawn == gunner);
            var n = now.Hunters.FirstOrDefault(h => h.Pawn == gunner);
            if (a is null) yield return gunner + "=unseen";
            else if (n is null) yield return gunner + "=unobserved";
            else if (Math.Max(Math.Abs((long)a.Location.X - n.Location.X), Math.Abs((long)a.Location.Z - n.Location.Z)) is var moved && moved > MoveTolerance) yield return gunner + "=moved:" + moved;
            else if (!SameGunner(a, n, MoveTolerance)) yield return gunner + "=changed:" + Differences(a, n);
        }
    }

    /// <summary>
    /// What differs between two reads of one gunner, apart from where he stands, for the findings and release reasons that
    /// name a changed gunner: the field names, and for health each hediff gained (<c>+</c>) or lost (<c>-</c>), by definition
    /// and part. Skill levels and hediff severities are not differences (<see cref="StillDescribesGunner"/>).
    /// </summary>
    public static string Differences(HuntActorRead a, HuntActorRead n)
    {
        var fields = new List<string>();
        if (a.Kind != n.Kind) fields.Add("kind");
        if (a.Xenotype != n.Xenotype) fields.Add("xenotype");
        if (Math.Abs(a.Age - n.Age) >= AgeTolerance) fields.Add("age");
        if (a.Weapon != n.Weapon) fields.Add("weapon");
        if (a.WeaponRange != n.WeaponRange) fields.Add("weapon_range");
        if (a.Apparel != n.Apparel) fields.Add("apparel");
        if (a.Traits != n.Traits) fields.Add("traits");
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

    /// <summary>
    /// Whether the gunner is still the pawn the estimate described, apart from where the hunt has walked him and that it
    /// drafted him: his kind, xenotype, weapon, apparel, traits, health set (each hediff by definition and part, not its
    /// severity) and missing parts in the same scene. His skill levels are not judged either: they move with every shot
    /// and would release the hunt that moved them (#3525). An executing formation moves and drafts its gunners, so neither
    /// stales it; a downed or breaking gunner is the formation's own release (<see cref="HuntAdmission"/>).
    /// </summary>
    public static bool StillDescribesGunner(HuntActivityRead made, HuntActivityRead now, string gunner) =>
        SameScene(made, now) && SameGunner(Unplaced(made.Hunters.FirstOrDefault(h => h.Pawn == gunner)), Unplaced(now.Hunters.FirstOrDefault(h => h.Pawn == gunner)), int.MaxValue);

    /// <summary>What no longer describes the gunner (<see cref="StillDescribesGunner"/>): the scene, or the fields of <see cref="Differences"/>.</summary>
    public static string GunnerDifferences(HuntActivityRead made, HuntActivityRead now, string gunner)
    {
        if (!SameScene(made, now)) return "scene";
        var a = Unplaced(made.Hunters.FirstOrDefault(h => h.Pawn == gunner));
        var n = Unplaced(now.Hunters.FirstOrDefault(h => h.Pawn == gunner));
        return a is null || n is null ? "unobserved" : Differences(a, n);
    }

    private static HuntActorRead? Unplaced(HuntActorRead? a) => a is null ? null : a with { Available = false, Downed = false, Mental = false };

    private static bool SameScene(HuntActivityRead made, HuntActivityRead now) =>
        made.Scene is { } a && now.Scene is { } b && (a.World, a.GameBuild, a.Mods, a.Weather, a.Terrain) == (b.World, b.GameBuild, b.Mods, b.Weather, b.Terrain);

    // Where the gunner stands is judged against the tolerance and his age against its own; his skill levels and his hediffs'
    // severities move with every shot and every hour of healing, so neither is a change: the health set is compared by hediff and part.
    private static bool SameGunner(HuntActorRead? x, HuntActorRead? y, int moveTolerance) => x is not null && y is not null
        && Math.Abs((long)x.Location.X - y.Location.X) <= moveTolerance && Math.Abs((long)x.Location.Z - y.Location.Z) <= moveTolerance
        && Math.Abs(x.Age - y.Age) < AgeTolerance
        && HealthSet(x).SetEquals(HealthSet(y))
        && x with { Location = default, Age = 0, Skills = [], Health = [] } == y with { Location = default, Age = 0, Skills = [], Health = [] };

    private static HashSet<string> HealthSet(HuntActorRead a) =>
        a.Health.Select(h => h.Definition + (h.Part.Length == 0 ? "" : "/" + h.Part)).ToHashSet(StringComparer.Ordinal);

    private static EncounterActor Actor(HuntActorRead read, bool wild, HuntSceneRead scene, string target) => new()
    {
        Id = read.Pawn, Kind = read.Kind, Wild = wild, X = read.Location.X - scene.MinX, Z = read.Location.Z - scene.MinZ, Age = read.Age,
        Xenotype = read.Xenotype, Herd = wild ? read.Herd : "", HuntTarget = target,
        Weapon = read.Weapon is { } w ? new(w.Definition, w.Stuff, w.Quality, w.HitPoints) : null,
        Apparel = read.Apparel.Select(g => new EncounterGear(g.Definition, g.Stuff, g.Quality, g.HitPoints)).ToArray(),
        Skills = read.Skills.ToDictionary(s => s.Skill, s => s.Level, StringComparer.Ordinal),
        Traits = read.Traits.Select(t => new EncounterTrait(t.Definition, t.Degree)).ToArray(),
        Health = read.Health.Select(h => new EncounterHediff(h.Definition, h.Part, h.Severity)).ToArray(),
        MissingParts = read.MissingParts.ToArray(),
    };
}
