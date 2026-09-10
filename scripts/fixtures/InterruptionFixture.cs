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
        [Tool("test/world_incident", Description = "Disposable ordinary ColdSnap or AnimalInsanitySingle incident at native storyteller settings. No direct condition, temperature, pawn or health edits.")]
        public async Task<object> WorldIncident(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string definition, bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                if (definition != "ColdSnap" && definition != "AnimalInsanitySingle") throw new ArgumentException("Unsupported fixture incident");
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Load and pause a disposable colony first");
                var def = DefDatabase<IncidentDef>.GetNamed(definition);
                var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                var eligible = def.Worker.CanFireNow(parms);
                var applied = !dryRun && eligible && def.Worker.TryExecute(parms);
                return (object)new { success = true, definition, dryRun, eligible, applied,
                    tick = Find.TickManager.TicksGame, temperature = map.mapTemperature.OutdoorTemp };
            }, cancellationToken);
        }

        [Tool("test/trade_quest_offer", Description = "Disposable ordinary TradeRequest quest generation at native storyteller points; never awards completion or edits quest states.")]
        public async Task<object> TradeQuest(IRimBridgeContext ctx, CancellationToken cancellationToken, bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Load and pause a disposable colony first");
                var def = DefDatabase<QuestScriptDef>.GetNamed("TradeRequest");
                var points = StorytellerUtility.DefaultThreatPointsNow(map);
                var eligible = def.CanRun(points, map);
                var quest = !dryRun && eligible ? QuestUtility.GenerateQuestAndMakeAvailable(def, points) : null;
                return (object)new { success = true, dryRun, eligible, points,
                    questId = quest?.GetUniqueLoadID(), state = quest?.State.ToString() };
            }, cancellationToken);
        }

        [Tool("test/join_incident", Description = "Disposable scenario setup: require and execute the ordinary native WandererJoin incident; no direct pawn generation or edits.")]
        public async Task<object> Join(IRimBridgeContext ctx, CancellationToken cancellationToken, bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Load and pause a disposable game first.");
                var def = DefDatabase<IncidentDef>.GetNamed("WandererJoin");
                if (!(def.Worker is IncidentWorker_GiveQuest) || def.questScriptDef?.defName != "WandererJoins"
                    || !def.questScriptDef.autoAccept)
                    throw new InvalidOperationException("Expected the installed native auto-accepted WandererJoins quest incident");
                var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                var before = map.mapPawns.FreeColonistsSpawned.Select(p => p.GetUniqueLoadID()).ToArray();
                var existingLetters = Find.LetterStack.LettersListForReading.Select(l => l.GetUniqueLoadID()).ToArray();
                var eligible = def.Worker.CanFireNow(parms);
                var applied = !dryRun && eligible && def.Worker.TryExecute(parms);
                string acceptedLetter = null;
                if (applied)
                {
                    var letter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_AcceptJoiner>()
                        .Single(l => !existingLetters.Contains(l.GetUniqueLoadID()) && l.quest?.root == def.questScriptDef);
                    // The installed ChoiceLetter_AcceptJoiner exposes its native Accept
                    // option first. Invoke that enabled player choice, never pawn edits.
                    var accept = letter.Choices.First();
                    if (accept.disabled || accept.action == null)
                        throw new InvalidOperationException("Native join acceptance is unavailable");
                    acceptedLetter = letter.GetUniqueLoadID();
                    accept.action();
                }
                var after = map.mapPawns.FreeColonistsSpawned.Select(p => p.GetUniqueLoadID()).ToArray();
                return (object)new { success = true, dryRun, eligible, applied, definition = def.defName,
                    worker = def.Worker.GetType().FullName, quest = def.questScriptDef.defName, acceptedLetter,
                    questStates = Find.QuestManager.QuestsListForReading.Where(q => q.root == def.questScriptDef)
                        .Select(q => new { id = q.GetUniqueLoadID(), state = q.State.ToString(),
                            acceptedTick = q.acceptanceTick, hidden = q.hidden }).ToArray(),
                    before, after, joined = after.Except(before).ToArray(), tick = Find.TickManager.TicksGame };
            }, cancellationToken);
        }

        [Tool("test/interruption_letter", Description = "Disposable letter delivery through the real LetterStack callback; no pawn or simulation edits.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string definition = "ThreatBig", string after = "none", string label = "Interruption acceptance")
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                if (Current.Game == null || Find.CurrentMap == null) throw new InvalidOperationException("Load a disposable game first.");
                if (!new[] { "none", "pause", "speed", "modal", "raid" }.Contains(after)) throw new ArgumentException("Unknown after action");
                var def = DefDatabase<LetterDef>.GetNamed(definition);
                var letter = LetterMaker.MakeLetter(label, "Disposable native attribution case.", def);
                var before = Find.TickManager.CurTimeSpeed;
                Find.LetterStack.ReceiveLetter(letter, null, 0, false);
                var delivered = Find.TickManager.CurTimeSpeed;
                // Exercise ambiguous same-frame ordering via native player-equivalent operations.
                // Actual physical input is accepted separately by native_player_input_acceptance.py.
                if (after == "pause") Find.TickManager.Pause();
                if (after == "speed") Find.TickManager.CurTimeSpeed = TimeSpeed.Fast;
                if (after == "modal") Find.WindowStack.Add(new Dialog_MessageBox("Disposable scenario modal"));
                if (after == "raid")
                {
                    var incident = DefDatabase<IncidentDef>.GetNamed("RaidEnemy");
                    var parms = StorytellerUtility.DefaultParmsNow(incident.category, Find.CurrentMap);
                    parms.points = 100;
                    if (!incident.Worker.TryExecute(parms)) throw new InvalidOperationException("Native raid fixture unavailable");
                }
                return (object)new { success = true, letterId = letter.GetUniqueLoadID(),
                    definition, after, before = before.ToString(), delivered = delivered.ToString(),
                    speed = Find.TickManager.CurTimeSpeed.ToString(),
                    tick = Find.TickManager.TicksGame, automaticPauseMode = Prefs.AutomaticPauseMode.ToString() };
            }, cancellationToken);
        }
    }
}
