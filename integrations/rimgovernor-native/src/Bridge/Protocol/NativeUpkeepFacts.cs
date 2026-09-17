#nullable enable
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
                    Home = b.OccupiedRect().All(c => map.areaManager.Home[c]), Flammability = Number(b.GetStatValue(StatDefOf.Flammability)),
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
                // Home-area filth only: a map carries hundreds of natural
                // dirt and rubble rows outside it that no clean order may
                // ever target (upkeep orders require the home area), and a
                // whole-map census exceeded the bound on every real map.
                var rows = things.OfType<Filth>().Where(f => map.areaManager.Home[f.Position]).OrderBy(f => f.thingIDNumber).ToList();
                Require(rows.Count, 256);
                var values = rows.Select(f => {
                    var value = new Obs.FilthState { Filth = Ref(f), Home = map.areaManager.Home[f.Position], Thickness = checked((uint)f.thickness) };
                    var room = f.GetRoom();
                    if (room?.Role != null) value.RoomRole = Id(room.Role.defName);
                    // The same room identity the typed room census reports, so
                    // the controller can pair filth with a measured room
                    // cleanliness instead of a role name alone.
                    if (room != null) value.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return value;
                }).ToList();
                result.Filth.AddRange(values);
            });
            Read("home_coverage", result, () => {
                var state = HomeCoverage.State(map);
                var targets = HomeCoverage.Targets(map).OrderBy(id => id, StringComparer.Ordinal).ToList();
                Require(targets.Count, 256);
                var facts = new Obs.HomeCoverageFacts { Revision = state.Revision };
                foreach (var target in targets) {
                    var cells = HomeCoverage.Scope(map, target);
                    if (cells == null) {
                        facts.Targets.Add(new Obs.HomeCoverageTarget { Id = Id(target), Blocker = "Bounded visible native facility geometry unavailable" });
                        continue;
                    }
                    var missing = cells.Where(c => !map.areaManager.Home[c]).ToList();
                    if (missing.Count == 0) continue;
                    var row = new Obs.HomeCoverageTarget { Id = Id(target), ShapeToken = HomeCoverage.Shape(target, cells),
                        MissingCells = checked((uint)missing.Count) };
                    row.Cells.AddRange(cells.Select(Cell));
                    facts.Targets.Add(row);
                }
                facts.Completeness = Complete(facts.Targets.Count);
                result.HomeCoverage = new Obs.HomeCoverageSection { Observed = facts };
            });
            Read("lighting", result, () => {
                // Work cells are the interaction cells of colonist benches (work
                // tables and research benches): the cell a pawn stands on while
                // working, which is what RimWorld's darkness penalties measure.
                var benches = things.OfType<Building>().Where(b => b.Faction == Faction.OfPlayerSilentFail && b.def.hasInteractionCell
                    && (b is Building_WorkTable || b is Building_ResearchBench)).OrderBy(b => b.thingIDNumber).ToList();
                var lamps = things.OfType<Building>().Where(b => b.Faction == Faction.OfPlayerSilentFail && b.TryGetComp<CompGlower>() != null)
                    .OrderBy(b => b.thingIDNumber).ToList();
                Require(benches.Count, 256); Require(lamps.Count, 256);
                var facts = new Obs.LightingFacts();
                foreach (var b in benches) {
                    var cell = b.InteractionCell;
                    var row = new Obs.WorkLightCell { Bench = Ref(b), Cell = Cell(cell), Glow = Number(map.glowGrid.GroundGlowAt(cell)), Roofed = cell.Roofed(map) };
                    var room = cell.GetRoom(map);
                    if (room != null) row.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    facts.WorkCells.Add(row);
                }
                foreach (var b in lamps) {
                    var glower = b.TryGetComp<CompGlower>();
                    var service = new Obs.BuildingServiceState { SwitchedOn = b.TryGetComp<CompFlickable>()?.SwitchIsOn ?? true };
                    var power = b.TryGetComp<CompPowerTrader>();
                    if (power != null) { service.Connected = power.PowerNet != null; service.PowerOn = power.PowerOn; service.PowerOutputW = Number(power.PowerOutput); }
                    service.BrokenDown = b.TryGetComp<CompBreakdownable>()?.BrokenDown ?? false;
                    var fuel = b.TryGetComp<CompRefuelable>();
                    if (fuel != null) {
                        service.Fuel = Number(fuel.Fuel); service.TargetFuel = Number(fuel.TargetFuelLevel); service.OutOfFuel = !fuel.HasFuel;
                        var defs = fuel.Props.fuelFilter.AllowedThingDefs.Select(d => d.defName).OrderBy(d => d, StringComparer.Ordinal).ToList();
                        Require(defs.Count, 256); service.AllowedFuelDefs.Add(defs);
                    }
                    var row = new Obs.LampState { Building = new Obs.BuildingState { Building = Ref(b), Service = service },
                        GlowRadius = Number(glower.Props.glowRadius), Lit = glower.Glows };
                    var room = b.Position.GetRoom(map);
                    if (room != null) row.RoomId = room.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    facts.Lamps.Add(row);
                }
                facts.Completeness = Complete(benches.Count + lamps.Count);
                result.Lighting = new Obs.LightingSection { Observed = facts };
            });
            Read("people", result, () => {
                var people = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                Require(people.Count, 256);
                var values = people.Select(p => new Obs.UpkeepPerson {
                    Pawn = new Obs.PawnState { Pawn = Ref(p) }, OwnedBedId = p.ownership?.OwnedBed?.GetUniqueLoadID() ?? "",
                    ComfortableMinC = Number(p.GetStatValue(StatDefOf.ComfyTemperatureMin)),
                    ComfortableMaxC = Number(p.GetStatValue(StatDefOf.ComfyTemperatureMax)), TemperatureC = Number(p.AmbientTemperature)
                }).ToList();
                result.People.AddRange(values);
            });
            Read("beds", result, () => {
                var beds = things.OfType<Building_Bed>().Where(b => b.Faction == Faction.OfPlayerSilentFail).OrderBy(b => b.thingIDNumber).ToList();
                var people = map.mapPawns.AllPawnsSpawned.Where(p => p.IsFreeColonist && !p.Dead).OrderBy(p => p.thingIDNumber).ToList();
                Require(beds.Count, 256); Require(people.Count, 256);
                var values = beds.Select(b => {
                    var row = new Obs.UpkeepBed { Bed = Ref(b), Slots = checked((uint)b.SleepingSlotsCount),
                        Humanlike = b.def.building.bed_humanlike, RestEffectiveness = Number(b.GetStatValue(StatDefOf.BedRestEffectiveness)),
                        Medical = b.Medical, Prisoners = b.ForPrisoners, Roofed = b.OccupiedRect().All(c => c.Roofed(map)),
                        TemperatureC = Number(b.AmbientTemperature) };
                    var owners = b.OwnersForReading.Select(p => Id(p.GetUniqueLoadID())).OrderBy(id => id, StringComparer.Ordinal).ToList();
                    Require(owners.Count, 256); row.Owners.AddRange(owners);
                    row.Users.AddRange(people.Where(p => p.CurrentBed() == b).Select(p => Id(p.GetUniqueLoadID())));
                    row.AccessibleTo.AddRange(people.Where(p => !b.IsForbidden(p) && p.CanReach(b, PathEndMode.OnCell, Danger.None)).Select(p => Id(p.GetUniqueLoadID())));
                    return row;
                }).ToList();
                result.Beds.AddRange(values);
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
                        Slaughter = map.designationManager.DesignationOn(p, DesignationDefOf.Slaughter) != null,
                        SafeToRelease = NativeHusbandryOperations.Eligible(p) && NativeHusbandryOperations.SafeToRelease(p)
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
            // The factionless animals a MaintainHerd tame write can target:
            // native tame eligibility only, no feed or pen facts. A wild
            // census beyond the bound leaves the section unknown rather than
            // silently truncating the tame candidate list.
            Read("wild_animals", result, () => {
                var wild = map.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.RaceProps.Animal && p.Faction == null)
                    .OrderBy(p => p.thingIDNumber).ToList();
                Require(wild.Count, 256);
                result.WildAnimals.AddRange(wild.Select(p => new Obs.AnimalFeed {
                    Pawn = new Obs.PawnState { Pawn = Ref(p), Wild = true, AnimalState = new Obs.AnimalState {
                        Tameable = NativeHusbandryOperations.Tameable(p),
                        Tame = map.designationManager.DesignationOn(p, DesignationDefOf.Tame) != null,
                        MinimumHandlingSkill = TrainableUtility.MinimumHandlingSkill(p) } },
                    Diet = Id(p.RaceProps.foodType.ToString()), RequiresPen = false
                }));
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
