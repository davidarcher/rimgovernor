using System;
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
