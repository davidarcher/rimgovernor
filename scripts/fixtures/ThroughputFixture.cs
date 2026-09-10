using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Test-only onset measurement. No pawn stats, simulation ticks or resources are edited.
    public sealed class ThroughputFixture
    {
        private static Game session;
        private static int deadline;
        private static int? onset;
        private static long? onsetAtMs;
        private static Pawn animal;
        private static string operation;
        private static bool applied;
        private static bool patched;

        [Tool("test/render_suspend", Description = "Private acceptance only: lease camera suspension for at most 30 seconds; zero releases. No game clock changes.")]
        public async Task<object> Render(IRimBridgeContext ctx, CancellationToken cancellationToken, int seconds = 0)
        {
            if (seconds < 0 || seconds > 30) throw new ArgumentOutOfRangeException(nameof(seconds));
            return await ctx.MainThread.InvokeAsync(() =>
            {
                RenderDemandDriver.TestSuspendUntil = seconds == 0 ? 0 : UnityEngine.Time.realtimeSinceStartup + seconds;
                return RenderDemandDriver.Lease(0);
            }, cancellationToken);
        }

        [Tool("test/throughput_event", Description = "Disposable tick-scheduled native pause, speed change or animal mental-state onset; excluded from production builds.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string op = "status", int afterTicks = 7)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                if (op != "status")
                {
                    if (op != "pause" && op != "speed" && op != "hostile") throw new ArgumentException("Unknown fixture event");
                    if (afterTicks < 1 || afterTicks > 6000) throw new ArgumentException("Invalid tick offset");
                    if (Current.Game == null || Find.CurrentMap == null || !Find.TickManager.Paused)
                        throw new InvalidOperationException("Requires a paused private game");
                    animal = null;
                    if (op == "hostile")
                    {
                        var colonists = Find.CurrentMap.mapPawns.FreeColonistsSpawned;
                        animal = Find.CurrentMap.mapPawns.AllPawnsSpawned.FirstOrDefault(p =>
                            p.RaceProps.Animal && p.Faction == null && !p.Downed && !p.Dead && !p.InMentalState
                            && colonists.Any(c => c.Position.DistanceTo(p.Position) <= 25));
                        if (animal == null) throw new InvalidOperationException("Fixture needs a conscious wild animal within 25 cells");
                    }
                    if (!patched)
                    {
                        new Harmony("rimgovernor.throughput-fixture").Patch(AccessTools.Method(typeof(TickManager), "DoSingleTick"),
                            postfix: new HarmonyMethod(typeof(ThroughputFixture), nameof(Tick)) { priority = Priority.First });
                        patched = true;
                    }
                    session = Current.Game; operation = op;
                    deadline = Find.TickManager.TicksGame + afterTicks; onset = null; onsetAtMs = null; applied = false;
                }
                return (object)new { success = true, eventKind = operation, deadline, onsetTick = onset, onsetAtMs, applied,
                    animal = animal?.GetUniqueLoadID(), tick = Find.TickManager.TicksGame };
            }, cancellationToken);
        }

        private static void Tick()
        {
            if (!ReferenceEquals(Current.Game, session) || onset.HasValue || Find.TickManager.TicksGame < deadline) return;
            onset = Find.TickManager.TicksGame;
            onsetAtMs = (DateTime.UtcNow.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond;
            if (operation == "hostile")
                applied = animal.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Manhunter);
            else
            {
                Find.TickManager.CurTimeSpeed = operation == "pause" ? TimeSpeed.Paused : TimeSpeed.Fast;
                applied = true;
            }
        }
    }
}
