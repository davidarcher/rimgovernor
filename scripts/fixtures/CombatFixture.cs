using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace RimBot.CombatFixtures
{
    // Disposable scenario entry only. Production/model capability policy excludes test/*.
    public sealed class CombatFixture
    {
        [Tool("test/combat_incident", Description = "Execute an eligible ordinary small tribal RaidEnemy incident in a paused disposable colony. No direct pawn, gear, health or faction edits.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused)
                    throw new InvalidOperationException("Load and pause a disposable colony first.");
                var def = DefDatabase<IncidentDef>.GetNamed("RaidEnemy");
                var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                var faction = Find.FactionManager.AllFactionsVisible.Where(f =>
                    f.def.techLevel == TechLevel.Neolithic && f.HostileTo(Faction.OfPlayer)
                    && ((IncidentWorker_RaidEnemy)def.Worker).FactionCanBeGroupSource(f, parms))
                    .OrderBy(f => f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)).FirstOrDefault();
                if (faction == null) throw new InvalidOperationException("No currently eligible native hostile tribal faction.");
                parms.faction = faction;
                parms.points = Math.Max(def.minThreatPoints,faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat));
                parms.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                parms.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
                var before = map.mapPawns.AllPawnsSpawned.Select(p => p.GetUniqueLoadID()).ToArray();
                var eligible = def.Worker.CanFireNow(parms);
                var applied = !dryRun && eligible && def.Worker.TryExecute(parms);
                var added = map.mapPawns.AllPawnsSpawned.Where(p => !before.Contains(p.GetUniqueLoadID()))
                    .Select(p => new { id = p.GetUniqueLoadID(), kind = p.kindDef.defName,
                        hostile = p.HostileTo(Faction.OfPlayer), x = p.Position.x, z = p.Position.z }).ToArray();
                return (object)new { success = true, dryRun, eligible, applied, incident = def.defName,
                    faction = faction.GetUniqueLoadID(), factionDef = faction.def.defName,
                    points = parms.points, added, tick = Find.TickManager.TicksGame,
                    earliestDay = def.earliestDay, daysPassed = GenDate.DaysPassedSinceSettle,
                    wandererGraceEnds = Find.GameEnder.newWanderersCreatedTick + 300000 };
            }, cancellationToken);
        }
    }
}
