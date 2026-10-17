using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Fixtures.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (#3404). quest_accept_prepare builds one minimal not-yet-accepted quest (no
    // QuestGen node graph) carrying a two-option QuestPart_Choice reward with no rewards attached to either option, so
    // NativeQuestOperations.Resolve's reward-choice branch is exercised without concrete reward content. The quest
    // carries no QuestPart_RequirementsToAccept part, so Quest.RequiresAccepter is false and quest/accept exercises the
    // "this quest does not accept an accepter" refusal by supplying one anyway. The hack and give reads cover native
    // state no production read carries for a terminal and a beggar outside any quest.
    public sealed class QuestAcceptFixture
    {
        [Tool("test/quest_accept_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture: build one minimal not-yet-accepted quest with a two-option, reward-free QuestPart_Choice and no accepter requirement.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                var colonists = map.mapPawns.FreeColonistsSpawned
                    .Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState)
                    .OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count < 1) return Refuse("At least one existing colonist is required.");

                var quest = new Quest {
                    id = Find.UniqueIDsManager.GetNextQuestID(),
                    name = "Fixture quest offer", description = "Private disposable fixture quest.",
                    acceptanceTick = -1, acceptanceExpireTick = -1,
                };
                var choice = quest.AddPart<QuestPart_Choice>();
                choice.choices.Add(new QuestPart_Choice.Choice());
                choice.choices.Add(new QuestPart_Choice.Choice());
                Find.QuestManager.Add(quest);
                return FixtureKit.Reply(new QuestAcceptPrepared { Success = true, QuestId = quest.GetUniqueLoadID(),
                    PawnIds = colonists.Select(p => p.GetUniqueLoadID()).ToArray() });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_hack_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable lab: an unhacked Ideology terminal and ordinary Research worker; no autohack designation, hacking job or completion supplied.")]
        public async Task<object> PrepareHack(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !ModsConfig.IdeologyActive) return Refuse("Ideology lab map is required.");
                var hacker = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .FirstOrDefault(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Research) && !p.Dead && !p.Downed);
                if (hacker == null) return Refuse("The pinned lab needs a Research-capable colonist.");
                Idle(map);
                hacker.skills.GetSkill(SkillDefOf.Intellectual).Level = 12;
                hacker.workSettings.SetPriority(WorkTypeDefOf.Research, 1);
                var terminal = Place(map, DefDatabase<ThingDef>.GetNamed("AncientTerminal_Worshipful"), null, map.Center + new IntVec3(6, 0, 6));
                map.fogGrid.Unfog(terminal.Position);
                var item = ThingMaker.MakeThing(ThingDefOf.WoodLog); item.stackCount = 1;
                GenSpawn.Spawn(item, map.Center + new IntVec3(7, 0, 6), map);
                FixtureMeals(map);
                var hack = terminal.TryGetComp<CompHackable>();
                if (hack == null || hack.Autohack || hack.IsHacked || hack.ProgressPercent != 0) return Refuse("The terminal must start untouched.");
                return FixtureKit.Reply(new QuestHackPrepared { Success = true, TerminalId = terminal.GetUniqueLoadID(),
                    HackerId = hacker.GetUniqueLoadID(), UnhackableItemId = item.GetUniqueLoadID() });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_hack_read", Description = "UNSAFE FOR MODEL EXECUTION. Read exact terminal autohack, actual native hacking progress/completion and the vanilla comp's last hacker; never changes progress.")]
        public async Task<object> ReadHack(IRimBridgeContext ctx, CancellationToken cancellationToken, string terminalId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var terminal = RefIndex.Thing(map, terminalId);
                var hack = terminal?.TryGetComp<CompHackable>();
                if (hack == null) return Refuse("The exact terminal is unavailable.");
                var hacker = (Pawn)typeof(CompHackable).GetField("lastUser", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(hack);
                return FixtureKit.Reply(new QuestHackRead { Success = true, Autohack = hack.Autohack, Hacked = hack.IsHacked,
                    Progress = hack.ProgressPercent, HackerId = hacker?.GetUniqueLoadID() ?? "" });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_give_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable Ideology lab with a vanilla beggar lord requesting twenty silver; no delivery job or received items supplied.")]
        public async Task<object> PrepareGive(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !ModsConfig.IdeologyActive) return Refuse("Ideology lab map is required.");
                var hauler = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber)
                    .FirstOrDefault(p => !p.Dead && !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
                var faction = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !f.IsPlayer && !f.def.hidden && f.def.humanlikeFaction && !f.HostileTo(Faction.OfPlayer));
                if (hauler == null || faction == null) return Refuse("A mobile hauler and neutral visitor faction are required.");
                Idle(map);
                var recipient = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, faction);
                recipient.inventory.innerContainer.ClearAndDestroyContents();
                var spot = map.Center + new IntVec3(5, 0, 5);
                GenSpawn.Spawn(recipient, spot, map);
                Verse.AI.Group.LordMaker.MakeNewLord(faction, new LordJob_BegForItems(faction, spot, recipient, ThingDefOf.Silver, 20), map, new[] { recipient });
                var silver = ThingMaker.MakeThing(ThingDefOf.Silver); silver.stackCount = 20;
                GenSpawn.Spawn(silver, map.Center + new IntVec3(2, 0, 5), map);
                map.fogGrid.Unfog(spot); map.fogGrid.Unfog(silver.Position);
                FixtureMeals(map);
                return FixtureKit.Reply(new QuestGivePrepared { Success = true, HaulerId = hauler.GetUniqueLoadID(),
                    RecipientId = recipient.GetUniqueLoadID(), Remaining = GiveItemsToPawnUtility.ItemCountLeftToCollect(recipient) });
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/quest_give_read", Description = "UNSAFE FOR MODEL EXECUTION. Read actual recipient silver inventory and native whole-request remainder, plus the exact hauler's current job; no delivery mutations.")]
        public async Task<object> ReadGive(IRimBridgeContext ctx, CancellationToken cancellationToken, string recipientId, string haulerId)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var recipient = Find.Maps.SelectMany(m => m.mapPawns.AllPawns)
                    .Concat(Find.WorldPawns.AllPawnsAliveOrDead).FirstOrDefault(p => p.GetUniqueLoadID() == recipientId);
                var hauler = map?.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == haulerId);
                if (recipient == null || hauler == null) return Refuse("Exact lab pawns are unavailable.");
                return FixtureKit.Reply(new QuestGiveRead { Success = true,
                    Received = recipient.inventory.innerContainer.Where(t => t.def == ThingDefOf.Silver).Sum(t => t.stackCount),
                    Remaining = GiveItemsToPawnUtility.GetCountRemaining(recipient, ThingDefOf.Silver, 20),
                    Job = hauler.CurJob?.def.defName ?? "", TargetId = hauler.CurJob?.targetB.Pawn?.GetUniqueLoadID() ?? "" });
            }, cancellationToken).ConfigureAwait(false);
        }

        // Every colonist idle with no work enabled, so only the work the case grants runs.
        private static void Idle(Map map) {
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned) {
                pawn.workSettings.EnableAndInitialize();
                foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading) pawn.workSettings.SetPriority(work, 0);
                pawn.jobs.StopAll();
                for (var hour = 0; hour < 24; hour++) pawn.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
            }
        }

        private static void FixtureMeals(Map map) {
            for(var i=0;i<4;i++) { var food=ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack);food.stackCount=food.def.stackLimit;GenSpawn.Spawn(food,map.Center+new IntVec3(i,0,4),map); }
        }

        private static Thing Place(Map map, ThingDef def, ThingDef stuff, IntVec3 cell)
        {
            var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
            thing.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(thing, cell, map, Rot4.North);
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
