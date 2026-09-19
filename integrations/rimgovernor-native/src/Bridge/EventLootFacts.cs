#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Native facts only. The controller selects and executes the desired forbid
    // state through the same authority and receipt path as starting supplies.
    internal static class EventLootFacts
    {
        internal static Obs.LootSection Read(Map map, List<Thing> things, Func<Thing, bool> reachable)
        {
            // Fresh animal corpses have a separate storage/forbid owner.
            var items = things.Where(t => NativeSupplyAllow.Eligible(t) && !FoodSupplyFacts.FreshAnimalCorpse(t)).OrderBy(t => t.thingIDNumber).Take(4097).ToList();
            if (items.Count > 4096)
                return new Obs.LootSection { Unavailable = new Common.Unavailable {
                    Reason = Common.UnavailableReason.LimitExceeded, Detail = "Hauling safety census exceeds 4096 items." } };
            var safety = new HaulingSafety(map);
            var census = new Obs.LootCensus();
            foreach (var t in items) {
                var row = new Obs.LootItem {
                    Item = new Obs.EntityRef { Id = t.GetUniqueLoadID(), DefName = t.def.defName,
                        MapId = map.uniqueID, Position = new Common.Cell { X = t.Position.x, Z = t.Position.z } },
                    Forbidden = t.IsForbidden(Faction.OfPlayer)
                };
                var safe = safety.Safe(t);
                if (safe.HasValue) row.SafeToHaul = safe.Value;
                census.Items.Add(row);
            }
            return new Obs.LootSection { Observed = census };
        }

        internal static bool? Safe(Thing item) => new HaulingSafety(item.Map).Safe(item);

        private sealed class HaulingSafety
        {
            private readonly Map map;
            private readonly List<Pawn> people;
            private readonly List<Thing> hazards;
            private readonly Dictionary<IntVec3, bool> exposed = new Dictionary<IntVec3, bool>();
            internal HaulingSafety(Map map)
            {
                this.map = map;
                people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.InMentalState).ToList();
                hazards = map.listerThings.AllThings.Where(t => t.Spawned && !t.Position.Fogged(map)
                    && (t is Fire || t is Building_Trap
                        || t is Pawn p && !p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer)
                        || t is Building b && b.HostileTo(Faction.OfPlayer) && b.def.building?.turretGunDef != null)).ToList();
            }

            private bool Exposed(IntVec3 cell)
            {
                if (exposed.TryGetValue(cell, out var result)) return result;
                result = cell.Fogged(map) || hazards.Any(h => {
                    if (h is Fire) return cell.DistanceToSquared(h.Position) <= 4;
                    if (h is Building_Trap) return cell == h.Position;
                    var range = h is Pawn p ? Math.Max(12f, p.CurrentEffectiveVerb?.verbProps.range ?? 0f)
                        : Math.Max(12f, h.def.building?.turretGunDef?.Verbs?.FirstOrDefault()?.range ?? 0f);
                    return cell.DistanceToSquared(h.Position) <= (range + 5) * (range + 5)
                        && GenSight.LineOfSight(cell, h.Position, map, true);
                });
                exposed[cell] = result;
                return result;
            }

            private bool Route(Pawn pawn, IntVec3 from, LocalTargetInfo target, PathEndMode end)
            {
                if (hazards.Count == 0) return pawn.CanReach(target, end, Danger.None);
                using (var path = map.pathFinder.FindPathNow(from, target, TraverseParms.For(pawn, Danger.None), peMode: end))
                    return path.Found && path.NodesReversed.All(c => !Exposed(c) && c.GetDangerFor(pawn, map) == Danger.None);
            }

            internal bool? Safe(Thing item)
            {
                if (Exposed(item.Position)) return false;
                var reachable = false;
                foreach (var pawn in people)
                {
                    var area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
                    if (area != null && !area[item.Position]) continue;
                    if (!pawn.CanReach(item, PathEndMode.Touch, Danger.None)) continue;
                    reachable = true;
                    if (!Route(pawn, pawn.Position, item, PathEndMode.Touch)) return false;
                    // Check the return route to the native storage choice, too.
                    if (StoreUtility.TryFindBestBetterStoreCellFor(item, pawn, map, StoragePriority.Unstored,
                        Faction.OfPlayer, out var destination)
                        && !Route(pawn, item.Position, destination, PathEndMode.OnCell)) return false;
                }
                return reachable ? (bool?)true : null;
            }
        }
    }
}
