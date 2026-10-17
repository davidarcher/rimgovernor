using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Fixtures.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using RimWorld.QuestGen;
using Verse;

namespace HomeBridge.BridgeTools
{
    public sealed class QuestShuttleFixture
    {
        private static Quest Build(Map map, Pawn pawn, IntVec3 cell, bool named)
        {
            var thing = ThingMaker.MakeThing(ThingDefOf.Shuttle);
            GenSpawn.Spawn(thing, cell, map, Rot4.North);
            var shuttle = thing.TryGetComp<CompShuttle>();
            if (shuttle == null || shuttle.shipParent == null) throw new InvalidOperationException("Fixture shuttle transport ship unavailable.");
            shuttle.acceptColonists = !named;
            shuttle.acceptChildren = false;
            shuttle.onlyAcceptColonists = true;
            shuttle.onlyAcceptHealthy = true;
            if (named) shuttle.requiredPawns.Add(pawn);
            else { shuttle.requiredColonistCount = 1; shuttle.maxColonistCount = 1; }
            var quest = new Quest { id = Find.UniqueIDsManager.GetNextQuestID(), name = "Fixture shuttle", description = "Private shuttle contract fixture.", acceptanceTick = -1, acceptanceExpireTick = -1 };
            quest.AddPart<QuestPart_SetupTransportShip>().transportShip = shuttle.shipParent;
            Find.QuestManager.Add(quest);
            quest.Accept(null);
            var wait = (ShipJob_WaitTime)ShipJobMaker.MakeShipJob(ShipJobDefOf.WaitTime);
            wait.duration = 60000;
            wait.leaveImmediatelyWhenSatisfied = false;
            shuttle.shipParent.AddJob(wait);
            shuttle.shipParent.Start();
            return quest;
        }

        [Tool("test/quest_shuttle_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable lab fixture: spawn named and count-based quest shuttles for native loading and launch.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                var pawns = map?.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState).OrderBy(p => p.thingIDNumber).ToArray();
                if (map == null || !Find.TickManager.Paused || pawns == null || pawns.Length < 3) return new { success = false, reason = "Paused lab with three colonists required." };
                var named = Build(map, pawns[0], map.Center + new IntVec3(-12,0,0), true);
                var counted = Build(map, pawns[1], map.Center + new IntVec3(12,0,0), false);
                return FixtureKit.Reply(new QuestShuttlePrepared { Success = true, NamedQuest = named.GetUniqueLoadID(), CountedQuest = counted.GetUniqueLoadID(),
                    NamedPawn = pawns[0].GetUniqueLoadID(), CountedPawn = pawns[1].GetUniqueLoadID() });
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
