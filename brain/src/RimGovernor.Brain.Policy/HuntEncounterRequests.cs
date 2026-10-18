using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>What the runtime knows about roster estimates for the current observation. Policy reads this; it never calls the service.</summary>
public sealed record HuntEstimateView(IReadOnlyDictionary<string, HuntEstimate> Estimates, IReadOnlyDictionary<string, string> Refusals, IReadOnlySet<string> Pending)
{
    public static readonly HuntEstimateView None = new(new Dictionary<string, HuntEstimate>(), new Dictionary<string, string>(), new HashSet<string>());

    /// <summary>Names which rosters are answered; a plan built over one revision is stale once another arrives.</summary>
    public string Revision => string.Join(',', Estimates.Keys.Order(StringComparer.Ordinal)) + "|" + string.Join(',', Refusals.Keys.Order(StringComparer.Ordinal));
}

/// <summary>The runtime side of the hunt comparison: asks the prediction service asynchronously and remembers what it answered.</summary>
public interface IHuntEstimates
{
    /// <summary>Estimates still valid for this observation, refusals recorded for it, and rosters still being predicted.</summary>
    HuntEstimateView Current(HuntActivityRead read, long tick);

    /// <summary>Queues the rosters without a current estimate or refusal; never blocks. Returns how many were newly queued.</summary>
    int Request(HuntActivityRead read, IEnumerable<HuntRoster> rosters, long tick);
}

/// <summary>Builds the stated hypothetical scene a roster is predicted in, from the observed hunt read.</summary>
public static class HuntEncounterRequests
{
    public const string Tactic = "native-hunt/timed-departure";
    /// <summary>The encounter horizon, 2.4 game hours: the window the qualification protocol measured.</summary>
    public static readonly int Horizon = (int)(GameTime.TicksPerHour * 2.4);
    public const int Samples = 16, SeedBase = 2000001;
    /// <summary>An estimate is reused while its pawns have moved no more than this many cells and nothing about them changed.</summary>
    public const int MoveTolerance = 8;
    /// <summary>Biological age grows every tick, so an estimate is reused while the pawns have aged less than this many years.</summary>
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

    /// <summary>Whether an estimate made from <paramref name="made"/> still describes <paramref name="now"/> for the roster: its gunners and its prey. A hauler is not in the scene; its trip is priced from the routes of each round.</summary>
    public static bool StillDescribes(HuntActivityRead made, HuntActivityRead now, HuntRoster roster) =>
        SameScene(made, now)
        && roster.Gunners.All(g => SameActor(made.Hunters.FirstOrDefault(h => h.Pawn == g), now.Hunters.FirstOrDefault(h => h.Pawn == g), MoveTolerance))
        && roster.Prey.All(p => SameActor(made.Prey.FirstOrDefault(h => h.Pawn == p), now.Prey.FirstOrDefault(h => h.Pawn == p), MoveTolerance));

    /// <summary>
    /// Whether the gunner is still the pawn the estimate described, apart from where the hunt has walked him and that it
    /// drafted him: his kind, xenotype, weapon, apparel, skills, traits, health and missing parts in the same scene. An
    /// executing formation moves and drafts its gunners, so neither stales it; a downed or breaking gunner is the
    /// formation's own release (<see cref="HuntAdmission"/>).
    /// </summary>
    public static bool StillDescribesGunner(HuntActivityRead made, HuntActivityRead now, string gunner)
    {
        static HuntActorRead? Unplaced(HuntActorRead? a) => a is null ? null : a with { Available = false, Downed = false, Mental = false };
        return SameScene(made, now) && SameActor(Unplaced(made.Hunters.FirstOrDefault(h => h.Pawn == gunner)), Unplaced(now.Hunters.FirstOrDefault(h => h.Pawn == gunner)), int.MaxValue);
    }

    private static bool SameScene(HuntActivityRead made, HuntActivityRead now) =>
        made.Scene is { } a && now.Scene is { } b && (a.World, a.GameBuild, a.Mods, a.Weather, a.Terrain) == (b.World, b.GameBuild, b.Mods, b.Weather, b.Terrain);

    private static bool SameActor(HuntActorRead? x, HuntActorRead? y, int moveTolerance) => x is not null && y is not null
        && Math.Abs((long)x.Location.X - y.Location.X) <= moveTolerance && Math.Abs((long)x.Location.Z - y.Location.Z) <= moveTolerance
        && Math.Abs(x.Age - y.Age) < AgeTolerance
        && x with { Location = default, Age = 0 } == y with { Location = default, Age = 0 };

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
