#nullable enable
using System.Collections.Generic;
using System.Linq;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// One snapshot stream frame (#858): every state family, plus the
    /// subscription's parameterized ones, each read exactly as its dedicated tool answers
    /// the request shape the controller issues, all in one game-thread hop
    /// so they describe one tick. A family that fails to read is omitted
    /// and the controller waits for a frame that carries it. Each section's
    /// read is timed into the observation account (#642) under its
    /// BundleSnapshot name.
    /// </summary>
    internal static class SnapshotFrames
    {
        private static long Now() { return System.Diagnostics.Stopwatch.GetTimestamp(); }

        // On the main thread: one snapshot stream frame (#858), every state
        // family plus the subscription's, or null when the map has no
        // readable context.
        internal static Obs.BundleSnapshot? Capture(Map map, Obs.SnapshotStreamRequest request)
        {
            if (!ProtoBoundary.TryReadContext(map, out var context, out _)) return null;
            var observed = new Obs.BundleSnapshot { Context = context, Paused = Find.TickManager.Paused };
            var status = new Obs.StatusRequest { Scope = new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() },
                Colonists = true, Threats = true, ColonistDetail = false };
            var statusBegan = Now();
            var statusRead = NativeObservationTools.TryStatus(map, status, context, out var emergency, out _);
            ObservationWork.Captured("emergency", Now() - statusBegan, 0);
            if (!statusRead) return null;
            observed.Emergency = emergency;
            ReadFamilies(map, context, observed);
            ReadStepFamilies(map, context, observed);
            ReadSubscribed(map, request, context, observed);
            var combatBegan = Now();
            Supervisor.EnsureHazardHooks(); Supervisor.EnsureCombatHooks();
            CombatMirror.Capture(map, observed);
            ObservationWork.Captured("combat", Now() - combatBegan, observed.CombatPawns.Count + observed.CombatEvents.Count);
            var inputsBegan = Now();
            CombatInputs(map, context, observed);
            ObservationWork.Captured("combatInputs", Now() - inputsBegan, observed.CombatDetail != null ? observed.CombatDetail.Pawns.Count : 0);
            return observed;
        }

        // On the main thread. The defense planner's combat inputs (#853):
        // the combat pawn detail for the census colonists, hostiles and
        // hunting predators, and the lines of fire from ranged colonists to
        // hostile buildings, while the census lists a threat or a colonist
        // in a mental state. Each is omitted when it fails to read.
        private static void CombatInputs(Map map, Common.ObservationContext context, Obs.BundleSnapshot observed)
        {
            var census = observed.Emergency;
            var threats = census?.Threats;
            if (census?.Colonists == null || threats == null) return;
            var colonists = census.Colonists.Pawns;
            if (threats.Hostiles.Count + threats.HuntingPredators.Count + threats.HostileBuildings.Count == 0
                && !colonists.Any(p => p.HasMentalState)) return;
            var ids = new List<string>();
            foreach (var row in colonists.Concat(threats.Hostiles.Select(t => t.Pawn)).Concat(threats.HuntingPredators.Select(t => t.Pawn)))
            {
                var id = row?.Pawn?.Id;
                if (id != null && !ids.Contains(id)) ids.Add(id);
            }
            if (ids.Count == 0 || ids.Count > 256) return;
            var pawns = new Obs.ListPawnsRequest {
                Scope = new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() },
                Filter = new Obs.PawnFilter { IncludeDead = true },
                Details = new Obs.PawnDetails { Needs = false, Health = true, Equipment = true, Biography = true, Settings = false, Social = false, Animals = true },
            };
            pawns.Filter.Ids.AddRange(ids);
            if (!NativePawnObservationTools.TryRead(map, pawns, context, out var detail)) return;
            observed.CombatDetail = detail;
            // The hottest live hive's temperature (#1073), for the heat-stroke hold.
            foreach (var hive in map.listerThings.ThingsOfDef(RimWorld.ThingDefOf.Hive))
            {
                if (!hive.Spawned || hive.Destroyed) continue;
                var t = hive.Position.GetTemperature(map);
                if (!observed.HasCombatHiveTemperatureC || t > observed.CombatHiveTemperatureC) observed.CombatHiveTemperatureC = t;
            }
            // The damaged player doors (#900), for a fight's door repair.
            foreach (var door in map.listerBuildings.AllBuildingsColonistOfClass<RimWorld.Building_Door>())
            {
                if (observed.CombatDoors.Count >= 64) break;
                if (!door.Spawned || !door.def.useHitPoints || door.HitPoints >= door.MaxHitPoints) continue;
                observed.CombatDoors.Add(new RimGovernor.Protocol.Mirror.CombatDoorRow { Id = door.GetUniqueLoadID(),
                    Cell = new Common.Cell { X = door.Position.x, Z = door.Position.z }, HitPoints = door.HitPoints, MaxHitPoints = door.MaxHitPoints });
            }
            // The unroofed player mortars (#931), for counter-battery.
            foreach (var mortar in map.listerBuildings.AllBuildingsColonistOfClass<RimWorld.Building_TurretGun>())
            {
                if (observed.CombatMortars.Count >= 16) break;
                if (!mortar.Spawned || mortar.def.building?.IsMortar != true || mortar.AttackVerb == null || map.roofGrid.Roofed(mortar.Position)) continue;
                observed.CombatMortars.Add(new RimGovernor.Protocol.Mirror.CombatMortarRow { Id = mortar.GetUniqueLoadID(),
                    Cell = new Common.Cell { X = mortar.Position.x, Z = mortar.Position.z },
                    MinRange = mortar.AttackVerb.verbProps.minRange, MaxRange = mortar.AttackVerb.verbProps.range });
                // The loaded shell (#1051), for the shell the fight asks for.
                var loaded = mortar.gun?.TryGetComp<RimWorld.CompChangeableProjectile>()?.LoadedShell;
                if (loaded != null) observed.CombatMortars[observed.CombatMortars.Count - 1].LoadedShell = loaded.defName;
            }
            var colonistIds = new HashSet<string>(colonists.Select(p => p.Pawn?.Id ?? ""));
            var firing = new List<IntVec3>();
            foreach (var row in detail.Pawns)
            {
                if (row.Pawn?.Position == null || !colonistIds.Contains(row.Pawn.Id) || row.Equipment == null || !row.Equipment.HasPrimaryId) continue;
                var primary = row.Equipment.Equipped.FirstOrDefault(g => g.Thing?.Id == row.Equipment.PrimaryId);
                if (primary == null || !primary.Ranged || !primary.HasRange || primary.Range <= 0) continue;
                var cell = new IntVec3(row.Pawn.Position.X, 0, row.Pawn.Position.Z);
                if (!firing.Contains(cell) && firing.Count < NativeDefenseObservationTools.MaximumLineCells) firing.Add(cell);
            }
            var approach = new List<IntVec3>();
            foreach (var building in threats.HostileBuildings)
                foreach (var c in building.OccupiedCells)
                {
                    var cell = new IntVec3(c.X, 0, c.Z);
                    if (!approach.Contains(cell) && approach.Count < NativeDefenseObservationTools.MaximumLineCells) approach.Add(cell);
                }
            if (firing.Count == 0 || approach.Count == 0 || firing.Concat(approach).Any(c => !c.InBounds(map))) return;
            try { observed.CombatLinesOfFire = NativeDefenseObservationTools.Lines(map, firing, approach, context); }
            catch (System.Exception ex) { Log.Error(ObservationWork.Failed("combatLinesOfFire", ex)); }
        }

        // On the main thread. Adds the census families to observed, each
        // keyed as its dedicated read, omitting any that fails.
        private static void ReadFamilies(Map map, Common.ObservationContext context, Obs.BundleSnapshot observed)
        {
            Obs.ReadScope Scope() => new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() };
            {
                var began = Now();
                var read = NativeColonyObservationTools.TryRead(map, new Obs.ColonyFactsRequest { Scope = Scope(), Planning = true }, context, out var colony);
                ObservationWork.Captured("colonyFacts", Now() - began, read ? colony!.Resources.Count : 0);
                if (read) { observed.ColonyFacts = colony; }
            }
            {
                var began = Now();
                var read = NativePopulationObservation.TryRead(map, new Obs.PopulationRequest { Scope = Scope() }, context, out var population);
                ObservationWork.Captured("population", Now() - began, read ? population!.Persons.Count : 0);
                if (read) { observed.Population = population; }
            }
            {
                var began = Now();
                var read = NativeResearchObservationTools.TryRead(map, new Obs.ResearchRequest { Scope = Scope(), IncludeLocked = true, IncludeFinished = true }, context, out var research);
                ObservationWork.Captured("research", Now() - began, read ? research!.Projects.Count : 0);
                if (read) { observed.Research = research; }
            }
            if (observed.Emergency?.Colonists != null
                && observed.Emergency.Colonists.Pawns.Count > 0)
            {
                var pawns = new Obs.ListPawnsRequest {
                    Scope = Scope(),
                    Filter = new Obs.PawnFilter { IncludeDead = true },
                    Details = new Obs.PawnDetails { Needs = true, Health = true, Equipment = true, Biography = true, Settings = true, Social = true, Animals = true, Work = true, Schedule = true },
                };
                foreach (var row in observed.Emergency.Colonists.Pawns) pawns.Filter.Ids.Add(row.Pawn.Id);
                var began = Now();
                var read = NativePawnObservationTools.TryRead(map, pawns, context, out var detail);
                // The colonists asked for are the candidates; the detail rows
                // returned are what the section produced.
                ObservationWork.Captured("colonistPawns", Now() - began, read ? detail!.Pawns.Count : 0, pawns.Filter.Ids.Count);
                if (read) { observed.ColonistPawns = detail; }
            }
        }

        // On the main thread. Adds the step families (#593), each the exact
        // read its dedicated tool answers for the request shape the
        // controller issues, omitting any that fails or is not observed.
        private static void ReadStepFamilies(Map map, Common.ObservationContext context, Obs.BundleSnapshot observed)
        {
            Obs.ReadScope Scope() => new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() };
            {
                var began = Now();
                var buildings = NativeBuildingObservationTools.Read(map, new Obs.ListBuildingsRequest { Scope = Scope(), PlayerOnly = true, Category = "artificial" }, context).Observed;
                ObservationWork.Captured("buildings", Now() - began, buildings != null ? buildings.Buildings.Count : 0);
                if (buildings != null) { observed.Buildings = buildings; }
            }
            {
                var began = Now();
                var bills = NativeBillsObservationTools.Read(map, new Obs.BillsRequest { Scope = Scope() }, context).Observed;
                ObservationWork.Captured("bills", Now() - began, bills != null ? bills.Benches.Count : 0);
                if (bills != null) { observed.Bills = bills; }
            }
            {
                var began = Now();
                var zones = NativeZoneObservationTools.Read(map, new Obs.ListZonesRequest { Scope = Scope() }, context).Observed;
                ObservationWork.Captured("zones", Now() - began, zones != null ? zones.Zones.Count : 0);
                if (zones != null) { observed.Zones = zones; }
            }
            {
                var began = Now();
                try { observed.Traders = NativeTradeObservation.Traders(map, context); } catch (System.Exception ex) { Log.Error(ObservationWork.Failed("traders", ex)); }
                ObservationWork.Captured("traders", Now() - began, observed.Traders != null ? observed.Traders.Traders.Count : 0);
            }
            {
                var began = Now();
                try { observed.WorldProgression = NativeWorldProgressionObservation.Build(context, false); } catch (System.Exception ex) { Log.Error(ObservationWork.Failed("worldProgression", ex)); }
                ObservationWork.Captured("worldProgression", Now() - began);
            }
            {
                var rooms = new Obs.ListRoomsRequest { Scope = Scope(), IncludeOutdoors = false, IncludeBoundary = false, IncludeCells = true };
                var began = Now();
                var census = NativeRoomObservationTools.Read(map, rooms, context).Observed;
                ObservationWork.Captured("rooms", Now() - began, census != null ? census.Rooms.Count : 0);
                if (census != null) { observed.Rooms = census; }
            }
        }

        // On the main thread. Adds the subscription's parameterized
        // families: each named resource's sources, the planning window
        // band and the named planning definitions.
        private static void ReadSubscribed(Map map, Obs.SnapshotStreamRequest request, Common.ObservationContext context, Obs.BundleSnapshot observed)
        {
            Obs.ReadScope Scope() => new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() };
            foreach (var resource in request.ResourceSources)
            {
                if (!ProtoBoundary.IsIdentifier(resource)) continue;
                var began = Now();
                var sources = NativeResourceSourcesTool.Read(map, new Obs.ResourceSourcesRequest { Scope = Scope(), Resource = resource, IncludeDevelopment = false }, context).Observed;
                ObservationWork.Captured("resourceSources", Now() - began, sources != null ? sources.Sources.Count : 0);
                if (sources != null) { observed.ResourceSources.Add(sources); }
            }
            var window = request.PlanningWindow;
            if (window?.Minimum != null && window.Maximum != null)
            {
                var width = (long)window.Maximum.X - window.Minimum.X + 1;
                var height = (long)window.Maximum.Z - window.Minimum.Z + 1;
                if (width >= 1 && height >= 1)
                {
                    var cells = new Obs.GetCellsRequest { Scope = Scope(), Rectangle = window.Clone(), Compact = true,
                        Fields = new Obs.CellFields { Terrain = false, Roof = true, Visibility = true, Traversal = true, Zone = true, Areas = false, Things = false, Designations = false, Room = true, Growth = true } };
                    if (NativeObservationTools.ValidateCells(cells, out _))
                    {
                        var began = Now();
                        var snapshot = NativeObservationTools.ReadCells(map, cells, context).Observed;
                        // The window's cells are the candidates; a
                        // changed_since_tick read returns only those that moved.
                        var rows = snapshot == null ? 0 : snapshot.Cells.Count + (snapshot.Compact != null ? snapshot.Compact.Rows.Count : 0);
                        ObservationWork.Captured("planningWindow", Now() - began, rows, width * height);
                        if (snapshot != null) { observed.PlanningWindow = snapshot; }
                    }
                }
            }
            if (request.Definitions.Count > 0)
            {
                var began = Now();
                try { observed.ProjectDefinitions.Add(NativeColonyObservationTools.Definitions(map, request.Definitions)); }
                catch (System.Exception ex) { observed.ProjectDefinitions.Clear(); Log.Error(ObservationWork.Failed("projectDefinitions", ex)); }
                ObservationWork.Captured("projectDefinitions", Now() - began, observed.ProjectDefinitions.Count);
            }
        }
    }
}
