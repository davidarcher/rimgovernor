using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // A disposable hunt chain: one ranger with a bow, a corpse stockpile and six wild deer
    // designated for hunting. Only the initial state is staged; the shots are native. The
    // fixture records what the hunter is doing two ticks after each wild kill, through its own
    // hooks rather than the rule runtime's, so the case compares native rules with an
    // independent reading: a rule firing leaves the hunter on a Hunt job against another live
    // designated deer, vanilla leaves it on the corpse (or a haul).
    public sealed class HuntChainRuleFixture
    {
        private sealed class Kill { internal int Tick; internal string Deer = ""; internal bool Recorded; }
        private static Map tracked;
        private static Pawn hunter;
        private static readonly List<Kill> kills = new List<Kill>();
        private static readonly List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        private static bool patched;

        [Tool("test/hunt_chain_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Stage one ranger with a bow, a corpse stockpile and six wild deer designated for hunting, and start recording the hunter's job two ticks after each wild kill.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused map required.");
                var ranger = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Dead && !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting));
                if (ranger == null) throw new InvalidOperationException("Need a colonist who can hunt.");
                foreach (var zone in map.zoneManager.AllZones.ToList()) zone.Delete();
                foreach (var thing in map.listerThings.AllThings.ToList())
                    if (thing != ranger) thing.Destroy(DestroyMode.Vanish);
                int mid = map.Size.z / 2;
                ranger.jobs.StopAll(); ranger.drafter.Drafted = false;
                ranger.inventory?.innerContainer.ClearAndDestroyContents(); ranger.carryTracker?.innerContainer.ClearAndDestroyContents();
                foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                    if (!ranger.WorkTypeIsDisabled(work)) ranger.workSettings.SetPriority(work, 0);
                ranger.workSettings.SetPriority(WorkTypeDefOf.Hunting, 1);
                for (int hour = 0; hour < 24; hour++) ranger.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                foreach (var cell in map.AllCells) { map.roofGrid.SetRoof(cell, null); map.fogGrid.Unfog(cell); }
                ranger.Position = new IntVec3(20, 0, mid); ranger.pather.StopDead();
                ranger.equipment.DestroyAllEquipment();
                var bowDef = DefDatabase<ThingDef>.GetNamed("Bow_Short");
                ranger.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(bowDef, GenStuff.DefaultStuffFor(bowDef)));
                var food = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack); food.stackCount = 8;
                GenSpawn.Spawn(food, new IntVec3(22, 0, mid + 2), map); food.SetForbidden(false, false);

                var stockpile = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(stockpile);
                stockpile.settings.filter.SetAllowAll(null);
                for (int x = 12; x <= 16; x++) for (int z = mid - 2; z <= mid + 2; z++) stockpile.AddCell(new IntVec3(x, 0, z));

                var deerKind = DefDatabase<PawnKindDef>.GetNamed("Deer");
                for (int i = 0; i < 6; i++)
                {
                    var deer = PawnGenerator.GeneratePawn(new PawnGenerationRequest(deerKind, null, fixedBiologicalAge: 6f));
                    GenSpawn.Spawn(deer, new IntVec3(34 + 8 * i, 0, mid + (i % 2 == 0 ? -3 : 3)), map);
                    map.designationManager.AddDesignation(new Designation(deer, DesignationDefOf.Hunt));
                }

                tracked = map; hunter = ranger; kills.Clear(); records.Clear();
                if (!patched)
                {
                    var harmony = new Harmony("rimgovernor.test.hunt-chain-rule");
                    harmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.Kill)), prefix: new HarmonyMethod(typeof(HuntChainRuleFixture), nameof(Killing)), postfix: new HarmonyMethod(typeof(HuntChainRuleFixture), nameof(Killed)));
                    harmony.Patch(AccessTools.Method(typeof(TickManager), "DoSingleTick"), postfix: new HarmonyMethod(typeof(HuntChainRuleFixture), nameof(AfterTick)));
                    patched = true;
                }
                return new { success = true, hunter = ranger.GetUniqueLoadID(), deer = Designated(map).Count };
            }, cancellationToken);
        }

        private static List<Pawn> Designated(Map map) =>
            map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.Faction == null && p.RaceProps.Animal && map.designationManager.DesignationOn(p, DesignationDefOf.Hunt) != null).ToList();

        // Pawn.Kill despawns its victim before postfixes run; capture map ownership first.
        private static void Killing(Pawn __instance, out bool __state)
        {
            __state = tracked != null && __instance.Faction == null && __instance.RaceProps.Animal && __instance.Map == tracked;
        }

        private static void Killed(Pawn __instance, bool __state)
        {
            if (!__state || !__instance.Dead) return;
            kills.Add(new Kill { Tick = Find.TickManager.TicksGame, Deer = __instance.GetUniqueLoadID() });
        }

        // Two ticks after a kill the vanilla job has moved on to the corpse; a native rule has
        // already replaced it. Either way the job the hunter holds is read as it stands.
        private static void AfterTick()
        {
            if (tracked == null || hunter == null) return;
            var now = Find.TickManager.TicksGame;
            foreach (var kill in kills)
            {
                if (kill.Recorded || now < kill.Tick + 2) continue;
                kill.Recorded = true;
                var job = hunter.CurJob;
                var target = job?.targetA.Thing;
                var prey = target as Pawn;
                var live = prey != null && !prey.Dead && prey.Spawned;
                records.Add(new Dictionary<string, object> {
                    { "killTick", kill.Tick }, { "killed", kill.Deer }, { "jobDef", hunter.CurJobDef?.defName ?? "" },
                    { "targetId", target?.GetUniqueLoadID() ?? "" }, { "targetLive", live },
                    { "targetDesignated", live && tracked.designationManager.DesignationOn(prey, DesignationDefOf.Hunt) != null },
                    { "targetIsKilled", target != null && (target.GetUniqueLoadID() == kill.Deer || (target is Corpse c && c.InnerPawn.GetUniqueLoadID() == kill.Deer)) },
                    { "carryingCorpse", hunter.carryTracker?.CarriedThing is Corpse },
                });
            }
        }

        [Tool("test/hunt_chain_observe", Description = "Read the hunter's job record taken two ticks after each wild kill, and the designated deer still alive; no mutations.")]
        public async Task<object> Observe(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => new {
                success = true, tick = Find.TickManager.TicksGame, kills = kills.Count, records = records.ToList(),
                designatedAlive = tracked == null ? 0 : Designated(tracked).Count,
            }, cancellationToken);
        }
    }
}
