#nullable enable
using System.Collections.Generic;
using System.Linq;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// One snapshot stream frame: every state family, plus the
    /// subscription's parameterized ones, each read exactly as its dedicated tool answers
    /// the request shape the controller issues, all in one game-thread hop
    /// so they describe one tick. A family that fails to read is omitted
    /// and the controller waits for a frame that carries it. Each section's
    /// read is timed into the observation account under its
    /// BundleSnapshot name.
    /// </summary>
    internal static class SnapshotFrames
    {
        /// A frame section's read threw. The stream publishes a frame
        /// carrying only the Failure, never the frame with the section omitted.
        internal sealed class SnapshotSectionException : System.Exception
        {
            internal readonly string Section;
            private SnapshotSectionException(string section, System.Exception inner) : base(section + ": " + inner.GetType().Name + ": " + inner.Message, inner) { Section = section; }

            internal static T Read<T>(string section, System.Func<T> read)
            {
                try { return read(); }
                catch (System.Exception ex)
                {
                    ObservationWork.Failed(section, ex);
                    throw new SnapshotSectionException(section, ex);
                }
            }

            internal Obs.BundleSnapshot Frame() => new Obs.BundleSnapshot
            {
                Failure = new Common.Failure { Code = Common.FailureCode.NativeFailure, Detail = "snapshot section " + Message },
            };
        }

        private static long Now() { return System.Diagnostics.Stopwatch.GetTimestamp(); }

        // On the main thread: one snapshot stream frame, every state
        // family plus the subscription's, or null when the map has no
        // readable context.
        internal static Obs.BundleSnapshot? Capture(Map map, Obs.SnapshotStreamRequest request) => Capture(map, request, out _);

        // Capture, with the whole-map cell grid read that
        // CellGridEncoder.Attach encodes off the game thread; null when it
        // failed to read.
        internal static Obs.BundleSnapshot? Capture(Map map, Obs.SnapshotStreamRequest request, out CellGridEncoder.GridRead? grid)
        {
            grid = null;
            if (!ProtoBoundary.TryReadContext(map, out var context, out _)) return null;
            var observed = new Obs.BundleSnapshot { Context = context, Paused = Find.TickManager.Paused };
            var read = false;
            var referenced = NativeRef.Collect(() => read = ReadSections(map, request, context, observed));
            if (!read) return null;
            var thingsBegan = Now();
            try { observed.Things = NativeObservationTools.Things(referenced, context); } catch (System.Exception ex) { ObservationWork.Failed("things", ex); }
            ObservationWork.Captured("things", Now() - thingsBegan, observed.Things != null ? observed.Things.Things.Count : 0);
            var gridBegan = Now();
            try { grid = CellGridEncoder.Read(map); } catch (System.Exception ex) { ObservationWork.Failed("grid", ex); }
            ObservationWork.Captured("grid", Now() - gridBegan, grid != null ? grid.Count : 0);
            return observed;
        }

        // On the main thread: every section of the frame but its things
        // table and grid, false when the status census fails. The things
        // the sections reference become the things table.
        private static bool ReadSections(Map map, Obs.SnapshotStreamRequest request, Common.ObservationContext context, Obs.BundleSnapshot observed)
        {
            var status = new Obs.StatusRequest { Scope = new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() },
                Colonists = true, Threats = true };
            var statusBegan = Now();
            var statusRead = NativeObservationTools.TryStatus(map, status, context, out var emergency, out _);
            ObservationWork.Captured("emergency", Now() - statusBegan, 0);
            if (!statusRead) return false;
            observed.Emergency = emergency;
            ReadFamilies(map, context, observed, request);
            ReadStepFamilies(map, context, observed);
            ReadSubscribed(map, request, context, observed);
            var combatBegan = Now();
            Supervisor.EnsureHazardHooks(); Supervisor.EnsureCombatHooks();
            CombatMirror.Capture(map, observed);
            ObservationWork.Captured("combat", Now() - combatBegan, observed.CombatPawns.Count + observed.CombatEvents.Count);
            var inputsBegan = Now();
            CombatInputs(map, context, observed, request);
            ObservationWork.Captured("combatInputs", Now() - inputsBegan, observed.CombatDoors.Count + observed.CombatMortars.Count);
            return true;
        }

        // The census pawns the pawn table carries combat detail for:
        // every engaged threat row, a superset of what Go classifies as a
        // hostile or hunting predator.
        private static ISet<string> CombatSet(Obs.StatusSnapshot? census)
        {
            var ids = new HashSet<string>(System.StringComparer.Ordinal);
            var threats = census?.Threats;
            if (threats == null) return ids;
            foreach (var threat in threats.Pawns.Where(Engaged))
                if (threat?.Pawn?.Id != null) ids.Add(threat.Pawn.Id);
            return ids;
        }
        private static bool Engaged(Obs.ThreatPawn row) => row.HasMentalState || row.FactionHostile || row.PrisonBreak || row.PredatorHunt;

        // On the main thread. The defense planner's other combat inputs
        //, the pawn detail being the pawn table's: the hive
        // temperature, damaged doors, mortars and the lines of fire from
        // ranged colonists to hostile buildings, while the census lists a
        // threat or a colonist in a mental state. Each is omitted when it
        // fails to read.
        private static void CombatInputs(Map map, Common.ObservationContext context, Obs.BundleSnapshot observed, Obs.SnapshotStreamRequest request)
        {
            var census = observed.Emergency;
            var threats = census?.Threats;
            var table = observed.Pawns;
            if (census == null || threats == null || table == null) return;
            var colonistIds = new HashSet<string>(census.Colonists.Select(p => p.Id ?? ""), System.StringComparer.Ordinal);
            var colonists = table.Pawns.Where(p => colonistIds.Contains(p.Pawn?.Id ?? "")).ToList();
            if (!threats.Pawns.Any(Engaged) && threats.HostileBuildings.Count == 0
                && !colonists.Any(p => p.HasMentalState)) return;
            // The hottest live hive's temperature, for the heat-stroke hold.
            foreach (var hive in map.listerThings.ThingsOfDef(RimWorld.ThingDefOf.Hive))
            {
                if (!hive.Spawned || hive.Destroyed) continue;
                var t = hive.Position.GetTemperature(map);
                if (!observed.HasCombatHiveTemperatureC || t > observed.CombatHiveTemperatureC) observed.CombatHiveTemperatureC = t;
            }
            // The damaged player doors, for a fight's door repair.
            foreach (var door in map.listerBuildings.AllBuildingsColonistOfClass<RimWorld.Building_Door>())
            {
                if (!door.Spawned || !door.def.useHitPoints || door.HitPoints >= door.MaxHitPoints) continue;
                observed.CombatDoors.Add(new RimGovernor.Protocol.Mirror.CombatDoorRow { Id = door.GetUniqueLoadID(),
                    Cell = new Common.Cell { X = door.Position.x, Z = door.Position.z }, HitPoints = door.HitPoints, MaxHitPoints = door.MaxHitPoints });
            }
            // The unroofed player mortars, for counter-battery.
            foreach (var mortar in map.listerBuildings.AllBuildingsColonistOfClass<RimWorld.Building_TurretGun>())
            {
                if (!mortar.Spawned || mortar.def.building?.IsMortar != true || mortar.AttackVerb == null || map.roofGrid.Roofed(mortar.Position)) continue;
                observed.CombatMortars.Add(new RimGovernor.Protocol.Mirror.CombatMortarRow { Id = mortar.GetUniqueLoadID(),
                    Cell = new Common.Cell { X = mortar.Position.x, Z = mortar.Position.z },
                    MinRange = mortar.AttackVerb.verbProps.minRange, MaxRange = mortar.AttackVerb.verbProps.range });
                // The loaded shell, for the shell the fight asks for.
                var loaded = mortar.gun?.TryGetComp<RimWorld.CompChangeableProjectile>()?.LoadedShell;
                if (loaded != null) observed.CombatMortars[observed.CombatMortars.Count - 1].LoadedShell = loaded.defName;
            }
            var firing = new List<IntVec3>();
            foreach (var row in colonists)
            {
                if (row.Pawn?.Position == null || row.Equipment == null || !row.Equipment.HasPrimaryId) continue;
                // The shooter's primary weapon is the live one: the wire rows no
                // longer carry its class or range, the catalog does.
                var shooter = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == row.Pawn.Id);
                var weapon = shooter?.equipment?.Primary;
                if (weapon == null || weapon.GetUniqueLoadID() != row.Equipment.PrimaryId || !weapon.def.IsRangedWeapon) continue;
                var gun = weapon.def.Verbs?.FirstOrDefault(v => !v.IsMeleeAttack && v.range > 0);
                if (gun == null || float.IsNaN(gun.range) || float.IsInfinity(gun.range)) continue;
                var cell = new IntVec3(row.Pawn.Position.X, 0, row.Pawn.Position.Z);
                if (!firing.Contains(cell)) firing.Add(cell);
            }
            var approach = new List<IntVec3>();
            foreach (var building in threats.HostileBuildings)
                for (var z = building.Occupied.Minimum.Z; z <= building.Occupied.Maximum.Z; z++)
                    for (var x = building.Occupied.Minimum.X; x <= building.Occupied.Maximum.X; x++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        if (!approach.Contains(cell)) approach.Add(cell);
                    }
            if (firing.Count == 0 || approach.Count == 0 || firing.Concat(approach).Any(c => !c.InBounds(map))) return;
            try { observed.CombatLinesOfFire = NativeDefenseObservationTools.Lines(map, firing, approach, context, request.HasLinesOfFireBudgetMs ? request.LinesOfFireBudgetMs : (uint?)null); }
            catch (System.Exception ex) { ObservationWork.Failed("combatLinesOfFire", ex); }
        }

        // The frame's colony facts request: planning, with the stream's herd
        // radius and hunt route budget carried through when Go supplied them.
        private static Obs.ColonyFactsRequest ColonyRequest(Obs.ReadScope scope, Obs.SnapshotStreamRequest request)
        {
            var colony = new Obs.ColonyFactsRequest { Scope = scope, Planning = true };
            if (request.HasHerdRadius) colony.HerdRadius = request.HerdRadius;
            if (request.HasHuntRouteBudgetMs) colony.HuntRouteBudgetMs = request.HuntRouteBudgetMs;
            return colony;
        }

        // On the main thread. Adds the census families to observed, each
        // keyed as its dedicated read. A section that throws fails the frame
        // (SnapshotSectionException); none is omitted.
        private static void ReadFamilies(Map map, Common.ObservationContext context, Obs.BundleSnapshot observed, Obs.SnapshotStreamRequest request)
        {
            Obs.ReadScope Scope() => new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() };
            {
                var began = Now();
                observed.ColonyFacts = SnapshotSectionException.Read("colonyFacts", () => NativeColonyObservationTools.Read(map, ColonyRequest(Scope(), request), context, request.HasRoutePathBudgetMs ? request.RoutePathBudgetMs : (uint?)null));
                ObservationWork.Captured("colonyFacts", Now() - began, observed.ColonyFacts.Resources.Count);
            }
            {
                var began = Now();
                observed.Population = SnapshotSectionException.Read("population", () => NativePopulationObservation.Population(map, new Obs.PopulationRequest { Scope = Scope() }, context));
                ObservationWork.Captured("population", Now() - began, observed.Population.Persons.Count);
            }
            {
                var began = Now();
                observed.Research = SnapshotSectionException.Read("research", () => NativeResearchObservationTools.Section(map, new Obs.ResearchRequest { Scope = Scope(), ProgressOnly = true }, context));
                ObservationWork.Captured("research", Now() - began, observed.Research.Projects.Count);
            }
            {
                var began = Now();
                observed.Pawns = SnapshotSectionException.Read("pawns", () => NativePawnObservationTools.Table(map, context, CombatSet(observed.Emergency)));
                ObservationWork.Captured("pawns", Now() - began, observed.Pawns != null ? observed.Pawns.Pawns.Count : 0);
            }
        }

        // On the main thread. Adds the step families, each the exact
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
                try { observed.Traders = NativeTradeObservation.Traders(map, context); } catch (System.Exception ex) { ObservationWork.Failed("traders", ex); }
                ObservationWork.Captured("traders", Now() - began, observed.Traders != null ? observed.Traders.Traders.Count : 0);
            }
            {
                var began = Now();
                try { observed.WorldProgression = NativeWorldProgressionObservation.Build(context, false); } catch (System.Exception ex) { ObservationWork.Failed("worldProgression", ex); }
                ObservationWork.Captured("worldProgression", Now() - began);
            }
            {
                var began = Now();
                observed.IdeologyActive = ModsConfig.IdeologyActive;
                try { observed.Ideology = NativeIdeologyObservation.Build(context); } catch (System.Exception ex) { ObservationWork.Failed("ideology", ex); }
                ObservationWork.Captured("ideology", Now() - began, observed.Ideology != null ? observed.Ideology.Precepts.Count : 0);
            }
            {
                var rooms = new Obs.ListRoomsRequest { Scope = Scope(), IncludeOutdoors = false, IncludeBoundary = false };
                var began = Now();
                var census = NativeRoomObservationTools.Read(map, rooms, context).Observed;
                ObservationWork.Captured("rooms", Now() - began, census != null ? census.Rooms.Count : 0);
                if (census != null) { observed.Rooms = census; }
            }
        }

        // On the main thread. Adds the subscription's parameterized
        // families: each named resource's sources.
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
        }
    }
}
