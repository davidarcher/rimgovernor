using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Disposable test setup only (issue #92). The native surface reads the
    // letter stack but never removes from it; the acceptance advance loop
    // acknowledges informational letters and takes them off the stack here so
    // a run's letters are the ones its own fixtures delivered. Removing a
    // ChoiceLetter without choosing is the game's own timeout outcome (the
    // joiner leaves, the quest stays offered until it expires).
    public sealed class LetterFixture
    {
        [Tool("test/dismiss_letter", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test cleanup: remove one letter from the letter stack by id without choosing anything. Reports the letter's def and type; a missing id is reported, not an error.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Letter id as home/status letters[].id reports it.")] string letterId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || Find.LetterStack == null) throw new InvalidOperationException("A loaded game is required.");
                if (string.IsNullOrEmpty(letterId)) throw new ArgumentException("letterId is required.");
                var letter = Find.LetterStack.LettersListForReading.FirstOrDefault(l => l.GetUniqueLoadID() == letterId);
                if (letter == null)
                    return new { success = true, removed = false, letterId, remaining = Find.LetterStack.LettersListForReading.Count };
                Find.LetterStack.RemoveLetter(letter);
                return new { success = true, removed = true, letterId, label = letter.Label.RawText, letterDef = letter.def?.defName,
                    type = letter.GetType().FullName, remaining = Find.LetterStack.LettersListForReading.Count };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/deliver_letter", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: deliver a plain letter of the given LetterDef through the real LetterStack callback, now or delayTicks (1..600) game ticks from now, so an advance window meets a letter of a known def.")]
        public async Task<object> Deliver(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "LetterDef name, e.g. PositiveEvent or ThreatBig.")] string definition = "PositiveEvent",
            [ToolParameter(Description = "Letter label.")] string label = "Letter acceptance",
            [ToolParameter(Description = "0 delivers now; 1..600 schedules that many ticks ahead.")] int delayTicks = 0)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null || Find.CurrentMap == null) throw new InvalidOperationException("A loaded game is required.");
                if (delayTicks < 0 || delayTicks > 600) throw new ArgumentException("delayTicks must be within 0..600.");
                var def = DefDatabase<LetterDef>.GetNamed(definition);
                var letter = LetterMaker.MakeLetter(label, "Disposable letter acceptance case.", def);
                var id = letter.GetUniqueLoadID();
                if (delayTicks == 0)
                {
                    Find.LetterStack.ReceiveLetter(letter, null, 0, false);
                    return new { success = true, letterId = id, definition, delayTicks, pauseMode = def.pauseMode.ToString(),
                        automaticPauseMode = Prefs.AutomaticPauseMode.ToString(), tick = Find.TickManager.TicksGame,
                        speed = Find.TickManager.CurTimeSpeed.ToString() };
                }
                var scheduler = Current.Game.GetComponent<ScheduledLetter>();
                if (scheduler == null) { scheduler = new ScheduledLetter(Current.Game); Current.Game.components.Add(scheduler); }
                var due = checked(Find.TickManager.TicksGame + delayTicks);
                scheduler.Schedule(letter, due);
                return new { success = true, letterId = id, definition, delayTicks, dueTick = due, pauseMode = def.pauseMode.ToString(),
                    automaticPauseMode = Prefs.AutomaticPauseMode.ToString(), tick = Find.TickManager.TicksGame };
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/letter_pause_mode", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: read or set Prefs.AutomaticPauseMode for this process (Never, MajorThreat, AnyThreat, AnyLetter), which decides which letters pause the clock. Not saved to Prefs.xml.")]
        public async Task<object> PauseMode(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Mode to set; empty only reads.")] string mode = "")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var before = Prefs.AutomaticPauseMode.ToString();
                if (!string.IsNullOrEmpty(mode))
                {
                    AutomaticPauseMode parsed;
                    if (!Enum.TryParse(mode, out parsed)) throw new ArgumentException("Unknown AutomaticPauseMode.");
                    Prefs.AutomaticPauseMode = parsed;
                }
                return new { success = true, before, mode = Prefs.AutomaticPauseMode.ToString() };
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    // Delivers scheduled letters on the game tick they fall due, through the
    // same LetterStack callback the game uses.
    public sealed class ScheduledLetter : GameComponent
    {
        private readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, Letter>> pending =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, Letter>>();
        public ScheduledLetter(Game game) { }
        public void Schedule(Letter letter, int tick) { pending.Add(new System.Collections.Generic.KeyValuePair<int, Letter>(tick, letter)); }
        public override void GameComponentTick()
        {
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                if (Find.TickManager.TicksGame < pending[i].Key) continue;
                var letter = pending[i].Value; pending.RemoveAt(i);
                Find.LetterStack.ReceiveLetter(letter, null, 0, false);
            }
        }
    }
}
