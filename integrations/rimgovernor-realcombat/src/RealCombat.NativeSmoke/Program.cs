using RimGovernor.RealCombat;
using RimGovernor.RealCombat.Native;
using Verse;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Operations = RimGovernor.Contracts.Wire.Operations;

// One duel in which the colonist is observed through the mod's combat mirror and commanded through its combat order
// handler (the raider stands still: nothing drives it yet). With --bare the built-in retargeting plays both sides.
// Exit 0 when the seat observed events and applied the draft and the attack.
// With --policy the live combat policy (PolicyController) plays the colonist.
//   RimGovernor.RealCombat.NativeSmoke --game <dir> [--bare|--policy]
var game = args[Array.IndexOf(args, "--game") + 1];
if (args.Contains("--arena-isolation"))
{
    return RimGovernor.RealCombat.NativeSmoke.ArenaIsolation.Run(game,
        args[Array.IndexOf(args, "--fixture") + 1], args[Array.IndexOf(args, "--out") + 1]);
}
if (args.Contains("--predictor"))
{
    return await RimGovernor.RealCombat.NativeSmoke.PredictorCheck.RunAsync(game,
        args[Array.IndexOf(args, "--predictor") + 1], args[Array.IndexOf(args, "--fixture") + 1], args[Array.IndexOf(args, "--out") + 1]);
}
var bare = args.Contains("--bare");
var policy = args.Contains("--policy");
var request = new RealFightRequest
{
    Schema = RealFightRequest.CurrentSchema,
    Name = "native-smoke",
    Width = 60,
    Height = 60,
    Terrain = "Soil",
    Weather = "Clear",
    Seeds = [1],
    MaxTicks = 3000,
    Pawns =
    [
        new RealPawn { Id = "col", Side = "colonist", Age = 30, Xenotype = "Baseliner", Kind = "Colonist", X = 20, Z = 30, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
        new RealPawn { Id = "host", Side = "hostile", Age = 30, Xenotype = "Baseliner", Kind = "Pirate", X = 45, Z = 30, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
    ],
};
// --policy fights inside a walled compound with the killbox the colony's planner lays out (DefenseStage); --open is
// the same fight on open ground, where the policy holds no layout and forms the squad fallback.
var open = args.Contains("--open");
DefenseStage? stage = null;
if (policy)
{
    // The policy fields at least two defenders for each opponent, so a duel is never one of its fights: two against one.
    var home = new RimGovernor.Brain.Domain.Cell(20, 30);
    if (!open)
    {
        var (_, _, h, _) = RimGovernor.Brain.Policy.LayoutDefenseRules.LayoutKillbox(RimGovernor.Brain.Policy.LayoutDefenseRules.OpeningPlan(RimGovernor.Brain.Domain.Rotation.West), new RimGovernor.Brain.Policy.Bounds(request.Width, request.Height));
        home = h;
    }

    request = request with
    {
        Pawns =
        [
            new RealPawn { Id = "col0", Side = "colonist", Age = 30, Xenotype = "Baseliner", Kind = "Colonist", X = home.X, Z = home.Z - 1, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
            new RealPawn { Id = "col1", Side = "colonist", Age = 30, Xenotype = "Baseliner", Kind = "Colonist", X = home.X, Z = home.Z + 1, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
            new RealPawn { Id = "host", Side = "hostile", Age = 30, Xenotype = "Baseliner", Kind = "Pirate", X = 45, Z = 30, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
        ],
    };
}

// --drugs: each colonist carries go-juice; --high makes the raiders high on it and --raiders N adds raiders, either of
// which is a fight worth dosing for.
if (args.Contains("--drugs"))
{
    var extra = args.Contains("--raiders") ? int.Parse(args[Array.IndexOf(args, "--raiders") + 1]) - 1 : 0;
    var raiders = Enumerable.Range(0, extra).Select(i => request.Pawns.Last(p => p.Side == "hostile") with { Id = "host" + (i + 1), Z = 28 + (i * 2) });
    request = request with
    {
        Pawns = [.. request.Pawns.Concat(raiders).Select(p => p.Side == "colonist" ? p with { Carried = ["GoJuice"] } : args.Contains("--high") ? p with { Hediffs = [new RealHediff("GoJuiceHigh", "", 0.5)] } : p)],
    };
}

// --armor: both sides in flak vest and helmet. --melee: the raiders carry swords, not rifles. --cover: sandbags in front of
// the colonists on open ground (the compound's own come from the planner).
if (args.Contains("--armor"))
{
    RealWear[] gear = [new("Apparel_FlakVest", ""), new("Apparel_AdvancedHelmet", "")];
    request = request with { Pawns = [.. request.Pawns.Select(p => p with { Worn = gear })] };
}

if (args.Contains("--melee"))
{
    request = request with { Pawns = [.. request.Pawns.Select(p => p.Side == "hostile" ? p with { Weapon = "MeleeWeapon_LongSword", WeaponStuff = "Steel", Skills = new Dictionary<string, int> { ["Melee"] = 8 } } : p)] };
}

// --psycast: the first colonist knows Stun (Royalty), and the raiders stand 18 cells off, inside the 20 cells the policy casts at.
if (args.Contains("--psycast"))
{
    var caster = true;
    request = request with { Pawns = [.. request.Pawns.Select(p => p.Side == "colonist" && caster ? CastFirst(p) : p.Side == "hostile" ? p with { X = 38 } : p)] };
    RealPawn CastFirst(RealPawn p) { caster = false; return p with { Psycasts = ["Stun"] }; }
}

// --permit: the first colonist is an Empire acolyte with the trooper squad permit and favor for it, against four raiders,
// which outmatch the two colonists so the policy calls the aid.
if (args.Contains("--permit"))
{
    var host = request.Pawns.Last(p => p.Side == "hostile");
    var raiders = Enumerable.Range(1, 3).Select(i => host with { Id = "host" + i, Z = 30 + (i * 2) });
    var holder = true;
    request = request with { Pawns = [.. request.Pawns.Concat(raiders).Select(p => p.Side == "colonist" && holder ? Holder(p) : p)] };
    RealPawn Holder(RealPawn p) { holder = false; return args.Contains("--no-aid") ? p : p with { Title = "Acolyte", Favor = 10, Permits = ["CallMilitaryAidSmall"] }; }
}

if (args.Contains("--cover"))
{
    request = request with { Things = [.. Enumerable.Range(28, 5).Select(z => new RealThing("Sandbags", "", 23, z))] };
}

// --hive (with --policy --open): an infestation. A hive at 44,30 under rock with a corridor of three doors west of it (HiveStage),
// three megascarabs near it, and three colonists west of the corridor with rifles.
HiveStage? hive = null;
if (args.Contains("--hive"))
{
    hive = new HiveStage(44, 30);
    request = request with
    {
        HostileFaction = "Insect",
        MaxTicks = 20000,
        Pawns =
        [
            new RealPawn { Id = "col0", Side = "colonist", Age = 30, Xenotype = "Baseliner", Kind = "Colonist", X = 35, Z = 29, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
            new RealPawn { Id = "col1", Side = "colonist", Age = 30, Xenotype = "Baseliner", Kind = "Colonist", X = 35, Z = 30, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
            new RealPawn { Id = "col2", Side = "colonist", Age = 30, Xenotype = "Baseliner", Kind = "Colonist", X = 35, Z = 31, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 } },
            new RealPawn { Id = "bug0", Side = "hostile", Kind = "Megascarab", X = 41, Z = 28 },
            new RealPawn { Id = "bug1", Side = "hostile", Kind = "Megascarab", X = 41, Z = 32 },
            new RealPawn { Id = "bug2", Side = "hostile", Kind = "Megascarab", X = 42, Z = 27 },
        ],
    };
}

GameHost.Boot(game);
if (args.Contains("--request-check"))
{
    return RequestCheckMode.Run();
}

if (args.Contains("--hunt"))
{
    return HuntMode.Run();
}

if (args.Contains("--gear-check"))
{
    return RequestCheckMode.RunGear();
}

if (args.Contains("--fixture"))
{
    return FixtureMode.Run(args[Array.IndexOf(args, "--fixture") + 1], args.Contains("--record"));
}

var controller = new SeatController();
var doorProbe = args.Contains("--doortest") ? new DoorProbe() : null;
var live = new PolicyController { Log = args.Contains("--trace") ? Console.Error.WriteLine : null };
if (policy && !open)
{
    var range = Verse.DefDatabase<Verse.ThingDef>.GetNamed("Gun_BoltActionRifle").Verbs[0].range;
    stage = new DefenseStage(RimGovernor.Brain.Domain.Rotation.West, 2, range) { Traps = !args.Contains("--no-traps") };
}

using var arena = new Arena(request, hive is not null ? hive.Raise : stage is null ? null : stage.Raise) { Controller = bare ? null : doorProbe is not null ? doorProbe : policy ? live : controller };
if (stage is not null)
{
    live.Layout = stage.Layout;
    Console.Error.WriteLine($"stage: layout {(stage.Layout.IsKnown ? "held" : "none")} refusal={(stage.Failure.Length == 0 ? "none" : stage.Failure)} home={stage.Home.X},{stage.Home.Z} bed={stage.BedCell?.X},{stage.BedCell?.Z} firing={string.Join(" ", stage.Planned?.Firing.Select(f => f.Cell.X + "," + f.Cell.Z) ?? [])} tiers={stage.Planned?.Tiers.Count}");
}
var result = arena.Fight(1);
Console.Error.WriteLine($"winner={result.Winner} ticks={result.Ticks} {(bare ? "(bare)" : policy ? "policy " + live.Summary : controller.Summary)}");
foreach (var p in result.Pawns)
{
    Console.Error.WriteLine($"  {p.Id} shots={p.Shots} hitsTaken={p.HitsTaken} dead={p.Dead} downed={p.Downed}");
}

if (doorProbe is not null)
{
    return 0;
}

if (policy)
{
    return live.Decisions > 0 && live.Applied > 0 && live.Failure.Length == 0 ? 0 : 1;
}

return bare || controller.Applied == 2 && controller.Refused == 0 && controller.Events > 0 ? 0 : 1;

// --request-check: the strict request format. Each reject case must throw RealFightRejected naming its field before a map
// or pawn exists; the accept case (body-part hediff, missing part, non-30 age, non-Baseliner xenotype) must build and read
// back equal. Exit 0 when every case behaves. --gear-check (two arenas: a process builds few maps before the game's terrain drifts)
// plays worn gear against new gear and reads a two-terrain, roofed map back cell for cell.
internal static class RequestCheckMode
{
    private static readonly RealPawn Colonist = new() { Id = "col", Side = "colonist", Kind = "Colonist", Age = 30, Xenotype = "Baseliner", X = 20, Z = 30, Weapon = "Gun_BoltActionRifle" };

    private static readonly RealPawn Raider = new() { Id = "host", Side = "hostile", Kind = "Pirate", Age = 30, Xenotype = "Baseliner", X = 45, Z = 30, Weapon = "Gun_BoltActionRifle" };

    private static readonly RealFightRequest Base = new()
    {
        Schema = RealFightRequest.CurrentSchema,
        Name = "request-check",
        Terrain = "Soil",
        Weather = "Clear",
        Seeds = [1],
        MaxTicks = 600,
        Pawns = [Colonist, Raider],
    };

    private const int DepartAt = 450;

    private static readonly string DepartKind = Environment.GetCommandLineArgs().Contains("--flee") ? RealDeparture.Flee : RealDeparture.Leave;

    internal static int Run()
    {
        var failures = 0;
        var rejects = new (string Field, Func<RealFightRequest> Make)[]
        {
            ("Schema", () => Base with { Schema = 0 }),
            ("Schema", () => Base with { Schema = 1 }),
            ("Schema", () => Base with { Schema = RealFightRequest.CurrentSchema + 1 }),
            ("Pawns[2].Side", () => Base with { Pawns = [Colonist, Raider, Deer("d0", "", 20) with { Kind = "Colonist", Age = 30, Xenotype = "Baseliner" }] }),
            ("Pawns[2].Herd", () => Base with { Pawns = [Colonist, Raider, Deer("d0", "lone", 20)] }),
            ("Pawns[3].Herd", () => Base with { Pawns = [Colonist, Raider, Deer("d0", "mixed", 20), Deer("d1", "mixed", 21) with { Kind = "WildBoar" }] }),
            ("Pawns[0].Herd", () => With(Colonist with { Herd = "h" })),
            ("Pawns[0].Hunt", () => With(Colonist with { Hunt = "host" })),
            ("Pawns[0].Hunt", () => With(Colonist with { Hunt = "nobody" })),
            ("Pawns[1].Hunt", () => Base with { Pawns = [Colonist, Raider with { Hunt = "col" }] }),
            ("Pawns[2]", () => Base with { Pawns = [Colonist, Raider, Deer("d0", "", 20) with { Weapon = "Gun_BoltActionRifle" }] }),
            ("Pawns[2].Hediffs[0].Part", () => Base with { Pawns = [Colonist, Raider, Deer("d0", "", 20) with { Hediffs = [new("Cut", "tail", 3)] }] }),
            ("Terrain", () => Base with { Terrain = "NoSuchTerrain" }),
            ("Pawns[0].Kind", () => With(Colonist with { Kind = "NoSuchKind" })),
            ("Pawns[0].Weapon", () => With(Colonist with { Weapon = "NoSuchGun" })),
            ("Pawns[0].Weapon", () => With(Colonist with { Weapon = "Steel" })),
            ("Pawns[0].Skills.NoSuchSkill", () => With(Colonist with { Skills = new Dictionary<string, int> { ["NoSuchSkill"] = 5 } })),
            ("Pawns[0].Xenotype", () => With(Colonist with { Xenotype = "NoSuchXenotype" })),
            ("Pawns[0].Age", () => With(Colonist with { Age = 0 })),
            ("Pawns[0].Hediffs[0].Def", () => With(Colonist with { Hediffs = [new("NoSuchHediff", "", 1)] })),
            ("Pawns[0].Hediffs[0].Part", () => With(Colonist with { Hediffs = [new("Cut", "tail", 3)] })),
            ("Pawns[0].Hediffs[0].Part", () => With(Colonist with { Hediffs = [new("Cut", "", 3)] })),
            ("Pawns[0].Hediffs[0].Part", () => With(Colonist with { Missing = ["left hand"], Hediffs = [new("Cut", "left hand", 3)] })),
            ("Pawns[0].Hediffs[0].Severity", () => With(Colonist with { Hediffs = [new("Cut", "right arm", 0)] })),
            ("Pawns[0].Missing[0]", () => With(Colonist with { Missing = ["tail"] })),
            ("Pawns[1].Id", () => Base with { Pawns = [Colonist, Raider with { Id = "col" }] }),
            ("Departures[0].Id", () => Base with { Departures = [new("nobody", 10, RealDeparture.Leave)] }),
            ("Departures[0].Tick", () => Base with { Departures = [new("col", -1, RealDeparture.Leave)] }),
            ("Departures[0].Tick", () => Base with { Departures = [new("col", 601, RealDeparture.Flee)] }),
            ("Departures[0].Kind", () => Base with { Departures = [new("col", 10, "vanish")] }),
            ("Departures[1].Id", () => Base with { Departures = [new("col", 10, RealDeparture.Leave), new("col", 20, RealDeparture.Flee)] }),
            ("Pawns[0].WeaponQuality", () => With(Colonist with { WeaponQuality = "Shoddy" })),
            ("Pawns[0].WeaponHitPoints", () => With(Colonist with { WeaponHitPoints = -3 })),
            ("Pawns[0].Worn[0].Quality", () => With(Colonist with { Worn = [new("Apparel_FlakVest", "", "poor")] })),
            ("Pawns[0].Worn[0].HitPoints", () => With(Colonist with { Worn = [new("Apparel_FlakVest", "", "", -1)] })),
            ("Ground[0].Terrain", () => Base with { Ground = [new("NoSuchTerrain", "", 0, 0, 5, 5)] }),
            ("Ground[0].Roof", () => Base with { Ground = [new("", "NoSuchRoof", 0, 0, 5, 5)] }),
            ("Ground[0].Max.X/Z", () => Base with { Ground = [new("Sand", "", 0, 0, 99, 5)] }),
            ("Ground[0]", () => Base with { Ground = [new("", "", 0, 0, 5, 5)] }),
        };
        var worldPawns = Verse.Find.WorldPawns.AllPawnsAliveOrDead.Count();
        foreach (var (field, make) in rejects)
        {
            try
            {
                using var rejectedArena = new Arena(make());
                Console.Error.WriteLine($"FAIL accepted a request that must reject on {field}");
                failures++;
            }
            catch (RealFightRejected e) when (e.Problems.Any(p => p.StartsWith(field + ":", StringComparison.Ordinal)))
            {
                Console.Error.WriteLine($"ok   rejected {field}: {e.Problems.First(p => p.StartsWith(field + ":", StringComparison.Ordinal))}");
            }
            catch (RealFightRejected e)
            {
                Console.Error.WriteLine($"FAIL rejected without naming {field}: {e.Message}");
                failures++;
            }
        }

        foreach (var (what, json) in new[] { ("unknown field", """{"schema":3,"bogus":1}"""), ("unknown pawn field", """{"schema":3,"pawns":[{"id":"a","tail":true}]}""") })
        {
            try
            {
                RequestJson.Read(json);
                Console.Error.WriteLine($"FAIL accepted JSON with an {what}");
                failures++;
            }
            catch (RealFightRejected e)
            {
                Console.Error.WriteLine($"ok   rejected {what}: {e.Message}");
            }
        }

        if (Verse.Find.Maps.Count != 0 || Verse.Find.WorldPawns.AllPawnsAliveOrDead.Count() != worldPawns)
        {
            Console.Error.WriteLine($"FAIL a rejected request built a map ({Verse.Find.Maps.Count}) or generated a pawn");
            failures++;
        }

        var accepted = Colonist with
        {
            Age = 41,
            Xenotype = "Hussar",
            Missing = ["left hand"],
            Hediffs = [new("Cut", "right arm", 3), new("Cut", "right arm", 2.5), new("GoJuiceHigh", "", 0.5)],
        };
        var accept = Base with { MaxTicks = 900, Departures = [new("col2", DepartAt, DepartKind)], Pawns = [accepted, Colonist with { Id = "col2", Z = 34 }, Raider] };
        using var arena = new Arena(accept with { Verify = true });
        var outcome = arena.Fight(1);
        var read = arena.Readback.Pawns[0];
        Console.Error.WriteLine($"accepted: age={read.Age:0.##} xenotype={read.Xenotype} missing=[{string.Join(",", read.Missing)}] hediffs=[{string.Join(", ", read.Hediffs)}] ticks={outcome.Ticks}");
        if (read.Age < 40.9 || read.Xenotype != "Hussar" || read.Missing.Count != 1 || read.Hediffs.Count != 3)
        {
            Console.Error.WriteLine("FAIL the readback does not carry the request");
            failures++;
        }

        // Determinism still holds with the new health: the same seed played twice gives one outcome.
        if (outcome.Nondeterministic)
        {
            Console.Error.WriteLine("FAIL the accepted request is not deterministic");
            failures++;
        }

        // Departures: the second hunter departs at tick DepartAt ("leave", or "flee" under --flee). It fires nothing after
        // that tick, the first colonist keeps fighting, and the result records the applied tick. The accept fight is the
        // only arena per process, so it carries the departure (Verify replays it, covering determinism).
        var gone = outcome.Pawns.Single(p => p.Id == "col2");
        var kept = outcome.Pawns.Single(p => p.Id == "col");
        Console.Error.WriteLine($"{DepartKind}: ticks={outcome.Ticks} departedAt={gone.DepartureTick} gotAway={gone.GotAway} firstShot={gone.FirstShotTick} lastShot={gone.LastShotTick} keptShots={kept.Shots} keptLastShot={kept.LastShotTick}");
        if (gone.DepartureTick != DepartAt || gone.LastShotTick > DepartAt || outcome.Ticks <= DepartAt || kept.LastShotTick <= DepartAt)
        {
            Console.Error.WriteLine($"FAIL the {DepartKind} departure was not replayed (applied tick, no shot after it, the others fighting on)");
            failures++;
        }

        if (DepartKind == RealDeparture.Leave && !gone.GotAway)
        {
            Console.Error.WriteLine("FAIL a pawn that left the map is not reported as away");
            failures++;
        }

        Console.Error.WriteLine(failures == 0 ? "request-check: PASS" : $"request-check: FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }

    // Gear condition and per-cell ground. A poor, half-hit-point rifle and a worn flak vest read back equal and the shots and
    // damage differ from the same pawns in new gear; two terrains and a roofed patch read back cell for cell; a hit point
    // count above the item's maximum is rejected by the readback gate.
    internal static int RunGear()
    {
        var failures = 0;
        var vest = Verse.DefDatabase<Verse.ThingDef>.GetNamed("Apparel_FlakVest");
        var rifle = Verse.DefDatabase<Verse.ThingDef>.GetNamed("Gun_BoltActionRifle");
        RealWear[] fresh = [new("Apparel_FlakVest", "", "Normal")];
        RealWear[] worn = [new("Apparel_FlakVest", "", "Poor", vest.BaseMaxHitPoints / 2)];
        var newGear = Colonist with { WeaponQuality = "Normal", Worn = fresh };
        var oldGear = Colonist with { WeaponQuality = "Poor", WeaponHitPoints = rifle.BaseMaxHitPoints / 2, Worn = worn };
        RealSeedOutcome Play(RealPawn colonist, out RealPawnReadback read, out RealReadback world)
        {
            using var arena = new Arena(Base with { Terrain = "Soil", Ground = [new("Sand", "", 10, 10, 30, 40), new("", "RoofRockThick", 15, 15, 20, 20), new("", "RoofConstructed", 40, 40, 44, 44)], Pawns = [colonist, Raider with { Worn = fresh, WeaponQuality = "Normal" }] });
            var o = arena.Fight(1);
            read = arena.Readback.Pawns[0];
            world = arena.Readback;
            return o;
        }

        var good = Play(newGear, out var goodRead, out var goodWorld);
        var bad = Play(oldGear, out var badRead, out _);
        Console.Error.WriteLine($"gear: new ticks={good.Ticks} {Summ(good)}; worn ticks={bad.Ticks} {Summ(bad)}; read {badRead.WeaponQuality}/{badRead.WeaponHitPoints} vest {badRead.Worn[0]}");
        if (badRead.WeaponQuality != "Poor" || badRead.WeaponHitPoints != rifle.BaseMaxHitPoints / 2 || badRead.Worn[0].Quality != "Poor" || badRead.Worn[0].HitPoints != vest.BaseMaxHitPoints / 2 || goodRead.WeaponQuality != "Normal")
        {
            Console.Error.WriteLine("FAIL the gear does not read back as stated");
            failures++;
        }

        if (Summ(good) == Summ(bad) && good.Ticks == bad.Ticks)
        {
            Console.Error.WriteLine("FAIL worn gear played the same fight as new gear");
            failures++;
        }

        var ground = goodWorld;
        Console.Error.WriteLine($"ground: terrain=[{string.Join(", ", ground.TerrainCells.Select(kv => kv.Key + "=" + kv.Value))}] roof=[{string.Join(", ", ground.RoofCells.Select(kv => kv.Key + "=" + kv.Value))}]");
        if (ground.TerrainCells.GetValueOrDefault("Sand") != 21 * 31 || ground.RoofCells.GetValueOrDefault("RoofRockThick") != 36 || ground.RoofCells.GetValueOrDefault("RoofConstructed") != 25)
        {
            Console.Error.WriteLine("FAIL the ground does not read back as stated");
            failures++;
        }

        Console.Error.WriteLine(failures == 0 ? "gear-check: PASS" : $"gear-check: FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static string Summ(RealSeedOutcome o) => string.Join(" ", o.Pawns.Select(p => $"{p.Id}:shots={p.Shots},hits={p.HitsTaken},hp={p.HealthPercent:0.###}"));

    private static RealFightRequest With(RealPawn first) => Base with { Pawns = [first, Raider] };

    private static RealPawn Deer(string id, string herd, int z) => new() { Id = id, Side = "wild", Kind = "Deer", Herd = herd, X = 40, Z = z };
}

// --fixture <dir>: the committed hunting fixture (hunting-observed.request.json) built in this process and its readback
// compared with the committed hunting-observed.readback.json, value for value; the unsupported variant must reject
// naming its field before any map exists. One seed, one arena. --record writes the readback file instead of comparing
// (review it against the request before committing). A pass is a round-trip proof, not a calibration claim.
internal static class FixtureMode
{
    internal static int Run(string dir, bool record)
    {
        var failures = 0;
        var readbackPath = Path.Combine(dir, "hunting-observed.readback.json");
        var json = File.ReadAllText(Path.Combine(dir, "hunting-observed.request.json"));
        var worldPawns = Verse.Find.WorldPawns.AllPawnsAliveOrDead.Count();
        try
        {
            RequestJson.Read(File.ReadAllText(Path.Combine(dir, "hunting-unsupported.request.json")));
            Console.Error.WriteLine("FAIL the unsupported variant was accepted");
            failures++;
        }
        catch (RealFightRejected e)
        {
            Console.Error.WriteLine("ok   unsupported variant rejected: " + e.Message);
        }

        if (Verse.Find.Maps.Count != 0 || Verse.Find.WorldPawns.AllPawnsAliveOrDead.Count() != worldPawns)
        {
            Console.Error.WriteLine("FAIL a rejected request built a map or generated a pawn");
            failures++;
        }

        var request = RequestJson.Read(json);
        using var arena = new Arena(request);
        var outcome = arena.Fight(request.Seeds[0]);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };
        var actual = System.Text.Json.JsonSerializer.Serialize(arena.Readback, options);
        if (record)
        {
            File.WriteAllText(readbackPath, actual + "\n");
            Console.Error.WriteLine("recorded " + readbackPath);
        }
        else if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(actual), System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(readbackPath))))
        {
            Console.Error.WriteLine("FAIL the readback differs from the committed expectation\n" + actual);
            failures++;
        }

        Console.Error.WriteLine($"fixture: winner='{outcome.Winner}' ticks={outcome.Ticks} nondeterministic={outcome.Nondeterministic}");
        foreach (var p in outcome.Pawns)
        {
            Console.Error.WriteLine($"  {p.Id} down={p.Downed} dead={p.Dead} hp={p.HealthPercent:0.00} shots={p.Shots} hit={p.HitsTaken} attack={p.FirstAttackTick} depart={p.DepartureTick} withdrew={p.Withdrew}@{p.WithdrawalTick} kills=[{string.Join(",", p.Kills)}] injuries={p.Injuries.Count}");
        }

        if (outcome.Nondeterministic)
        {
            Console.Error.WriteLine("FAIL the fixture is not deterministic");
            failures++;
        }

        Console.Error.WriteLine(failures == 0 ? "fixture: PASS" : $"fixture: FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }
}

// --hunt: one hunter with a rifle against a wild herd of three flee-only deer and one of three revenge-capable boars, each
// run on its own through the game's Hunt job and animal AI (no scripting of the prey), twice per seed under Verify, with a
// prey animal built carrying an existing injury that must read back. Exit 0 when the injury reads back and no seed
// is nondeterministic.
internal static class HuntMode
{
    private static int s_prey;

    private static int s_wounds;

    // The outcome against the game objects read directly after the fight: every injury (def, part, severity, bleed, tend and
    // permanence), the missing parts, the bleed rate, and the dead pawn's meat and leather; event ticks must be ordered.
    private static int CompareWithGame(string kind, int seed, RealSeedOutcome outcome, Dictionary<string, Direct> direct)
    {
        var bad = 0;
        foreach (var p in outcome.Pawns)
        {
            var d = direct[p.Id];
            var injuries = p.Injuries.Select(i => $"{i.Def}|{i.Part}|{i.Severity}|{i.BleedRate}|{i.TendableNow}|{i.Tended}|{i.Permanent}").ToList();
            var products = p.Products is null ? "alive" : $"{p.Products.MeatDef}|{p.Products.Meat}|{p.Products.LeatherDef}|{p.Products.Leather}";
            var ok = injuries.SequenceEqual(d.Injuries) && p.Missing.Order().SequenceEqual(d.Missing.Order()) && p.BleedRate == d.BleedRate
                && p.NeedsTend == d.NeedsTend && products == d.Products && p.Dead == d.Dead
                && (p.FirstShotTick < 0 || p.FirstAttackTick is >= 0 && p.FirstAttackTick <= p.FirstShotTick)
                && p.Withdrew == (p.WithdrawalTick >= 0) && p.Kills.All(k => outcome.Pawns.Single(o => o.Id == k).Dead);
            s_wounds += p.Injuries.Count;
            s_prey += p.Products is { Meat: > 0 } ? 1 : 0;
            if (!ok)
            {
                Console.Error.WriteLine($"FAIL {kind} seed {seed} {p.Id}: outcome differs from the game objects\n  outcome: {string.Join(";", injuries)} missing=[{string.Join(",", p.Missing)}] bleed={p.BleedRate} {products}\n  game:    {string.Join(";", d.Injuries)} missing=[{string.Join(",", d.Missing)}] bleed={d.BleedRate} {d.Products}");
                bad++;
            }
        }

        var line = string.Join(" ", outcome.Pawns.Select(p => $"{p.Id}[injuries={p.Injuries.Count} missing={p.Missing.Count} bleed={p.BleedRate:0.###} tend={p.NeedsTend} attack={p.FirstAttackTick} withdrew={p.Withdrew}@{p.WithdrawalTick} kills=[{string.Join(",", p.Kills)}] products={(p.Products is null ? "-" : $"{p.Products.Meat:0.#} {p.Products.MeatDef} {p.Products.Leather:0.#} {p.Products.LeatherDef}")}]"));
        Console.Error.WriteLine($"  detail: {line}");
        return bad;
    }

    internal sealed record Direct(IReadOnlyList<string> Injuries, IReadOnlyList<string> Missing, double BleedRate, bool NeedsTend, string Products, bool Dead)
    {
        internal static Direct Of(Verse.Pawn pawn)
        {
            var set = pawn.health.hediffSet;
            var corpse = pawn.Corpse?.InnerPawn ?? pawn;
            return new Direct(
                [.. set.hediffs.Where(h => h is not Verse.Hediff_MissingPart).Select(h => $"{h.def.defName}|{h.Part?.Label ?? ""}|{(double)h.Severity}|{(double)h.BleedRate}|{h.TendableNow()}|{h.IsTended()}|{h.IsPermanent()}")],
                [.. set.GetMissingPartsCommonAncestors().Select(m => m.Part.Label)],
                set.BleedRateTotal,
                pawn.health.HasHediffsNeedingTend(),
                pawn.Dead ? $"{corpse.RaceProps.meatDef?.defName}|{(double)RimWorld.StatExtension.GetStatValue(corpse, RimWorld.StatDefOf.MeatAmount)}|{corpse.RaceProps.leatherDef?.defName}|{(double)RimWorld.StatExtension.GetStatValue(corpse, RimWorld.StatDefOf.LeatherAmount)}" : "alive",
                pawn.Dead);
        }
    }

    internal static int Run()
    {
        var failures = 0;
        foreach (var (kind, herd) in new[] { ("Deer", "deer"), ("WildBoar", "boar") })
        {
            var request = new RealFightRequest
            {
                Schema = RealFightRequest.CurrentSchema,
                Name = "hunt-" + kind,
                Terrain = "Soil",
                Weather = "Clear",
                Seeds = [1, 2, 3],
                MaxTicks = 3000,
                Verify = true,
                Pawns =
                [
                    new RealPawn { Id = "hunter", Side = "colonist", Kind = "Colonist", Age = 30, Xenotype = "Baseliner", X = 15, Z = 30, Weapon = "Gun_BoltActionRifle", Skills = new Dictionary<string, int> { ["Shooting"] = 8 }, Hunt = "p0" },
                    new RealPawn { Id = "p0", Side = "wild", Kind = kind, Herd = herd, X = 38, Z = 29, Hediffs = [new("Cut", "front left leg", 2)] },
                    new RealPawn { Id = "p1", Side = "wild", Kind = kind, Herd = herd, X = 40, Z = 31 },
                    new RealPawn { Id = "p2", Side = "wild", Kind = kind, Herd = herd, X = 41, Z = 28 },
                ],
            };
            using var arena = new Arena(request);
            var direct = new Dictionary<string, Direct>();
            arena.Inspect = pawns =>
            {
                direct.Clear();
                foreach (var (id, pawn) in pawns)
                {
                    direct[id] = Direct.Of(pawn);
                }
            };
            foreach (var seed in request.Seeds)
            {
                var outcome = arena.Fight(seed);
                failures += CompareWithGame(kind, seed, outcome, direct);
                var line = string.Join(" ", outcome.Pawns.Select(p => $"{p.Id}[down={p.Downed} dead={p.Dead} hp={p.HealthPercent:0.00} shots={p.Shots} hit={p.HitsTaken}]"));
                Console.Error.WriteLine($"{kind} seed {seed}: winner='{outcome.Winner}' ticks={outcome.Ticks} nondeterministic={outcome.Nondeterministic} {line}");
                if (outcome.Nondeterministic)
                {
                    failures++;
                }
            }

            var read = arena.Readback.Pawns.First(p => p.Id == "p0");
            Console.Error.WriteLine($"{kind} p0 read back: side={read.Side} hediffs=[{string.Join(", ", read.Hediffs)}]");
            if (read.Side != "wild" || read.Hediffs.Count != 1 || read.Hediffs[0].Def != "Cut" || read.Hediffs[0].Part != "front left leg")
            {
                failures++;
            }
        }

        // The check must have seen a wounded pawn and a killed animal's products, or it proved nothing.
        Console.Error.WriteLine($"hunt: {s_wounds} injuries and {s_prey} corpses with meat compared");
        if (s_wounds == 0 || s_prey == 0)
        {
            failures++;
        }

        Console.Error.WriteLine(failures == 0 ? "hunt: PASS" : $"hunt: FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }
}

internal sealed class SeatController : IFightController
{
    private NativeSeat? _seat;
    private bool _drafted;
    private string? _target;

    internal int Captures { get; private set; }

    internal int Events { get; private set; }

    internal int Applied { get; private set; }

    internal int Refused { get; private set; }

    internal string Summary => $"captures={Captures} events={Events} applied={Applied} refused={Refused} hookErrors={_seat?.HookErrors.Count}";

    public void Begin(Map map) => _seat = NativeSeat.Open(map);

    public void Decide(Map map, int tick)
    {
        if (tick % 15 != 0)
        {
            return;
        }

        var frame = _seat!.Capture();
        Captures++;
        Events = Math.Max(Events, frame.CombatEvents.Count);
        var me = frame.CombatPawns.FirstOrDefault(p => p.Side == Mirror.CombatSide.Colonist);
        var foe = frame.CombatPawns.FirstOrDefault(p => p.Side == Mirror.CombatSide.Hostile && p.Downed != true);
        if (me is null || foe is null)
        {
            return;
        }

        if (!_drafted)
        {
            Record(_seat.Apply(Orders(me.Id!, new Operations.CombatOrderOrder.DraftCase(new Operations.Clear()))));
            _drafted = true;
        }

        if (_target != foe.Id)
        {
            _target = foe.Id;
            Record(_seat.Apply(Orders(me.Id!, new Operations.CombatOrderOrder.AttackCase(new Operations.EntityPrecondition { EntityId = foe.Id }))));
        }
    }

    private void Record(IReadOnlyList<RimGovernor.Contracts.Wire.Receipts.CombatOrderResult> results)
    {
        foreach (var r in results)
        {
            if (r.Applied == true)
            {
                Applied++;
            }
            else
            {
                Refused++;
                Console.Error.WriteLine($"refused: {r.Refusal}");
            }
        }
    }

    private static Operations.CombatOrders Orders(string pawn, Operations.CombatOrderOrder order) => new()
    {
        Orders = [new Operations.CombatOrder { Pawn = new Operations.EntityPrecondition { EntityId = pawn }, Order = order }],
    };
}


// --doortest (with --hive): the draft-and-cancel door trick. A colonist walks to the cell beside the outer corridor door, is
// ordered onto the door cell, and is stopped the moment the door starts to open; the door is then watched, a hold-open set and
// cleared, and a manual close tried. Prints the door and pawn every few ticks.
internal sealed class DoorProbe : IFightController
{
    private NativeSeat? _seat;
    private string _pawn = "";
    private int _phase;
    private int _since;
    private int _lastLog = -100;

    public void Begin(Map map) => _seat = NativeSeat.Open(map);

    public void Decide(Map map, int tick)
    {
        var door = new IntVec3(38, 0, 30).GetEdifice(map) as RimWorld.Building_Door;
        var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
        if (door is null || pawn is null)
        {
            return;
        }

        var f = typeof(RimWorld.Building_Door);
        object? Field(string n) => f.GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(door);
        string State() => $"open={door.Open} free={door.FreePassage} hold={door.HoldOpen} ticksOpen={Field("visualTicksOpen")} untilClose={Field("ticksUntilClose")} pawn={pawn.Position.x},{pawn.Position.z} job={pawn.CurJobDef?.defName}";
        void Say(string what) => Console.Error.WriteLine($"t={tick} [{what}] {State()}");
        void Do(Operations.CombatOrderOrder order) => _seat!.Apply(new Operations.CombatOrders { Orders = [new Operations.CombatOrder { Pawn = new Operations.EntityPrecondition { EntityId = "Thing_" + pawn.ThingID }, Order = order }] });
        _pawn = pawn.ThingID;

        if (tick == 1)
        {
            Do(new Operations.CombatOrderOrder.DraftCase(new Operations.Clear()));
            Do(new Operations.CombatOrderOrder.MoveCase(new RimGovernor.Contracts.Wire.Common.Cell { X = 37, Z = 30 }));
            _phase = 1;
        }

        if (_phase == 1 && pawn.Position.x == 37 && pawn.Position.z == 30)
        {
            Say("beside the door; ordering onto it");
            Do(new Operations.CombatOrderOrder.MoveCase(new RimGovernor.Contracts.Wire.Common.Cell { X = 38, Z = 30 }));
            _phase = 2;
            _since = tick;
        }

        if (_phase == 2 && (door.Open || Convert.ToInt32(Field("visualTicksOpen") ?? 0) > 0))
        {
            Say("door started opening; stopping the pawn");
            Do(new Operations.CombatOrderOrder.StopCase(new Operations.Clear()));
            _phase = 3;
            _since = tick;
        }

        if (_phase == 3 && tick - _since == 120)
        {
            Say("120 ticks later; setting hold-open");
            Do(new Operations.CombatOrderOrder.DoorCase(new Operations.CombatDoor { Cell = new RimGovernor.Contracts.Wire.Common.Cell { X = 38, Z = 30 }, Mode = Operations.CombatDoorMode.HoldOpen }));
            _phase = 4;
            _since = tick;
        }

        if (_phase == 4 && tick - _since == 200)
        {
            Say("200 ticks held; releasing hold (Close)");
            Do(new Operations.CombatOrderOrder.DoorCase(new Operations.CombatDoor { Cell = new RimGovernor.Contracts.Wire.Common.Cell { X = 38, Z = 30 }, Mode = Operations.CombatDoorMode.Close }));
            _phase = 5;
            _since = tick;
        }

        if (_phase == 5 && tick - _since == 30)
        {
            Say("30 ticks after release; trying StartManualCloseBy");
            var m = f.GetMethod("StartManualCloseBy");
            Console.Error.WriteLine($"   StartManualCloseBy present={m is not null}");
            m?.Invoke(door, [pawn]);
            _phase = 6;
            _since = tick;
        }

        if (_phase >= 2 && tick - _lastLog >= 10 && tick - _since < 400)
        {
            _lastLog = tick;
            Say("watch");
        }
    }
}
