using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    public sealed class LabStartFixture
    {
        static LabStartFixture() { LabStart.EnsurePatched(); }
        public LabStartFixture() { LabStart.EnsurePatched(); }

        [Tool("test/lab_crew", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: staff the loaded map with a crew of superpawns. Tops the free colonists up to size with fixture-made adults beside the first colonist (a larger colony is left as it is), then makes every free colonist capable of every work type (backstory work restrictions dropped), sets every skill to level (or only skill when it names one SkillDef) with no passion lost, and replaces the traits with the traits list of TraitDef[:degree] (empty leaves traits alone). Replies the colonist count and how many were added.")]
        public async Task<object> Crew(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Colonists the crew holds at least; 0 adds none.")] int size = 0,
            [ToolParameter(Description = "Level 0..20 for the skills.")] int level = 20,
            [ToolParameter(Description = "One SkillDef name to set; empty sets every skill.")] string skill = "",
            [ToolParameter(Description = "Comma list of TraitDef[:degree], e.g. SpeedOffset:2,Industriousness:2; empty skips.")] string traits = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");
                if (level < 0 || level > 20) throw new ArgumentException("level must be within 0..20.");
                if (size < 0 || size > 64) throw new ArgumentException("size must be within 0..64.");
                var skillDef = skill == "" ? null : DefDatabase<SkillDef>.GetNamedSilentFail(skill) ?? throw new ArgumentException($"No SkillDef named {skill}.");
                var wanted = new List<Trait>();
                foreach (var part in (traits ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Trim().Split(':');
                    var traitDef = DefDatabase<TraitDef>.GetNamedSilentFail(pair[0]) ?? throw new ArgumentException($"No TraitDef named {pair[0]}.");
                    wanted.Add(new Trait(traitDef, pair.Length > 1 ? int.Parse(pair[1]) : 0, true));
                }
                var open = new { Childhood = DefDatabase<BackstoryDef>.AllDefs.First(b => b.slot == BackstorySlot.Childhood && b.workDisables == WorkTags.None), Adulthood = DefDatabase<BackstoryDef>.AllDefs.First(b => b.slot == BackstorySlot.Adulthood && b.workDisables == WorkTags.None) };
                var crew = map.mapPawns.FreeColonistsSpawned.ToList();
                var home = crew.Count > 0 ? crew[0].Position : map.Center;
                var added = 0;
                while (crew.Count < size)
                {
                    var pawn = LabStart.Colonist(crew.Count);
                    GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(home, map, 8), map);
                    crew.Add(pawn);
                    added++;
                }
                foreach (var pawn in crew)
                {
                    if (pawn.story.Childhood != null && pawn.story.Childhood.workDisables != WorkTags.None) pawn.story.Childhood = open.Childhood;
                    if (pawn.story.Adulthood != null && pawn.story.Adulthood.workDisables != WorkTags.None) pawn.story.Adulthood = open.Adulthood;
                    foreach (var s in pawn.skills.skills)
                        if (skillDef == null || s.def == skillDef) { s.Level = level; s.xpSinceLastLevel = 0f; }
                    if (wanted.Count > 0)
                    {
                        foreach (var trait in pawn.story.traits.allTraits.ToList()) pawn.story.traits.RemoveTrait(trait);
                        foreach (var trait in wanted) pawn.story.traits.GainTrait(new Trait(trait.def, trait.Degree, true));
                    }
                    pawn.Notify_DisabledWorkTypesChanged();
                    pawn.workSettings.EnableAndInitialize();
                }
                return new { success = true, colonists = crew.Count, added };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/lab_start", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup (#730): wipe the loaded map to bare Soil (no things, plants, filth, zones, roofs, snow or fog; every pawn removed), spawn N fixture-made adult colonists (fixed skills, every work type on, no traits) at the centre, lock clear weather and a 21 C outdoor temperature, and quiet the storyteller. Persists across save and reload. Replies the map size, colonist ids, centre cell and a digest of the map.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Fixture colonists to spawn, 1..8 (default 3).")] int colonists = LabStart.DefaultColonists,
            [ToolParameter(Description = "wipe (default), or digest, which only reads the current map's digest.")] string action = "wipe")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");
                if (action == "digest") return new { success = true, mapSize = map.Size.x, lab = !float.IsNaN(AcceptanceWorld.Lab), digest = LabStart.Digest(map) };
                if (action != "wipe") throw new ArgumentException("Unknown action.");
                return LabStart.Wipe(map, colonists);
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/lab_spawn", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup (#743): spawn one finished thing at a cell of the loaded map, the lab contract runner's single-building or single-pawn helper. def is a PawnKindDef (a generated pawn) or a ThingDef (a building, or an item stack of count). A pawn may be given a fixed gender and age. Replies the spawned thing's load id, kind and cell.")]
        public async Task<object> Spawn(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "PawnKindDef or ThingDef name.")] string def,
            [ToolParameter(Description = "Cell x.")] int x,
            [ToolParameter(Description = "Cell z.")] int z,
            [ToolParameter(Description = "Stuff ThingDef for a stuffed building; empty takes the def's default stuff.")] string stuff = "",
            [ToolParameter(Description = "Rotation 0..3 (north, east, south, west).")] int rotation = 0,
            [ToolParameter(Description = "Item stack count (items only, clamped to the stack limit).")] int count = 1,
            [ToolParameter(Description = "player (default) or none: the spawned thing's faction.")] string faction = "player",
            [ToolParameter(Description = "Pawn only: male or female; empty leaves it generated.")] string gender = "",
            [ToolParameter(Description = "Pawn only: biological and chronological age in years; 0 leaves it generated.")] float age = 0f)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");
                if (faction != "player" && faction != "none") throw new ArgumentException("faction must be player or none.");
                if (gender != "" && gender != "male" && gender != "female") throw new ArgumentException("gender must be male, female or empty.");
                if (age < 0f) throw new ArgumentException("age must not be negative.");
                if (rotation < 0 || rotation > 3) throw new ArgumentException("rotation must be within 0..3.");
                var cell = new IntVec3(x, 0, z);
                if (!cell.InBounds(map)) throw new ArgumentException($"Cell {x},{z} is out of bounds.");
                var owner = faction == "player" ? Faction.OfPlayer : null;
                var rot = new Rot4(rotation);
                Thing thing;
                string kind;
                var pawnKind = DefDatabase<PawnKindDef>.GetNamedSilentFail(def ?? "");
                if (pawnKind != null)
                {
                    thing = PawnGenerator.GeneratePawn(new PawnGenerationRequest(pawnKind, owner, forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowAddictions: false,
                        fixedGender: gender == "" ? (Gender?)null : gender == "male" ? Gender.Male : Gender.Female,
                        fixedBiologicalAge: age > 0f ? age : (float?)null, fixedChronologicalAge: age > 0f ? age : (float?)null));
                    kind = "pawn";
                }
                else
                {
                    var thingDef = DefDatabase<ThingDef>.GetNamedSilentFail(def ?? "") ?? throw new ArgumentException($"No PawnKindDef or ThingDef named {def}.");
                    if (thingDef.category != ThingCategory.Building && thingDef.category != ThingCategory.Item && thingDef.category != ThingCategory.Plant) throw new ArgumentException($"{def} is neither a building, an item nor a plant.");
                    ThingDef stuffDef = null;
                    if (thingDef.MadeFromStuff)
                        stuffDef = stuff == "" ? GenStuff.DefaultStuffFor(thingDef) : DefDatabase<ThingDef>.GetNamedSilentFail(stuff) ?? throw new ArgumentException($"No stuff named {stuff}.");
                    if (!GenAdj.OccupiedRect(cell, rot, thingDef.size).InBounds(map)) throw new ArgumentException($"{def} at {x},{z} does not fit the map.");
                    thing = ThingMaker.MakeThing(thingDef, stuffDef);
                    if (thingDef.category == ThingCategory.Item) thing.stackCount = Math.Max(1, Math.Min(count, thingDef.stackLimit));
                    else if (thing is Plant plant) plant.Growth = 1f;
                    else if (owner != null) thing.SetFaction(owner);
                    kind = thingDef.category == ThingCategory.Item ? "item" : thingDef.category == ThingCategory.Plant ? "plant" : "building";
                }
                GenSpawn.Spawn(thing, cell, map, rot);
                return new { success = true, id = thing.GetUniqueLoadID(), thingId = thing.ThingID, kind, def = thing.def.defName,
                    stuff = thing.Stuff?.defName, cell = new { x = thing.Position.x, z = thing.Position.z }, stackCount = thing.stackCount };
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // A combat lab fight is staged by test/stage (StageFixture); test/lab_stage reads, ticks and reseeds it.
    public static class LabStage
    {
        // Reseed (#2849) sets the game's Rand seed from spec.seed after a checkpoint
        // load, so one staged save serves runs seeds 1..N. It uses the same
        // derivation as the post-staging reseed, so a seed means one RNG start. The
        // reply echoes the seed and the next four draws, read without consuming them.
        public static object Reseed(JObject spec)
        {
            var seed = (int?)spec["seed"] ?? 0;
            if (seed == 0) throw new ArgumentException("reseed needs a non-zero spec seed.");
            Rand.Seed = StageSeed.After(seed);
            var draws = new List<int>();
            Rand.PushState();
            try { for (var i = 0; i < 4; i++) draws.Add(Rand.Int); }
            finally { Rand.PopState(); }
            return new { success = true, seed, draws, tick = Find.TickManager.TicksGame };
        }
    }

    // Every hit on a pawn since the last lab staging: the same
    // Thing.TakeDamage edge CombatInjuryHook and NativeCombatCausality patch,
    // attributed by DamageInfo.Instigator's side. A colonist hitting a
    // colonist is friendly fire. Patched by the first staging; bounded.
    public static class LabDamageLedger
    {
        private const int Cap = 4096;
        private static readonly List<object> rows = new List<object>();
        private static bool patched;
        public static int Dropped { get; private set; }

        public static void Reset()
        {
            rows.Clear();
            Dropped = 0;
            EnsurePatched();
        }

        // Installed when the bridge discovers test/lab_stage, so a run that
        // resumes a staged bundle records damage without staging (#2854).
        public static void EnsurePatched()
        {
            if (patched) return;
            new Harmony("rimgovernor.fixture.lab-damage").Patch(AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage), new[] { typeof(DamageInfo) }),
                postfix: new HarmonyMethod(typeof(LabDamageLedger), nameof(Postfix)));
            patched = true;
        }

        public static List<object> Rows() => rows.ToList();

        private static string Side(Thing t) => t == null ? "" : t.Faction == Faction.OfPlayer ? "colonist" : t.HostileTo(Faction.OfPlayer) ? "hostile" : "other";

        public static void Postfix(Thing __instance, DamageInfo dinfo, DamageWorker.DamageResult __result)
        {
            if (!(__instance is Pawn victim) || float.IsNaN(AcceptanceWorld.Lab)) return;
            if (rows.Count >= Cap) { Dropped++; return; }
            var by = dinfo.Instigator;
            rows.Add(new { tick = Find.TickManager.TicksGame, victim = victim.GetUniqueLoadID(), victimSide = Side(victim), instigator = by?.GetUniqueLoadID() ?? "",
                instigatorSide = Side(by), weapon = dinfo.Weapon?.defName ?? "", dealt = __result?.totalDamageDealt ?? 0f, downed = victim.Downed, dead = victim.Dead });
        }
    }

    // Shot-level tap for combat lab runs (#2917): every projectile launched
    // (tick, shooter, intended target, destination cell, burst position), its
    // impact (tick, thing hit), and the per-pawn opening milestones (jobs,
    // draft changes, stances) plus the first Pawn_JobTracker.JobTrackerTickInterval
    // ticks of each pawn (the think cadence). Observation only: nothing it
    // reads is written back. Installed with the damage ledger, bounded, one
    // compact row per event; replied by test/lab_stage action shots.
    public static class LabShotLedger
    {
        private const int Cap = 16384;
        private const int ThinkTicksPerPawn = 8;
        private static readonly List<object> rows = new List<object>();
        private static readonly Dictionary<string, List<int>> thinks = new Dictionary<string, List<int>>();
        private static readonly Dictionary<string, int> thinkCounts = new Dictionary<string, int>();
        private static bool patched;
        [ThreadStatic] private static int burstIndex;
        [ThreadStatic] private static int burstSize;
        public static int Dropped { get; private set; }

        public static void Reset()
        {
            rows.Clear(); thinks.Clear(); thinkCounts.Clear();
            Dropped = 0;
            EnsurePatched();
        }

        public static void EnsurePatched()
        {
            if (patched) return;
            var harmony = new Harmony("rimgovernor.fixture.lab-shots");
            harmony.Patch(AccessTools.Method(typeof(Verb), "TryCastNextBurstShot"), prefix: new HarmonyMethod(typeof(LabShotLedger), nameof(BurstPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Launch),
                new[] { typeof(Thing), typeof(UnityEngine.Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) }),
                postfix: new HarmonyMethod(typeof(LabShotLedger), nameof(LaunchPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Projectile), "Impact"), prefix: new HarmonyMethod(typeof(LabShotLedger), nameof(ImpactPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Verse.AI.Pawn_JobTracker), "StartJob"), postfix: new HarmonyMethod(typeof(LabShotLedger), nameof(StartJobPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Verse.AI.Pawn_JobTracker), nameof(Verse.AI.Pawn_JobTracker.JobTrackerTickInterval)), prefix: new HarmonyMethod(typeof(LabShotLedger), nameof(ThinkPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.SetStance)), postfix: new HarmonyMethod(typeof(LabShotLedger), nameof(StancePostfix)));
            harmony.Patch(AccessTools.PropertySetter(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted)), postfix: new HarmonyMethod(typeof(LabShotLedger), nameof(DraftPostfix)));
            patched = true;
        }

        // The rows in order, then one think row per pawn: its first
        // JobTrackerTickInterval ticks and how many it had.
        public static List<object> Rows()
        {
            var all = rows.ToList();
            foreach (var kv in thinks.OrderBy(k => k.Key, StringComparer.Ordinal))
                all.Add(new { k = "think", pawn = kv.Key, ticks = kv.Value, count = thinkCounts[kv.Key] });
            return all;
        }

        private static bool On(Thing t) => !float.IsNaN(AcceptanceWorld.Lab) && t != null && t.Map != null && t.Map == Find.CurrentMap;
        private static int Now => Find.TickManager.TicksGame;
        private static string Id(Thing t) => t?.GetUniqueLoadID() ?? "";
        private static string Side(Thing t) => t == null ? "" : t.Faction == Faction.OfPlayer ? "colonist" : t.HostileTo(Faction.OfPlayer) ? "hostile" : "other";

        private static void Add(object row)
        {
            if (rows.Count >= Cap) { Dropped++; return; }
            rows.Add(row);
        }

        public static void BurstPrefix(Verb __instance, int ___burstShotsLeft)
        {
            burstSize = Traverse.Create(__instance).Property("ShotsPerBurst").GetValue<int>();
            burstIndex = burstSize - ___burstShotsLeft;
        }

        public static void LaunchPostfix(Projectile __instance, UnityEngine.Vector3 ___destination, int ___ticksToImpact)
        {
            var by = __instance.Launcher;
            if (!On(by)) return;
            var dest = ___destination.ToIntVec3();
            var from = by.Position;
            var target = __instance.intendedTarget;
            Add(new { k = "shot", tick = Now, id = __instance.thingIDNumber, shooter = Id(by), side = Side(by), x = from.x, z = from.z,
                target = Id(target.Thing), tx = target.Cell.x, tz = target.Cell.z, dx = dest.x, dz = dest.z, burst = burstIndex, of = burstSize,
                weapon = __instance.EquipmentDef?.defName ?? "", proj = __instance.def.defName, tti = ___ticksToImpact });
        }

        public static void ImpactPrefix(Projectile __instance, Thing hitThing)
        {
            if (!On(__instance)) return;
            var at = __instance.Position;
            Add(new { k = "impact", tick = Now, id = __instance.thingIDNumber, hit = Id(hitThing), x = at.x, z = at.z });
        }

        public static void StartJobPostfix(Pawn ___pawn, Verse.AI.Job newJob)
        {
            if (!On(___pawn) || newJob == null) return;
            var tgt = newJob.targetA;
            Add(new { k = "job", tick = Now, pawn = Id(___pawn), side = Side(___pawn), job = newJob.def?.defName ?? "", forced = newJob.playerForced,
                thing = Id(tgt.Thing), x = tgt.Cell.x, z = tgt.Cell.z });
        }

        public static void ThinkPrefix(Pawn ___pawn, int delta)
        {
            if (!On(___pawn)) return;
            var id = Id(___pawn);
            thinkCounts.TryGetValue(id, out var n);
            thinkCounts[id] = n + 1;
            if (n >= ThinkTicksPerPawn) return;
            if (!thinks.TryGetValue(id, out var ticks)) thinks[id] = ticks = new List<int>();
            ticks.Add(Now);
        }

        public static void StancePostfix(Pawn ___pawn, Stance newStance)
        {
            if (!On(___pawn) || !(newStance is Stance_Busy busy)) return;
            var t = busy.focusTarg;
            Add(new { k = "stance", tick = Now, pawn = Id(___pawn), side = Side(___pawn), stance = newStance.GetType().Name, left = busy.ticksLeft,
                thing = Id(t.Thing), x = t.Cell.x, z = t.Cell.z });
        }

        public static void DraftPostfix(Pawn_DraftController __instance)
        {
            var pawn = __instance.pawn;
            if (!On(pawn)) return;
            Add(new { k = "draft", tick = Now, pawn = Id(pawn), drafted = __instance.Drafted });
        }
    }

    public sealed class LabStageFixture
    {
        // A tick call holds the main thread; 2000 ticks of a lab skirmish is
        // well under the call ceiling.
        private const int MaxTicks = 2000;
        static LabStageFixture() { LabDamageLedger.EnsurePatched(); LabShotLedger.EnsurePatched(); }
        public LabStageFixture() { LabDamageLedger.EnsurePatched(); LabShotLedger.EnsurePatched(); }

        [Tool("test/lab_stage", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup (#854): read, tick or reseed a combat lab fight test/stage staged. action read replies every pawn's cell, side, downed/dead state, current job (def, playerForced, target cell or thing), drafted and fire-at-will, worn shield energy, hediff defs, every player door's hold-open and forbidden flag, the damage ledger since the last staging, and the tick.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Action spec JSON (reseed and orphan-animal).")] string spec = "{}",
            [ToolParameter(Description = "read (default), reseed (spec {seed: non-zero}: set the game Rand seed the way a seeded staging ends, reply the next four draws; #2849), orphan-animal (spec animal and mode: despawn, dead or foreign), shots (the shot tap since the last staging: launches, impacts, jobs, stances, draft changes and each pawn's first think ticks, #2917), or tick: run ticks synchronous game ticks on the paused game, then read.")] string action = "read",
            [ToolParameter(Description = "Ticks for action tick, 1..2000.")] int ticks = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("A loaded game with a current map is required.");
                if (float.IsNaN(AcceptanceWorld.Lab)) throw new InvalidOperationException("test/lab_stage needs a lab map (test/lab_start first).");
                if (action == "orphan-animal")
                {
                    var setup = JObject.Parse(spec);
                    var animal = map.mapPawns.AllPawnsSpawned.First(p => p.GetUniqueLoadID() == (string)setup["animal"]);
                    if (!animal.RaceProps.Animal || animal.Faction != Faction.OfPlayer) throw new ArgumentException("Expected a player animal.");
                    switch ((string)setup["mode"])
                    {
                        case "despawn": animal.DeSpawn(); break;
                        case "dead": animal.Kill(null); break;
                        case "foreign": animal.SetFaction(null); break;
                        default: throw new ArgumentException("Expected despawn, dead or foreign.");
                    }
                    action = "read";
                }
                if (action == "shots")
                    return new { success = true, tick = Find.TickManager.TicksGame, shots = LabShotLedger.Rows(), shotsDropped = LabShotLedger.Dropped };
                if (action == "reseed")
                    return LabStage.Reseed(JObject.Parse(spec ?? "{}"));
                if (action == "tick")
                {
                    if (ticks < 1 || ticks > MaxTicks) throw new ArgumentException($"ticks must be within 1..{MaxTicks}.");
                    if (!Find.TickManager.Paused) throw new InvalidOperationException("tick needs a paused game.");
                    for (var i = 0; i < ticks; i++) Find.TickManager.DoSingleTick();
                    action = "read";
                }
                if (action == "read")
                    return new { success = true, tick = Find.TickManager.TicksGame, pawns = map.mapPawns.AllPawns.Where(p => p.Spawned || p.Corpse?.Spawned == true).Select(p => new {
                        id = p.GetUniqueLoadID(), side = p.Faction == Faction.OfPlayer ? "colonist" : "hostile", x = p.PositionHeld.x, z = p.PositionHeld.z, downed = p.Downed, dead = p.Dead,
                        fleeing = !p.Dead && (p.MentalStateDef == MentalStateDefOf.PanicFlee || p.CurJobDef == JobDefOf.Flee || p.CurJobDef == JobDefOf.FleeAndCower),
                        mental = p.MentalStateDef?.defName, suspended = p.Suspended, awake = p.Awake(),
                        job = p.CurJobDef?.defName, playerForced = p.CurJob?.playerForced == true, jobCell = p.CurJob == null || p.CurJob.targetA.HasThing ? null : new { x = p.CurJob.targetA.Cell.x, z = p.CurJob.targetA.Cell.z },
                        jobThing = p.CurJob?.targetA.Thing?.GetUniqueLoadID(), lordJob = p.GetLord()?.LordJob?.GetType().Name, lordToil = p.GetLord()?.CurLordToil?.GetType().Name, drafted = p.Drafted, fireAtWill = p.drafter?.FireAtWill,
                        shield = p.apparel?.WornApparel.Select(a => a.GetComp<CompShield>()).FirstOrDefault(c => c != null)?.Energy,
                        area = p.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label, areaCells = p.playerSettings?.AreaRestrictionInPawnCurrentMap?.TrueCount ?? 0,
                        hediffs = p.health.hediffSet.hediffs.Select(h => h.def.defName).Distinct().ToList() }).ToList(),
                        doors = map.listerBuildings.allBuildingsColonist.OfType<Building_Door>().Select(d => new { x = d.Position.x, z = d.Position.z, open = d.Open, holdOpen = d.HoldOpen, forbidden = d.IsForbidden(Faction.OfPlayer) }).ToList(),
                        areas = map.areaManager.AllAreas.Select(a => a.Label).ToList(),
                        damage = LabDamageLedger.Rows(), damageDropped = LabDamageLedger.Dropped };
                throw new ArgumentException("Unknown action.");
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public sealed class DebugStartFixture
    {
        [Tool("test/debug_map_census", Description = "Disposable read: how many spawned non-player buildings (natural rock included) the current map carries map-wide and within Home. Lets a case record that a read scoped to Home ran past the old map-wide 8192 bound (#414).")]
        public async Task<object> Census(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap ?? throw new InvalidOperationException("No current map.");
                var player = Faction.OfPlayerSilentFail;
                var home = map.areaManager.Home;
                var mapWide = map.listerThings.AllThings.OfType<Building>().Count(b => b.Spawned && b.Faction != player);
                var inHome = home.ActiveCells.SelectMany(c => c.GetThingList(map)).OfType<Building>()
                    .Where(b => b.Spawned && b.Faction != player).Select(b => b.thingIDNumber).Distinct().Count();
                return new { success = true, mapCells = map.cellIndices.NumGridCells, nonPlayerBuildings = mapWide, homeCells = home.TrueCount, homeNonPlayerBuildings = inHome };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
