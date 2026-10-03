using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private disposable acceptance only (#1748, epic #1694): a colony
    // survives a horror incident. test/horror_incident fires the game's own
    // incident worker for an Anomaly threat incident on the paused lab map:
    // the first anomaly threat incident, in def-name order, that the game
    // lets fire now and whose arrivals are exactly what the combat tactics
    // of #1739 treat as a melee entity pack (every arrival a hostile entity
    // or mutant whose attack is melee with no offensive ability, none hidden
    // from the player: the same reads NativeAnomalyFacts puts on the pawn
    // row). A candidate that spawns anything else is undone before the next
    // is tried. No incident or entity name is listed here. Everything after
    // the firing is the controller's and the game's.
    public sealed class HorrorIncidentFixture
    {
        [Tool("test/horror_incident", Description = "UNSAFE FOR MODEL EXECUTION. Private disposable fixture (#1748): fire one Anomaly threat incident whose arrivals are a melee entity pack through the game's own incident worker and reply the arrived pawns. Requires Anomaly and a paused lab map.")]
        public async Task<object> Fire(IRimBridgeContext ctx, CancellationToken cancellationToken)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || Current.Game == null || !Find.TickManager.Paused) return Refuse("A paused disposable colony map is required.");
                if (!ModsConfig.AnomalyActive) return Refuse("Anomaly is not active.");
                var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                if (colonists.Count == 0) return Refuse("The lab has no colonist to defend.");

                var tried = new List<string>();
                var candidates = DefDatabase<IncidentDef>.AllDefsListForReading
                    .Where(d => d.IsAnomalyIncident && (d.category == IncidentCategoryDefOf.ThreatBig || d.category == IncidentCategoryDefOf.ThreatSmall))
                    .OrderBy(d => d.defName, StringComparer.Ordinal).ToList();
                foreach (var def in candidates)
                {
                    var parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                    parms.forced = true;
                    parms.points = Math.Max(parms.points, def.minThreatPoints);
                    if (!def.Worker.CanFireNow(parms)) { tried.Add(def.defName + ": the game cannot fire it now"); continue; }

                    var before = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
                    var podsBefore = new HashSet<int>(map.listerThings.AllThings.OfType<DropPodIncoming>().Select(t => t.thingIDNumber));
                    var applied = def.Worker.TryExecute(parms);
                    var arrived = map.mapPawns.AllPawnsSpawned.Where(p => !before.Contains(p)).ToList();
                    var pods = map.listerThings.AllThings.OfType<DropPodIncoming>().Where(t => !podsBefore.Contains(t.thingIDNumber)).ToList();
                    var hostile = arrived.Where(p => p.HostileTo(Faction.OfPlayer)).ToList();
                    string why = null;
                    if (!applied) why = "the worker did not apply";
                    else if (pods.Count > 0) why = "the arrivals come in pods, which are not yet pawns to read";
                    else if (hostile.Count == 0) why = "no hostile pawn arrived";
                    else
                    {
                        var odd = hostile.FirstOrDefault(p => !MeleeEntity(p));
                        if (odd != null) why = odd.kindDef.defName + " is not a visible melee entity or mutant";
                    }
                    if (why == null)
                    {
                        var identity = Current.Game.GetComponent<ColonyIdentity>();
                        return new {
                            success = true, colonyId = identity?.ColonyId, loadToken = identity?.LoadToken, mapId = map.uniqueID,
                            tick = Find.TickManager.TicksGame, incident = def.defName, points = parms.points,
                            colonistIds = colonists.Select(p => p.GetUniqueLoadID()).ToArray(),
                            threatIds = hostile.Select(p => p.GetUniqueLoadID()).ToArray(),
                            threats = hostile.Select(p => new { id = p.GetUniqueLoadID(), kind = p.kindDef.defName, x = p.Position.x, z = p.Position.z }).ToArray(),
                            rejected = tried.ToArray(),
                        };
                    }
                    tried.Add(def.defName + ": " + why);
                    foreach (var pod in pods) pod.Destroy(DestroyMode.Vanish);
                    foreach (var pawn in arrived) pawn.Destroy(DestroyMode.Vanish);
                }
                return Refuse("No anomaly threat incident arrives as a melee entity pack: " + string.Join("; ", tried));
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool("test/horror_inspect", Description = "Read-only postcondition for the horror incident fixture: each colonist's and each threat's state on the live map.")]
        public async Task<object> Inspect(IRimBridgeContext ctx, CancellationToken cancellationToken, string colonistIds, string threatIds)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                if (Current.Game == null) return Refuse("A running game is required.");
                var all = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).Concat(Find.WorldPawns.AllPawnsAliveOrDead).Distinct().ToDictionary(p => p.GetUniqueLoadID(), p => p);
                object Row(string id)
                {
                    if (!all.TryGetValue(id, out var p)) return new { id, found = false };
                    return new { id, found = true, dead = p.Dead, destroyed = p.Destroyed, spawned = p.Spawned, downed = p.Downed };
                }
                var colonists = SplitIds(colonistIds).Select(Row).ToArray();
                var threats = SplitIds(threatIds).Select(Row).ToArray();
                return new { success = true, tick = Find.TickManager.TicksGame, colonists, threats };
            }, cancellationToken).ConfigureAwait(false);
        }

        // The pawn row's own reads: an entity or mutant whose attack is melee
        // with no offensive ability and that the player can see.
        private static bool MeleeEntity(Pawn pawn)
        {
            var a = NativeAnomalyFacts.Pawn(pawn);
            return a != null && ((a.HasEntity && a.Entity) || (a.HasMutant && a.Mutant)) && a.HasMeleeOnly && a.MeleeOnly && !(a.HasHiddenFromPlayer && a.HiddenFromPlayer);
        }

        private static IEnumerable<string> SplitIds(string ids) =>
            (ids ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0);

        private static object Refuse(string reason) => new { success = false, reason };
    }
}
