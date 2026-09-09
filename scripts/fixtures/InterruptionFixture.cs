using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace RimBot.InterruptionFixtures
{
    // Separate test assembly; never part of production or the gameplay capability allowlist.
    public sealed class InterruptionFixture
    {
        [Tool("test/join_incident", Description = "Disposable scenario setup: require and execute the ordinary native WandererJoin incident; no direct pawn generation or edits.")]
        public async Task<object> Join(IRimBridgeContext ctx, CancellationToken cancellationToken, bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Load and pause a disposable game first.");
                var def = DefDatabase<IncidentDef>.GetNamed("WandererJoin");
                if (!(def.Worker is IncidentWorker_WandererJoin)) throw new InvalidOperationException("Unexpected native join worker");
                var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                var before = map.mapPawns.FreeColonistsSpawned.Select(p => p.GetUniqueLoadID()).ToArray();
                var eligible = def.Worker.CanFireNow(parms);
                var applied = !dryRun && eligible && def.Worker.TryExecute(parms);
                var after = map.mapPawns.FreeColonistsSpawned.Select(p => p.GetUniqueLoadID()).ToArray();
                return (object)new { success = true, dryRun, eligible, applied, definition = def.defName,
                    before, after, joined = after.Except(before).ToArray(), tick = Find.TickManager.TicksGame };
            }, cancellationToken);
        }

        [Tool("test/interruption_letter", Description = "Disposable letter delivery through the real LetterStack callback; no pawn or simulation edits.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string definition = "ThreatBig", string after = "none")
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                if (Current.Game == null || Find.CurrentMap == null) throw new InvalidOperationException("Load a disposable game first.");
                if (after != "none" && after != "pause" && after != "speed") throw new ArgumentException("Unknown after action");
                var def = DefDatabase<LetterDef>.GetNamed(definition);
                var letter = LetterMaker.MakeLetter("Interruption acceptance", "Disposable native attribution case.", def);
                var before = Find.TickManager.CurTimeSpeed;
                Find.LetterStack.ReceiveLetter(letter, null, 0, false);
                var delivered = Find.TickManager.CurTimeSpeed;
                // Exercise ambiguous same-frame ordering via native player-equivalent operations.
                // Actual physical input is accepted separately by native_player_input_acceptance.py.
                if (after == "pause") Find.TickManager.Pause();
                if (after == "speed") Find.TickManager.CurTimeSpeed = TimeSpeed.Fast;
                return (object)new { success = true, letterId = letter.GetUniqueLoadID(),
                    definition, after, before = before.ToString(), delivered = delivered.ToString(),
                    speed = Find.TickManager.CurTimeSpeed.ToString(),
                    tick = Find.TickManager.TicksGame, automaticPauseMode = Prefs.AutomaticPauseMode.ToString() };
            }, cancellationToken);
        }
    }
}
