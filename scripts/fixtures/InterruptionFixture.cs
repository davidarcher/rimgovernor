using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using System.Collections.Generic;
using Verse;

namespace RimBot.InterruptionFixtures
{
    // Separate test assembly; never part of production or the gameplay capability allowlist.
    public sealed class InterruptionFixture
    {
        [Tool("test/settle_caravan", Description = "Disposable multi-map acceptance through the enabled native settle command. Requires the private profile's ordinary multiple-settlement setting. Does not create maps or relocate pawns directly.")]
        public async Task<object> Settle(IRimBridgeContext ctx, CancellationToken cancellationToken,
            string caravanId = null, bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                if (Find.CurrentMap == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("Load and pause a disposable colony first");
                if (caravanId == null)
                {
                    var candidates = new HashSet<PlanetTile>();
                    var neighbors = new List<PlanetTile>();
                    Find.WorldGrid.GetTileNeighbors(Find.CurrentMap.Tile, neighbors);
                    foreach (var tile in neighbors)
                    {
                        var next = new List<PlanetTile>();
                        Find.WorldGrid.GetTileNeighbors(tile, next);
                        foreach (var candidate in next) if (TileFinder.IsValidTileForNewSettlement(candidate)) candidates.Add(candidate);
                    }
                    return (object)new { success = true, candidates = candidates.Select(t => t.tileId).ToArray(),
                        maximumSettlements = Prefs.MaxNumberOfPlayerSettlements };
                }
                var caravan = Find.WorldObjects.Caravans.Single(c => c.IsPlayerControlled && c.GetUniqueLoadID() == caravanId);
                var command = (Command_Action)SettleInEmptyTileUtility.SettleCommand(caravan);
                if (command.Disabled) return (object)new { success = true, accepted = false, reason = command.disabledReason };
                if (!dryRun)
                {
                    var before = Find.WindowStack.Windows.ToArray();
                    command.action();
                    var confirmation = Find.WindowStack.Windows.OfType<Dialog_MessageBox>().SingleOrDefault(w => !before.Contains(w));
                    if (confirmation != null)
                    {
                        confirmation.buttonAAction?.Invoke();
                        confirmation.Close();
                    }
                }
                return (object)new { success = true, accepted = true, dryRun, tile = caravan.Tile.tileId };
            }, cancellationToken);
        }

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

        [Tool("test/trade_quest_offer", Description = "Disposable ordinary TradeRequest or ThreatReward_Raid_Joiner offer at native storyteller points; never awards completion or edits quest states or expiration.")]
        public async Task<object> TradeQuest(IRimBridgeContext ctx, CancellationToken cancellationToken, bool dryRun = true,
            string definition = "TradeRequest")
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Load and pause a disposable colony first");
                if (definition != "TradeRequest" && definition != "ThreatReward_Raid_Joiner") throw new ArgumentException("Unsupported quest fixture");
                var def = DefDatabase<QuestScriptDef>.GetNamed(definition);
                var points = StorytellerUtility.DefaultThreatPointsNow(map);
                var slate = new Slate();
                slate.Set("points", points);
                // CanRun caches by tick; repeated paused probes must also check the live native root.
                var eligible = def.CanRun(points, map) && def.root.TestRun(slate);
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
