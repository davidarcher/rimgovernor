using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    // Private disposable defense acceptance fixture. It stages the game's own raid and reads
    // independent native evidence of how it went. Movement and combat remain native.
    public sealed class DefenseFixture
    {
        [Tool("test/defense_setup", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable defense fixture: op=raid|inspect.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, string op, string strategy = "ImmediateAttack", string arrival = "EdgeWalkIn")
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap; var player = Faction.OfPlayerSilentFail;
                if (map == null || Current.Game == null || player == null || !Find.TickManager.Paused)
                    return Refuse("A paused disposable colony map is required.");
                switch (op)
                {
                    case "raid": return Raid(map, strategy, arrival);
                    case "inspect": return Inspect(map, player);
                    default: return Refuse("Use raid or inspect.");
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        // The storyteller only offers a strategy where its selection curve is
        // positive, and the strategy's pawn generation assumes that floor:
        // sappers below 700 points generate nothing and the incident refuses.
        private static float StrategyPointFloor(IncidentDef def, RaidStrategyDef strategy, Faction faction)
        {
            var floor = Math.Max(def.minThreatPoints, faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat));
            floor = Math.Max(floor, strategy.Worker.MinimumPoints(faction, PawnGroupKindDefOf.Combat));
            CurvePoint? previous = null;
            foreach (var point in strategy.selectionWeightPerPointsCurve)
            {
                if (point.y > 0f) return Math.Max(floor, previous.HasValue ? previous.Value.x + 1f : point.x);
                previous = point;
            }
            return floor;
        }

        // A strategy with a required pawn kind (sappers need canBeSapper) must
        // draw a combat group maker that carries one; the tribal faction's
        // ranged-only maker (commonality 60 of 225) has none, and the raid
        // then generates no pawns and refuses. The seed pins the
        // maker draw to one the strategy can use.
        private static int? RequiredKindGroupMakerSeed(IncidentParms parms)
        {
            if (!(parms.raidStrategy.Worker is RaidStrategyWorker_WithRequiredPawnKinds worker)) return null;
            var probe = IncidentParmsUtility.GetDefaultPawnGroupMakerParms(PawnGroupKindDefOf.Combat, parms);
            probe.points = IncidentWorker_Raid.AdjustedRaidPoints(parms.points, parms.raidArrivalMode, parms.raidStrategy, parms.faction, PawnGroupKindDefOf.Combat, parms.target);
            for (var seed = 0; seed < 64; seed++)
            {
                probe.seed = seed;
                if (PawnGroupMakerUtility.TryGetRandomPawnGroupMaker(probe, out var maker) && worker.CanUseWithGroupMaker(maker)) return seed;
            }
            return null;
        }

        // Raid stages the game's own raid incident, arriving wherever the
        // raid worker picks. The faction is the lowest-tech hostile humanlike
        // faction the chosen strategy and arrival accept natively (a siege
        // needs canSiege, a centre drop an industrial faction: pirates, not
        // the tribe), at the strategy's point floor. Pod pawns are not spawned
        // until the pods open, so a drop arrival reports the pawns inside the
        // incoming pods as added.
        private static object Raid(Map map, string strategy, string arrival)
        {
            var def = DefDatabase<IncidentDef>.GetNamed("RaidEnemy");
            var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            parms.raidStrategy = DefDatabase<RaidStrategyDef>.GetNamed(strategy);
            parms.raidArrivalMode = DefDatabase<PawnsArrivalModeDef>.GetNamed(arrival);
            var candidates = Find.FactionManager.AllFactionsVisible.Where(f => f.HostileTo(Faction.OfPlayer) && !f.def.hidden && f.def.humanlikeFaction
                    && ((IncidentWorker_RaidEnemy)def.Worker).FactionCanBeGroupSource(f, parms))
                .OrderBy(f => f.def.techLevel).ThenBy(f => f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)).ToList();
            var refused = new List<object>();
            Faction faction = null;
            foreach (var candidate in candidates)
            {
                parms.faction = candidate;
                parms.points = StrategyPointFloor(def, parms.raidStrategy, candidate);
                var strategyOk = parms.raidStrategy.Worker.CanUseWith(parms, PawnGroupKindDefOf.Combat);
                var arrivalOk = parms.raidArrivalMode.Worker.CanUseWith(parms);
                if (strategyOk && arrivalOk) { faction = candidate; break; }
                refused.Add(new { faction = candidate.def.defName, points = parms.points, strategyOk, arrivalOk });
            }
            if (faction == null) return new { success = false, reason = "No currently eligible hostile humanlike faction for " + strategy + "/" + arrival + ".", refused };
            parms.forced = true;
            parms.pawnGroupMakerSeed = RequiredKindGroupMakerSeed(parms);
            var before = new HashSet<string>(map.mapPawns.AllPawnsSpawned.Select(p => p.GetUniqueLoadID()));
            var podsBefore = new HashSet<int>(map.listerThings.AllThings.OfType<DropPodIncoming>().Select(t => t.thingIDNumber));
            var applied = def.Worker.TryExecute(parms);
            var arrived = map.mapPawns.AllPawnsSpawned.Where(p => !before.Contains(p.GetUniqueLoadID())).Select(p => new { pawn = p, inPod = false }).ToList();
            var pods = map.listerThings.AllThings.OfType<DropPodIncoming>().Where(t => !podsBefore.Contains(t.thingIDNumber)).ToList();
            foreach (var pod in pods)
                foreach (var pawn in pod.Contents.innerContainer.OfType<Pawn>()) arrived.Add(new { pawn, inPod = true });
            var added = arrived.Select(a => new { id = a.pawn.GetUniqueLoadID(), kind = a.pawn.kindDef.defName, hostile = a.pawn.HostileTo(Faction.OfPlayer), x = a.pawn.Position.x, z = a.pawn.Position.z,
                    lordJob = a.pawn.GetLord()?.LordJob?.GetType().Name, lordToil = a.pawn.GetLord()?.CurLordToil?.GetType().Name, a.inPod }).ToList();
            return new { success = applied && added.Count > 0, applied, faction = faction.def.defName, points = parms.points, strategy, arrival, groupMakerSeed = parms.pawnGroupMakerSeed,
                added, tick = Find.TickManager.TicksGame };
        }

        private static object Inspect(Map map, Faction player)
        {
            var hostiles = map.mapPawns.AllPawnsSpawned.Where(p => p.HostileTo(player) && p.RaceProps.Humanlike)
                .Select(p => new { id = p.GetUniqueLoadID(), dead = p.Dead, downed = p.Downed, x = p.Position.x, z = p.Position.z,
                    lordJob = p.GetLord()?.LordJob?.GetType().Name, lordToil = p.GetLord()?.CurLordToil?.GetType().Name }).ToList();
            var colonists = map.mapPawns.FreeColonistsSpawned
                .Select(p => new { id = p.GetUniqueLoadID(), x = p.Position.x, z = p.Position.z, drafted = p.Drafted, dead = p.Dead, downed = p.Downed }).ToList();
            return new { success = true, colonists, hostiles, tick = Find.TickManager.TicksGame };
        }

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
