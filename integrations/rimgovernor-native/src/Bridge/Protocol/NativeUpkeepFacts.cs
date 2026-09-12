using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using static HomeBridge.BridgeTools.NativePawnObservationTools;

namespace HomeBridge.BridgeTools
{
    // Complete independent censuses: a failed section contributes an issue and
    // no rows. The enclosing colony boundary still enforces its one-MiB limit.
    internal static class NativeUpkeepFacts
    {
        internal static void Populate(Map map, Obs.UpkeepFacts result)
        {
            var things = map.listerThings.AllThings.Where(t => t.Spawned && !t.Position.Fogged(map)).ToList();
            Read("items", result, () => {
                var rows = things.Where(t => t.def.category == ThingCategory.Item
                    && (t.Faction == null || t.Faction == Faction.OfPlayerSilentFail)).OrderBy(t => t.thingIDNumber).ToList();
                Require(rows.Count, 256);
                var values = rows.Select(t => {
                    var rot = t.TryGetComp<CompRottable>();
                    var value = new Obs.UpkeepItem {
                        Item = Ref(t), Count = t.stackCount, Roofed = t.Position.Roofed(map), InStorage = t.IsInValidStorage(),
                        DeteriorationRate = Number(t.GetStatValue(StatDefOf.DeteriorationRate)),
                        BaseDeteriorationRate = Number(t.def.GetStatValueAbstract(StatDefOf.DeteriorationRate, t.Stuff)),
                        Forbidden = t.IsForbidden(Faction.OfPlayerSilentFail), Medicine = t.def.IsMedicine,
                        Perishable = rot != null && rot.Active
                    };
                    if (rot != null && rot.Active) value.RotTicks = Math.Max(0, rot.TicksUntilRotAtCurrentTemp);
                    return value;
                }).ToList();
                result.Items.AddRange(values);
            });
            Read("structures", result, () => {
                // Non-damageable markers (including sleeping spots) have a
                // native -1 sentinel and cannot be repair targets.
                var rows = things.OfType<Building>().Where(b => b.Faction == Faction.OfPlayerSilentFail && b.def.useHitPoints).OrderBy(b => b.thingIDNumber).ToList();
                Require(rows.Count, 256);
                var values = rows.Select(b => new Obs.UpkeepStructure {
                    Building = new Obs.BuildingState { Building = Ref(b), HitPoints = b.HitPoints, MaxHitPoints = b.MaxHitPoints },
                    Home = b.OccupiedRect().All(c => map.areaManager.Home[c]),
                    RepairPriority = b.TryGetComp<CompTempControl>() != null || b.TryGetComp<CompPowerPlant>() != null
                        || b is Building_Bed bed && bed.Medical ? 0
                        : b.def.holdsRoof || b is Building_WorkTable || b is Building_Bed ? 1 : 2
                }).ToList();
                result.Structures.AddRange(values);
            });
            Read("fires", result, () => {
                var rows = things.OfType<Fire>().OrderBy(f => f.thingIDNumber).ToList();
                Require(rows.Count, 256);
                var values = rows.Select(f => new Obs.FireState { Fire = Ref(f), Home = map.areaManager.Home[f.Position], Size = Number(f.fireSize) }).ToList();
                result.Fires.AddRange(values);
            });
            Read("filth", result, () => {
                var rows = things.OfType<Filth>().OrderBy(f => f.thingIDNumber).ToList();
                Require(rows.Count, 256);
                var values = rows.Select(f => {
                    var value = new Obs.FilthState { Filth = Ref(f), Home = map.areaManager.Home[f.Position], Thickness = checked((uint)f.thickness) };
                    var room = f.GetRoom()?.Role?.defName;
                    if (room != null) value.RoomRole = Id(room);
                    return value;
                }).ToList();
                result.Filth.AddRange(values);
            });
            Read("animals", result, () => {
                var animals = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal
                    && p.Faction == Faction.OfPlayerSilentFail).OrderBy(p => p.thingIDNumber).ToList();
                Require(animals.Count, 256);
                var food = things.Where(t => t.def.category == ThingCategory.Item
                    && (t.Faction == null || t.Faction == Faction.OfPlayerSilentFail)
                    && t.def.IsNutritionGivingIngestible && !t.def.IsDrug && t.IngestibleNow).ToList();
                var values = animals.Select(p => {
                    var requiresPen = AnimalPenUtility.NeedsToBeManagedByRope(p);
                    var pen = requiresPen ? AnimalPenUtility.GetCurrentPenOf(p, false) : null;
                    var suitable = requiresPen ? AnimalPenUtility.ClosestSuitablePen(p, false) : null;
                    var state = new Obs.AnimalState {
                        Release = map.designationManager.DesignationOn(p, DesignationDefOf.ReleaseAnimalToWild) != null,
                        Slaughter = map.designationManager.DesignationOn(p, DesignationDefOf.Slaughter) != null
                    };
                    if (requiresPen) state.Contained = pen != null;
                    if (pen != null) state.PenId = Id(pen.parent.GetUniqueLoadID());
                    var value = new Obs.AnimalFeed {
                        Pawn = new Obs.PawnState { Pawn = Ref(p), AnimalState = state },
                        Diet = Id(p.RaceProps.foodType.ToString()), RequiresPen = requiresPen
                    };
                    if (suitable != null) value.SuitablePenId = Id(suitable.parent.GetUniqueLoadID());
                    var reachable = food.Where(t => p.WillEat(t) && !t.IsForbidden(p)
                        && p.CanReach(t, PathEndMode.Touch, Danger.None)
                        && (p.playerSettings?.AreaRestrictionInPawnCurrentMap == null
                            || p.playerSettings.AreaRestrictionInPawnCurrentMap[t.Position])).OrderBy(t => t.thingIDNumber).ToList();
                    Require(reachable.Count, 256);
                    foreach (var item in reachable) {
                        var rot = item.TryGetComp<CompRottable>();
                        var stock = new Obs.FoodStock { Item = Ref(item), Count = item.stackCount, HolderId = "",
                            Nutrition = Number(FoodUtility.NutritionForEater(p, item) * item.stackCount),
                            Perishable = rot != null && rot.Active, Roofed = item.Position.Roofed(map) };
                        stock.EaterIds.Add(Id(p.GetUniqueLoadID()));
                        if (rot != null && rot.Active) stock.RotTicks = Math.Max(0, rot.TicksUntilRotAtCurrentTemp);
                        value.ReachableStoredFeed.Add(stock);
                    }
                    return value;
                }).ToList();
                result.Animals.AddRange(values);
            });
        }

        private static Obs.EntityRef Ref(Thing thing) => new Obs.EntityRef {
            Id = Id(thing.GetUniqueLoadID()), DefName = Id(thing.def.defName), MapId = thing.Map.uniqueID, Position = Cell(thing.Position)
        };

        private static void Read(string field, Obs.UpkeepFacts result, Action read)
        {
            try { read(); }
            catch (ReadLimit) { result.Issues.Add(Issue(field, Common.UnavailableReason.LimitExceeded, "Complete upkeep census exceeds 256 rows.")); }
            catch (Exception) { result.Issues.Add(Issue(field, Common.UnavailableReason.ReadFailed, "Complete native upkeep section is unavailable.")); }
        }
    }
}
