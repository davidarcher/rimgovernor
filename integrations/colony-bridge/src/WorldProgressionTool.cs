using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Original local observation code. Receipts never stand in for world outcomes.
    public sealed class WorldProgressionTools
    {
        [Tool("home/world_progression", Title = "Observe caravans, assembly and quests",
            Description = "Read complete player caravan and visible quest censuses, native assembly jobs, cargo and pawn needs. Does not advance time or issue orders.")]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Include native stored-item census for return unloading verification", DefaultValue = false)] bool includeStorage = false)
        {
            return await ctx.MainThread.InvokeAsync(() => ReadNow(includeStorage), cancellationToken);
        }

        private static object PawnRow(Pawn pawn)
        {
            return new { thingId = pawn.GetUniqueLoadID(), label = pawn.LabelShort,
                dead = pawn.Dead, downed = pawn.Downed, mapId = pawn.MapHeld?.uniqueID,
                food = pawn.needs?.food?.CurLevelPercentage,
                rest = pawn.needs?.rest?.CurLevelPercentage,
                health = pawn.health?.summaryHealth?.SummaryHealthPercent,
                job = pawn.CurJob?.def?.defName,
                inventory = pawn.inventory?.innerContainer.Select(t => new {
                    thingId = t.ThingID, defName = t.def.defName, count = t.stackCount }).ToArray() };
        }

        internal static object ReadNow(bool includeStorage = false)
        {
            if (Current.Game == null)
                return new { success = false, reason = "No game loaded" };
            var identity = Current.Game.GetComponent<ColonyIdentity>();
            return new { success = true, ticksGame = Find.TickManager.TicksGame,
                colonyId = identity?.ColonyId, loadToken = identity?.LoadToken,
                mapId = Find.CurrentMap?.uniqueID,
                complete = true,
                maps = Find.Maps.Select(m => new { id = m.uniqueID, tile = m.Tile.tileId,
                    home = m.IsPlayerHome, label = m.Parent.Label,
                    pawns = m.mapPawns.FreeColonistsSpawned.Select(PawnRow).ToArray(),
                    storedItems = includeStorage ? m.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item && t.IsInValidStorage())
                        .Select(t => new { thingId = t.ThingID, defName = t.def.defName, count = t.stackCount }).ToArray() : null }).ToArray(),
                factions = Find.FactionManager.AllFactionsVisible.Select(f => new { id = f.GetUniqueLoadID(),
                    label = f.Name, player = f.IsPlayer, hostile = f.HostileTo(Faction.OfPlayer),
                    goodwill = f.IsPlayer ? (int?)null : f.PlayerGoodwill,
                    relation = f.IsPlayer ? "Player" : f.PlayerRelationKind.ToString() }).ToArray(),
                caravans = Find.WorldObjects.Caravans.Where(c => c.IsPlayerControlled).Select(c => new {
                    id = c.GetUniqueLoadID(), label = c.Label, tile = c.Tile.tileId,
                    destination = c.pather.Destination.tileId, moving = c.pather.Moving,
                    movingNow = c.pather.MovingNow, resting = c.NightResting,
                    massUsage = c.MassUsage, massCapacity = c.MassCapacity,
                    foodDays = c.DaysWorthOfFood.days, foodRotDays = c.DaysWorthOfFood.tillRot,
                    homeRoutes = Find.Maps.Where(m => m.IsPlayerHome).Select(m => new { mapId = m.uniqueID,
                        tile = m.Tile.tileId, reachable = c.CanReach(m.Tile),
                        estimatedTicks = c.CanReach(m.Tile) ? (int?)CaravanArrivalTimeEstimator.EstimatedTicksToArrive(c.Tile, m.Tile, c) : null }).ToArray(),
                    pawns = c.PawnsListForReading.Select(PawnRow).ToArray() }).ToArray(),
                assemblies = Find.Maps.SelectMany(m => m.lordManager.lords
                    .Where(l => l.LordJob is LordJob_FormAndSendCaravan)
                    .Select(l => new { id = l.GetUniqueLoadID(), mapId = m.uniqueID,
                        status = ((LordJob_FormAndSendCaravan)l.LordJob).Status,
                        gatheringItems = ((LordJob_FormAndSendCaravan)l.LordJob).GatheringItemsNow,
                        pawns = l.ownedPawns.Select(PawnRow).ToArray() })).ToArray(),
                quests = Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && !q.hiddenInUI)
                    .Select(q => new { id = q.GetUniqueLoadID(), label = q.name,
                        description = q.description.ToString(), state = q.State.ToString(),
                        acceptedTick = q.acceptanceTick, expiresInTicks = q.TicksUntilExpiry,
                        requiresAccepter = q.RequiresAccepter,
                        tradeRequests = q.PartsListForReading.OfType<QuestPart_InitiateTradeRequest>().Select(p => new {
                            settlementId = p.settlement?.GetUniqueLoadID(), tile = p.settlement?.Tile.tileId,
                            factionId = p.settlement?.Faction?.GetUniqueLoadID(), resource = p.requestedThingDef?.defName,
                            count = p.requestedCount, durationTicks = p.requestDuration }).ToArray(),
                        rewardChoices = q.PartsListForReading.OfType<QuestPart_Choice>().SelectMany(part =>
                            part.choices.Select((choice, index) => new { index,
                                rewards = choice.rewards.Select(r => new { kind = r.GetType().Name,
                                    description = BridgeCommon.SafeString(() => r.GetDescription(default(RewardsGeneratorParams))),
                                    items = (r as Reward_Items)?.items?.Select(t => new {
                                        label = t.Label, defName = t.def.defName, count = t.stackCount }).ToArray()
                                }).ToArray() })).ToArray(),
                        canAccept = q.State == QuestState.NotYetAccepted && QuestUtility.CanAcceptQuest(q).Accepted,
                        eligiblePawns = Find.Maps.SelectMany(m => m.mapPawns.FreeColonistsSpawned)
                            .Where(p => QuestUtility.CanPawnAcceptQuest(p, q))
                            .Select(p => p.GetUniqueLoadID()).ToArray() }).ToArray() };
        }
    }
}
