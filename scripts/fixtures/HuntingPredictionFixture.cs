#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace HomeBridge.BridgeTools;

/// <summary>Disposable CombatLab experiment. Independent live-game ticks; never calls the hypothetical arena.</summary>
public sealed class HuntingPredictionFixture
{
    [Tool("test/hunting_prediction_trial", Description = "UNSAFE FOR MODEL EXECUTION. Prepared revenge-enabled native Hunt experiment with observed input and outcome; test builds only.")]
    public async Task<object> Trial(IRimBridgeContext ctx, CancellationToken cancellationToken, int seed = 1,
        string stratum = "baseline", int horizon = 6000, string animal = "WildBoar", int preyCount = 0, int hunters = 0)
    {
        return await ctx.MainThread.InvokeAsync<object>(() => Run(seed, stratum, horizon, animal, preyCount, hunters), cancellationToken);
    }

    private static object Run(int seed, string stratum, int horizon, string animal, int preyCount, int hunters)
    {
        var map = Find.CurrentMap;
        if (map == null || !Find.TickManager.Paused || map.Size.x != 100 || map.Size.z != 100 || horizon is < 1 or > 12000)
            throw new InvalidOperationException("Paused 100-cell disposable lab and bounded horizon required.");
        if (!new[] { "baseline", "health", "distance", "terrain", "herd", "departure", "probe" }.Contains(stratum))
            throw new ArgumentException("Unknown predeclared stratum.");
        LabStart.Wipe(map, hunters > 0 ? hunters : stratum == "departure" ? 2 : 1);
        // Quiet incidents remain disabled; genuine revenge uses the engine's Rough difficulty value.
        var revenge = new Difficulty(DifficultyDefOf.Rough).manhunterChanceOnDamageFactor;
        Find.Storyteller.difficulty.manhunterChanceOnDamageFactor = revenge;
        var actors = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).ToList();
        var hunterCount = actors.Count;
        for (var i = 0; i < hunterCount; i++)
        {
            var pawn = actors[i];
            pawn.Position = new IntVec3(45 + i * 2, 0, 40);
            pawn.Notify_Teleported(true, true);
            pawn.apparel.DestroyAll();
            pawn.equipment.DestroyAllEquipment();
            var weapon = (ThingWithComps)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Gun_BoltActionRifle"));
            weapon.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Outsider);
            pawn.equipment.AddEquipment(weapon);
            if (stratum == "health")
            {
                var part = pawn.RaceProps.body.AllParts.First(p => p.def.defName == "Arm");
                var injury = HediffMaker.MakeHediff(HediffDefOf.Cut, pawn, part);
                injury.Severity = 3;
                pawn.health.AddHediff(injury);
            }
        }
        // Generation uses a fixed preparation stream; future native draws are seeded separately below.
        Rand.PushState(730);
        try
        {
            for (var i = 0; i < (preyCount > 0 ? preyCount : stratum == "herd" ? 3 : 1); i++)
            {
                var prey = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named(animal), null,
                    fixedBiologicalAge: 3, fixedChronologicalAge: 3));
                GenSpawn.Spawn(prey, new IntVec3(45 + i * 2, 0, stratum == "distance" ? 65 : 52), map);
                actors.Add(prey);
            }
        }
        finally { Rand.PopState(); }
        var ground = new List<EncounterCellRegion>();
        if (stratum == "terrain")
        {
            for (var z = 44; z <= 48; z++) for (var x = 0; x < 100; x++)
                map.terrainGrid.SetTerrain(new IntVec3(x, 0, z), TerrainDefOf.Sand);
            ground.Add(new(0, 44, 99, 48, "Sand", ""));
        }
        var ids = actors.Select((p, i) => (p, id: i < hunterCount ? "h" + i : "p" + (i - hunterCount))).ToDictionary(x => x.p, x => x.id);
        var start = Find.TickManager.TicksGame;
        var build = HashFile(typeof(Pawn).Assembly.Location);
        var mods = Mods();
        var request = new EncounterRequest
        {
            Identity = new("combatlab-hunting", start, build, mods, "native-hunt/timed-departure"),
            Width = 100, Height = 100, Terrain = "Soil", Weather = "Clear", Ground = ground,
            Environment = new(map.mapTemperature.OutdoorTemp, map.skyManager.CurSkyGlow),
            HorizonTicks = horizon, Seeds = [1000001], SamplingReason = "independent paired native pilot",
            Actors = actors.Select((p, i) => Observe(p, ids[p], i < hunterCount, stratum == "departure" && i == 1, stratum == "herd" || preyCount > 1)).ToArray(),
        };
        // Reject an unstated map layer rather than crediting a plausible-looking reconstruction.
        if (map.weatherManager.curWeather != WeatherDefOf.Clear || map.AllCells.Any(c =>
            map.roofGrid.RoofAt(c) != null || map.snowGrid.GetDepth(c) != 0 ||
            map.terrainGrid.TerrainAt(c).defName != (stratum == "terrain" && c.z is >= 44 and <= 48 ? "Sand" : "Soil")))
            throw new InvalidOperationException("Native lab environment differs from the observed request.");
        // The served hunt read of the same scene, before a shot, for the analytic estimate's comparison (#3533).
        var huntRead = System.Text.Json.JsonSerializer.Serialize(NativeHuntActivity.Read(map), RimGovernor.Contracts.Wire.WireJsonContext.Default.HuntActivityRead);
        var first = new Dictionary<Pawn, int>();
        var last = new Dictionary<Pawn, int>();
        var left = new Dictionary<Pawn, int>();
        var retaliation = new HashSet<string>();
        var kills = new Dictionary<Pawn, Pawn>();
        var ticks = 0;
        // No retained fixture state: a local patch callback is removed in finally, even on native failure.
        var patch = new Harmony("rimgovernor.test.hunting-prediction");
        ShotObserver = (p, shot) =>
        {
            if (!ids.ContainsKey(p)) return;
            first.TryAdd(p, Find.TickManager.TicksGame - start);
            if (shot) last[p] = Find.TickManager.TicksGame - start;
        };
        KillObserver = (victim, killer) => { if (ids.ContainsKey(victim) && ids.ContainsKey(killer)) kills.TryAdd(victim, killer); };
        ObservedSky = (float)request.Environment!.SkyGlow;
        var seeded = false;
        try
        {
            patch.Patch(AccessTools.DeclaredMethod(typeof(Verb_LaunchProjectile), "TryCastShot"), postfix: new HarmonyMethod(typeof(HuntingPredictionFixture), nameof(Shot)));
            patch.Patch(AccessTools.DeclaredMethod(typeof(Verb_MeleeAttack), "TryCastShot"), postfix: new HarmonyMethod(typeof(HuntingPredictionFixture), nameof(Melee)));
            patch.Patch(AccessTools.DeclaredMethod(typeof(Pawn), "Kill"), prefix: new HarmonyMethod(typeof(HuntingPredictionFixture), nameof(Killed)));
            patch.Patch(AccessTools.PropertyGetter(typeof(SkyManager), nameof(SkyManager.CurSkyGlow)), prefix: new HarmonyMethod(typeof(HuntingPredictionFixture), nameof(PinSky)));
            Rand.PushState(seed);
            seeded = true;
            while (ticks < horizon && actors.Take(hunterCount).Any(p => p.Spawned && !p.Dead && !p.Downed) &&
                actors.Skip(hunterCount).Any(p => p.Spawned && !p.Dead && !p.Downed && (p == actors[hunterCount] || p.HostileTo(Faction.OfPlayer))))
            {
                if (stratum == "departure" && ticks == 300 && actors[1].Spawned && !actors[1].Downed)
                {
                    actors[1].jobs.EndCurrentJob(JobCondition.InterruptForced);
                    actors[1].ExitMap(false, Rot4.Invalid);
                    left[actors[1]] = ticks;
                }
                foreach (var hunter in actors.Take(hunterCount).Where(p => p.Spawned && !p.Downed && !p.Dead))
                {
                    var prey = actors[hunterCount];
                    // Vanilla Hunt reserves its victim for one pawn, and a forced order steals that reservation, so several
                    // Hunt-ordered pawns would interrupt each other forever. A group is drafted and focus-fires, as the colony squad does.
                    var group = hunterCount > 1;
                    if (group && !hunter.Drafted) hunter.drafter.Drafted = true;
                    var jobDef = group ? JobDefOf.AttackStatic : JobDefOf.Hunt;
                    if (prey.Spawned && !prey.Dead && !prey.Downed &&
                        (hunter.CurJob?.def != jobDef || hunter.CurJob.targetA.Thing != prey))
                    {
                        var job = JobMaker.MakeJob(jobDef, prey);
                        job.ignoreDesignations = true;
                        hunter.jobs.TryTakeOrderedJob(job);
                    }
                }
                foreach (var p in actors.Where(p => p.Spawned)) p.Drawer.tweener.ResetTweenedPosToRoot();
                Find.TickManager.DoSingleTick();
                ticks++;
                foreach (var p in actors.Skip(hunterCount).Where(p => p.InMentalState)) retaliation.Add(ids[p] + ":" + p.MentalStateDef.defName);
                foreach (var p in actors.Where(p => !p.Spawned && !p.Dead)) left.TryAdd(p, ticks);
            }
        }
        finally { if (seeded) Rand.PopState(); patch.UnpatchAll(patch.Id); ShotObserver = null; KillObserver = null; }
        var outcome = new EncounterSample(seed, ticks, ticks == horizon, actors.Select(p => new EncounterActorOutcome(
            ids[p], p.Downed, p.Dead, p.health.summaryHealth.SummaryHealthPercent, p.health.hediffSet.BleedRateTotal,
            p.health.HasHediffsNeedingTend(), p.health.hediffSet.hediffs.Where(h => h is not Hediff_MissingPart)
                .Select(h => new EncounterInjury(h.def.defName, h.Part?.Label ?? "", h.Severity, h.BleedRate, h.TendableNow(), h.IsTended(), h.IsPermanent())).ToArray(),
            Missing(p), kills.Where(k => k.Value == p && k.Key.Dead).Select(k => ids[k.Key]).Order().ToArray(), p.Dead ? new(p.RaceProps.meatDef?.defName ?? "", p.GetStatValue(StatDefOf.MeatAmount),
                p.RaceProps.leatherDef?.defName ?? "", p.GetStatValue(StatDefOf.LeatherAmount)) : null,
            first.TryGetValue(p, out var attack) ? attack : null, last.TryGetValue(p, out var shot) ? shot : null,
            stratum == "departure" && p == actors[1] && left.ContainsKey(p) ? 300 : null,
            left.TryGetValue(p, out var exit) ? exit : null)).ToArray());
        return new { request, observed = outcome, nativeRunId = Guid.NewGuid().ToString("N"), nativeBuild = build, nativeMods = mods,
            predictorMods = Mods(true), huntRead, revengeFactor = revenge, retaliation = retaliation.Order().ToArray(), initialStateMatched = true,
            note = "Live-game observation; no hypothetical Arena execution or future-RNG read." };
    }

    private static Action<Pawn, bool>? ShotObserver;
    private static Action<Pawn, Pawn>? KillObserver;
    private static float ObservedSky;
    private static bool PinSky(ref float __result) { __result = ObservedSky; return false; }
    private static void Killed(Pawn __instance, DamageInfo? dinfo) { if (!__instance.Dead && dinfo?.Instigator is Pawn killer) KillObserver?.Invoke(__instance, killer); }
    private static void Shot(Verb __instance, bool __result) { if (__result && __instance.CasterPawn is { } p) ShotObserver?.Invoke(p, true); }
    private static void Melee(Verb __instance, bool __result) { if (__result && __instance.CasterPawn is { } p) ShotObserver?.Invoke(p, false); }
    private static string[] Missing(Pawn p) => p.health.hediffSet.hediffs.OfType<Hediff_MissingPart>()
        .Where(h => h.Part.parent == null || !p.health.hediffSet.PartIsMissing(h.Part.parent)).Select(h => h.Part.Label).ToArray();
    private static EncounterActor Observe(Pawn p, string id, bool hunter, bool departure, bool herd) => new()
    {
        Id = id, Kind = p.kindDef.defName, Wild = !hunter, X = p.Position.x, Z = p.Position.z,
        Age = p.ageTracker.AgeBiologicalYearsFloat, Xenotype = p.genes?.Xenotype?.defName ?? "", Herd = !hunter && herd ? "wild-herd" : "",
        HuntTarget = hunter ? "p0" : "", Weapon = Gear(p.equipment?.Primary),
        Apparel = p.apparel?.WornApparel.Select(a => Gear(a)!).ToArray() ?? [],
        Skills = p.skills?.skills.ToDictionary(s => s.def.defName, s => s.Level) ?? new Dictionary<string, int>(),
        Traits = p.story?.traits.allTraits.Select(t => new EncounterTrait(t.def.defName, t.Degree)).ToArray() ?? [],
        Health = p.health.hediffSet.hediffs.Where(h => h is not Hediff_MissingPart)
            .Select(h => new EncounterHediff(h.def.defName, h.Part?.Label ?? "", h.Severity)).ToArray(), MissingParts = Missing(p),
        Departure = departure ? new(300, EncounterDepartureKind.Leave) : null,
    };
    private static EncounterGear? Gear(ThingWithComps? thing)
    {
        if (thing == null) return null;
        thing.TryGetQuality(out var quality);
        return new(thing.def.defName, thing.Stuff?.defName ?? "", quality.ToString(), thing.HitPoints);
    }
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Mods(bool officialOnly = false)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string s) => hash.AppendData(Encoding.UTF8.GetBytes(s + "\n"));
        foreach (var mod in LoadedModManager.RunningModsListForReading.Where(m => !officialOnly || m.PackageId.StartsWith("ludeon.rimworld", StringComparison.Ordinal)).OrderBy(m => m.PackageId, StringComparer.Ordinal))
        {
            Add(mod.PackageId);
            foreach (var file in Directory.EnumerateFiles(mod.RootDir, "*", SearchOption.AllDirectories)
                .Where(f => string.Equals(Path.GetExtension(f), ".xml", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(f), ".dll", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            { Add(Path.GetRelativePath(mod.RootDir, file).Replace('\\', '/')); Add(HashFile(file)); }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
