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
            var census = new Obs.LootCensus { FreeHaulers = safety.FreeHaulers, StorytellerQuiet = StorytellerQuiet() };
            var headroom = new Dictionary<ThingDef, long>();
            foreach (var t in items) {
                var row = new Obs.LootItem {
                    Item = new Obs.EntityRef { Id = t.GetUniqueLoadID(), DefName = t.def.defName,
                        MapId = map.uniqueID, Position = new Common.Cell { X = t.Position.x, Z = t.Position.z } },
                    Forbidden = t.IsForbidden(Faction.OfPlayer), Count = t.stackCount
                };
                var safe = safety.Safe(t);
                if (safe.HasValue) row.SafeToHaul = safe.Value;
                if (safe == true && safety.PathLength >= 0) row.PathLength = safety.PathLength;
                if (!headroom.TryGetValue(t.def, out var units)) headroom[t.def] = units = StorageHeadroom(map, t);
                row.StorageHeadroom = units;
                census.Items.Add(row);
            }
            return new Obs.LootSection { Observed = census };
        }

        // Reach readiness (#520) reads the storyteller as quiet at zero threat
        // scale (peaceful) or with no incident generators at all.
        internal static bool StorytellerQuiet()
        {
            var storyteller = Find.Storyteller;
            return storyteller != null && (storyteller.difficulty.threatScale <= 0f || storyteller.storytellerComps.Count == 0);
        }

        // Free item units the player's storage accepting this def can still take:
        // empty accepting cells at the def's stack limit plus partial same-def
        // stacks. Bounded so a huge storage never drives the census cost.
        internal static long StorageHeadroom(Map map, Thing item)
        {
            long units = 0;
            foreach (var group in map.haulDestinationManager.AllGroupsListInPriorityOrder) {
                if (group.parent?.Accepts(item) != true) continue;
                foreach (var cell in group.CellsList) {
                    var occupant = cell.GetFirstItem(map);
                    if (occupant == null) units += item.def.stackLimit;
                    else if (occupant.def == item.def && occupant.CanStackWith(item)) units += Math.Max(0, item.def.stackLimit - occupant.stackCount);
                    if (units >= 1_000_000) return 1_000_000;
                }
            }
            return units;
        }

        internal static bool? Safe(Thing item) => new HaulingSafety(item.Map).Safe(item);

        internal sealed class HaulingSafety
        {
            private readonly Map map;
            private readonly List<Pawn> people;
            private readonly List<Thing> hazards;
            private readonly Dictionary<IntVec3, bool> exposed = new Dictionary<IntVec3, bool>();
            // PathLength is the last Safe(true) verdict's colonist route length in
            // cells, -1 when that verdict came without a measured path.
            internal double PathLength = -1;
            internal readonly long FreeHaulers;
            internal HaulingSafety(Map map)
            {
                this.map = map;
                people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && !p.InMentalState).ToList();
                FreeHaulers = people.Count(p => !p.Drafted && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)
                    && p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Hauling));
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

            private bool Route(Pawn pawn, IntVec3 from, LocalTargetInfo target, PathEndMode end, bool measure = false)
            {
                if (hazards.Count == 0 && !measure) return pawn.CanReach(target, end, Danger.None);
                using (var path = map.pathFinder.FindPathNow(from, target, TraverseParms.For(pawn, Danger.None), peMode: end)) {
                    if (!path.Found) return false;
                    if (measure) PathLength = Math.Min(PathLength < 0 ? path.NodesReversed.Count : PathLength, path.NodesReversed.Count);
                    return hazards.Count == 0 || path.NodesReversed.All(c => !Exposed(c) && c.GetDangerFor(pawn, map) == Danger.None);
                }
            }

            internal bool? Safe(Thing item)
            {
                PathLength = -1;
                if (Exposed(item.Position)) return false;
                var reachable = false;
                foreach (var pawn in people)
                {
                    var area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
                    if (area != null && !area[item.Position]) continue;
                    if (!pawn.CanReach(item, PathEndMode.Touch, Danger.None)) continue;
                    reachable = true;
                    if (!Route(pawn, pawn.Position, item, PathEndMode.Touch, measure: true)) return false;
                    // Check the return route to the native storage choice, too.
                    if (StoreUtility.TryFindBestBetterStoreCellFor(item, pawn, map, StoragePriority.Unstored,
                        Faction.OfPlayer, out var destination)
                        && !Route(pawn, item.Position, destination, PathEndMode.OnCell)) return false;
                }
                return reachable ? (bool?)true : null;
            }

            internal bool SalvageReturn(Building source, Thing output)
            {
                output.Position = source.Position;
                foreach (var pawn in people.Where(p => !p.Drafted && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))) {
                    if (!pawn.CanReach(source, PathEndMode.Touch, Danger.None)) continue;
                    if (!StoreUtility.TryFindBestBetterStoreCellFor(output, pawn, map, StoragePriority.Unstored, Faction.OfPlayer, out var destination)) continue;
                    foreach (var cell in GenAdj.CellsAdjacent8Way(source))
                        if (cell.InBounds(map) && cell.Standable(map) && !Exposed(cell) && !cell.IsForbidden(pawn)
                            && Route(pawn, pawn.Position, cell, PathEndMode.OnCell)
                            && Route(pawn, cell, destination, PathEndMode.OnCell)) return true;
                }
                return false;
            }
        }
    }
}
