using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimGovernor.Native
{
    /// <summary>
    /// One snapshot stream frame: every state family, plus the
    /// subscription's parameterized ones, each read exactly as its dedicated tool answers
    /// the request shape the controller issues, all in one game-thread hop
    /// so they describe one tick. Required census failures (including buildings)
    /// fail the frame explicitly; optional sections may be absent. Each section's
    /// read is timed into the observation account under its
    /// BundleSnapshot name.
    /// </summary>
    internal static partial class SnapshotFrames
    {
        private static long Now() { return System.Diagnostics.Stopwatch.GetTimestamp(); }

        // On the main thread: one snapshot stream frame, every state
        // family plus the subscription's and the whole-map cell grid
        // (absent when its read failed), or null when the map has no
        // readable context.
        internal static RimGovernor.Brain.GameFacts.Observations.BundleSnapshot? Capture(Map map, RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest request)
        {
            if (!GameThread.TryReadContext(map, out var context, out _)) return null;
            var observed = new RimGovernor.Brain.GameFacts.Observations.BundleSnapshot { Context = context };
            RimGovernor.Brain.GameFacts.Observations.BundleSnapshot? sections = null;
            var referenced = NativeRef.Collect(() => sections = ReadSections(map, request, context, observed));
            if (sections == null) return null;
            observed = sections;
            var thingsBegan = Now();
            try { observed = observed with { Things = NativeObservationTools.Things(referenced, context) }; } catch (System.Exception ex) { ObservationWork.Failed("things", ex); }
            ObservationWork.Captured("things", Now() - thingsBegan, observed.Things != null ? observed.Things.Things.Count : 0);
            var gridBegan = Now();
            try { observed = observed with { Grid = CellGridReader.Read(map) }; } catch (System.Exception ex) { ObservationWork.Failed("grid", ex); }
            ObservationWork.Captured("grid", Now() - gridBegan, observed.Grid != null ? observed.Grid.Held.Length : 0);
            return observed;
        }

        // On the main thread: every section of the frame but its things
        // table and grid, false when the status census fails. The things
        // the sections reference become the things table.
        private static RimGovernor.Brain.GameFacts.Observations.BundleSnapshot? ReadSections(Map map, RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest request, RimGovernor.Brain.GameFacts.Common.ObservationContext context, RimGovernor.Brain.GameFacts.Observations.BundleSnapshot observed)
        {
            var statusBegan = Now();
            var emergency = SnapshotSectionException.Read(nameof(RimGovernor.Brain.GameFacts.Observations.BundleSnapshot.Emergency), () => NativeObservationTools.Status(map, context));
            ObservationWork.Captured("emergency", Now() - statusBegan, 0);
            observed = observed with { Emergency = emergency };
            ReadFamilies(map, context, ref observed, request);
            ReadStepFamilies(map, context, ref observed);
            var combatBegan = Now();
            Supervisor.EnsureHazardHooks(); Supervisor.EnsureCombatHooks();
            observed = CombatMirror.Capture(map, observed);
            ObservationWork.Captured("combat", Now() - combatBegan, observed.CombatPawns.Count + observed.CombatEvents.Count);
            var inputsBegan = Now();
            CombatInputs(map, context, ref observed, request);
            ObservationWork.Captured("combatInputs", Now() - inputsBegan, observed.CombatDoors.Count + observed.CombatMortars.Count);
            return observed;
        }

        private static bool Engaged(RimGovernor.Brain.GameFacts.Observations.ThreatPawn row) => row.MentalState is not null || row.FactionHostile || row.PrisonBreak || row.PredatorHunt;

        // On the main thread. The defense planner's other combat inputs
        //, the pawn detail being the pawn table's: the hive
        // temperature, damaged doors, mortars and the lines of fire from
        // ranged colonists to hostile buildings, while the census lists a
        // threat or a colonist in a mental state. Each is omitted when it
        // fails to read.
        private static void CombatInputs(Map map, RimGovernor.Brain.GameFacts.Common.ObservationContext context, ref RimGovernor.Brain.GameFacts.Observations.BundleSnapshot observed, RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest request)
        {
            var census = observed.Emergency;
            var threats = census?.Threats;
            var table = observed.Pawns;
            if (census == null || threats == null || table == null) return;
            var colonistIds = new HashSet<string>(census.Colonists.Select(p => p.Id ?? ""), System.StringComparer.Ordinal);
            var colonists = table.Pawns.Where(p => colonistIds.Contains(p.Pawn.Id ?? "")).ToList();
            if (!threats.Pawns.Any(Engaged) && threats.HostileBuildings.Count == 0
                && !colonists.Any(p => p.MentalState is not null)) return;
            // The damaged player doors, for a fight's door repair.
            var doors = new List<RimGovernor.Brain.GameFacts.Mirror.CombatDoorRow>(observed.CombatDoors);
            foreach (var door in map.listerBuildings.AllBuildingsColonistOfClass<RimWorld.Building_Door>())
            {
                if (!door.Spawned || !door.def.useHitPoints || door.HitPoints >= door.MaxHitPoints) continue;
                doors.Add(new RimGovernor.Brain.GameFacts.Mirror.CombatDoorRow { Cell = new RimGovernor.Brain.Domain.Cell { X = door.Position.x, Z = door.Position.z } });
            }
            // The unroofed player mortars, for counter-battery.
            var mortars = new List<RimGovernor.Brain.GameFacts.Mirror.CombatMortarRow>(observed.CombatMortars);
            foreach (var mortar in map.listerBuildings.AllBuildingsColonistOfClass<RimWorld.Building_TurretGun>())
            {
                if (!mortar.Spawned || mortar.def.building?.IsMortar != true || mortar.AttackVerb == null || map.roofGrid.Roofed(mortar.Position)) continue;
                // The loaded shell, for the shell the fight asks for.
                var loaded = mortar.gun?.TryGetComp<RimWorld.CompChangeableProjectile>()?.LoadedShell;
                mortars.Add(new RimGovernor.Brain.GameFacts.Mirror.CombatMortarRow { Id = mortar.GetUniqueLoadID(),
                    Cell = new RimGovernor.Brain.Domain.Cell { X = mortar.Position.x, Z = mortar.Position.z },
                    MinRange = mortar.AttackVerb.verbProps.minRange, MaxRange = mortar.AttackVerb.verbProps.range,
                    LoadedShell = loaded?.defName });
            }
            observed = observed with { CombatDoors = [.. doors], CombatMortars = [.. mortars] };
            var firing = new List<IntVec3>();
            foreach (var row in colonists)
            {
                if (row.Pawn is not { Place: RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap { Cell: var position } } pawnRef || row.Equipment is not { PrimaryId: { } primaryId }) continue;
                // The shooter's primary weapon is the live one: the wire rows no
                // longer carry its class or range, the catalog does.
                var shooter = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == (pawnRef.Id ?? ""));
                var weapon = shooter?.equipment?.Primary;
                if (weapon == null || weapon.GetUniqueLoadID() != primaryId || !weapon.def.IsRangedWeapon) continue;
                var gun = weapon.def.Verbs?.FirstOrDefault(v => !v.IsMeleeAttack && v.range > 0);
                if (gun == null || float.IsNaN(gun.range) || float.IsInfinity(gun.range)) continue;
                var cell = new IntVec3(position.X, 0, position.Z);
                if (!firing.Contains(cell)) firing.Add(cell);
            }
            var approach = new List<IntVec3>();
            foreach (var building in threats.HostileBuildings)
            {
                var lo = building.Occupied.Minimum; var hi = building.Occupied.Maximum;
                for (var z = lo.Z; z <= hi.Z; z++)
                    for (var x = lo.X; x <= hi.X; x++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        if (!approach.Contains(cell)) approach.Add(cell);
                    }
            }
            if (firing.Count == 0 || approach.Count == 0 || firing.Concat(approach).Any(c => !c.InBounds(map))) return;
            try { observed = observed with { CombatLinesOfFire = NativeDefenseObservationTools.Lines(map, firing, approach, context, request.LinesOfFireBudget is RimGovernor.Brain.GameFacts.Observations.FrameBudget.Millis lines ? lines.Value : uint.MaxValue) }; }
            catch (System.Exception ex) { ObservationWork.Failed("combatLinesOfFire", ex); }
        }

        // The frame's colony facts request: the planning form.
        private static RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest ColonyRequest(RimGovernor.Brain.GameFacts.Observations.ReadScope scope) => new RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest { Scope = scope };

        // On the main thread. Adds the census families to observed, each
        // keyed as its dedicated read. A section that throws fails the frame
        // (SnapshotSectionException); none is omitted.
        private static void ReadFamilies(Map map, RimGovernor.Brain.GameFacts.Common.ObservationContext context, ref RimGovernor.Brain.GameFacts.Observations.BundleSnapshot observed, RimGovernor.Brain.GameOrders.Observations.SnapshotStreamRequest request)
        {
            RimGovernor.Brain.GameFacts.Observations.ReadScope Scope() => new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = context.Identity };
            {
                var began = Now();
                observed = observed with { ColonyFacts = SnapshotSectionException.Read("colonyFacts", () => NativeColonyObservationTools.Read(map, ColonyRequest(Scope()), context, request.RoutePathBudget is RimGovernor.Brain.GameFacts.Observations.FrameBudget.Millis route ? route.Value : (uint?)null)) };
                ObservationWork.Captured("colonyFacts", Now() - began, observed.ColonyFacts?.Resources.Count ?? 0);
            }
            {
                var began = Now();
                observed = observed with { Population = SnapshotSectionException.Read("population", () => NativePopulationObservation.Population(map, new RimGovernor.Brain.GameOrders.Observations.PopulationRequest { Scope = Scope() })) };
                ObservationWork.Captured("population", Now() - began, observed.Population?.Persons.Count ?? 0);
            }
            {
                var began = Now();
                observed = observed with { Research = SnapshotSectionException.Read("research", () => NativeResearchObservationTools.Section(map, new RimGovernor.Brain.GameOrders.Observations.ResearchRequest { Scope = Scope(), ProgressOnly = true }, context)) };
                ObservationWork.Captured("research", Now() - began, observed.Research?.Projects.Count ?? 0);
            }
            {
                var began = Now();
                observed = observed with { Pawns = SnapshotSectionException.Read("pawns", () => NativePawnObservationTools.Table(map, context)) };
                ObservationWork.Captured("pawns", Now() - began, observed.Pawns != null ? observed.Pawns.Pawns.Count : 0);
            }
        }

        // On the main thread. Adds the step families, each the exact
        // read its dedicated tool answers for the request shape the
        // controller issues, omitting any that fails or is not observed.
        private static void ReadStepFamilies(Map map, RimGovernor.Brain.GameFacts.Common.ObservationContext context, ref RimGovernor.Brain.GameFacts.Observations.BundleSnapshot observed)
        {
            RimGovernor.Brain.GameFacts.Observations.ReadScope Scope() => new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = context.Identity };
            {
                var began = Now();
                var buildings = SnapshotSectionException.Read(nameof(RimGovernor.Brain.GameFacts.Observations.BundleSnapshot.Buildings), () => NativeBuildingObservationTools.Read(map, new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { Scope = Scope() }, context));
                ObservationWork.Captured("buildings", Now() - began, buildings.Buildings.Count);
                observed = observed with { Buildings = buildings };
            }
            {
                var began = Now();
                var bills = SnapshotSectionException.Read(nameof(RimGovernor.Brain.GameFacts.Observations.BundleSnapshot.Bills), () => NativeBillsObservationTools.Read(map, new RimGovernor.Brain.GameOrders.Observations.BillsRequest { Scope = Scope() }, context));
                ObservationWork.Captured("bills", Now() - began, bills.Benches.Count);
                observed = observed with { Bills = bills };
            }
            {
                var began = Now();
                observed = observed with { Zones = SnapshotSectionException.Read("zones", () => NativeZoneObservationTools.Read(map, new RimGovernor.Brain.GameOrders.Observations.ListZonesRequest { Scope = Scope() }, context)) };
                ObservationWork.Captured("zones", Now() - began, observed.Zones?.Zones.Count ?? 0);
            }
            {
                var began = Now();
                try { observed = observed with { Traders = NativeTradeObservation.Traders(map, context) }; } catch (System.Exception ex) { ObservationWork.Failed("traders", ex); }
                ObservationWork.Captured("traders", Now() - began, observed.Traders != null ? observed.Traders.Traders.Count : 0);
            }
            {
                var began = Now();
                observed = observed with { WorldProgression = SnapshotSectionException.Read("worldProgression", () => NativeWorldProgressionObservation.Build(context)) };
                ObservationWork.Captured("worldProgression", Now() - began);
            }
            {
                var began = Now();
                // Build is null only without the expansion; with it, a missing primary ideoligion throws inside it.
                observed = observed with { Ideology = SnapshotSectionException.Read("ideology", () => NativeIdeologyObservation.Build(map) is { } snapshot
                    ? (RimGovernor.Brain.GameFacts.Observations.IdeologyFrame)new RimGovernor.Brain.GameFacts.Observations.IdeologyFrame.Installed { Snapshot = snapshot }
                    : new RimGovernor.Brain.GameFacts.Observations.IdeologyFrame.NotInstalled()) };
                ObservationWork.Captured("ideology", Now() - began, observed.Ideology is RimGovernor.Brain.GameFacts.Observations.IdeologyFrame.Installed installed ? installed.Snapshot.Precepts.Count : 0);
            }
            {
                var rooms = new RimGovernor.Brain.GameOrders.Observations.ListRoomsRequest { Scope = Scope() };
                var began = Now();
                observed = observed with { Rooms = SnapshotSectionException.Read("rooms", () => NativeRoomObservationTools.Read(map, rooms, context)) };
                ObservationWork.Captured("rooms", Now() - began, observed.Rooms?.Rooms.Count ?? 0);
            }
        }

    }
}
