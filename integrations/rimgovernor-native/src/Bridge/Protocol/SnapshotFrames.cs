#nullable enable
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// One snapshot stream frame (#858): every state family the
    /// subscription names, each read exactly as its dedicated tool answers
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
        // family the request names, or
        // null when the map has no readable context.
        internal static Obs.BundleSnapshot? Capture(Map map, Obs.BundleRequest request)
        {
            if (!ProtoBoundary.TryReadContext(map, out var context, out _)) return null;
            var observed = new Obs.BundleSnapshot { Context = context, Paused = Find.TickManager.Paused };
            var status = new Obs.StatusRequest { Scope = new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() },
                Colonists = true, Threats = true, ColonistDetail = false, Page = new Common.PageRequest { Limit = 256 } };
            var statusBegan = Now();
            var statusRead = NativeObservationTools.TryStatus(map, status, context, out var emergency, out _);
            ObservationWork.Captured("emergency", Now() - statusBegan, 0);
            if (!statusRead) return null;
            observed.Emergency = emergency;
            ReadFamilies(map, request, context, observed);
            ReadStepFamilies(map, request, context, observed);
            var combatBegan = Now();
            Supervisor.EnsureHazardHooks(); Supervisor.EnsureCombatHooks();
            CombatMirror.Capture(map, observed);
            ObservationWork.Captured("combat", Now() - combatBegan, observed.CombatPawns.Count + observed.CombatEvents.Count);
            return observed;
        }

        // On the main thread. Adds the requested census families to observed,
        // each keyed as its dedicated read, omitting any that fails.
        private static void ReadFamilies(Map map, Obs.BundleRequest request, Common.ObservationContext context, Obs.BundleSnapshot observed)
        {
            Obs.ReadScope Scope() => new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() };
            if (request.HasColonyFacts && request.ColonyFacts)
            {
                var began = Now();
                var read = NativeColonyObservationTools.TryRead(map, new Obs.ColonyFactsRequest { Scope = Scope(), Planning = true, Page = new Common.PageRequest { Limit = 256 } }, context, out var colony);
                ObservationWork.Captured("colonyFacts", Now() - began, read ? colony!.Resources.Count : 0);
                if (read) { observed.ColonyFacts = colony; }
            }
            if (request.HasPopulation && request.Population)
            {
                var began = Now();
                var read = NativePopulationObservation.TryRead(map, new Obs.PopulationRequest { Scope = Scope() }, context, out var population);
                ObservationWork.Captured("population", Now() - began, read ? population!.Persons.Count : 0);
                if (read) { observed.Population = population; }
            }
            if (request.HasResearch && request.Research)
            {
                var began = Now();
                var read = NativeResearchObservationTools.TryRead(map, new Obs.ResearchRequest { Scope = Scope(), IncludeLocked = true, IncludeFinished = true, Page = new Common.PageRequest { Limit = 256 } }, context, out var research);
                ObservationWork.Captured("research", Now() - began, read ? research!.Projects.Count : 0);
                if (read) { observed.Research = research; }
            }
            if (request.HasColonistPawns && request.ColonistPawns && observed.Emergency?.Colonists != null
                && observed.Emergency.Colonists.Completeness?.Page?.Complete == true && observed.Emergency.Colonists.Pawns.Count > 0)
            {
                var pawns = new Obs.ListPawnsRequest {
                    Scope = Scope(),
                    Filter = new Obs.PawnFilter { IncludeDead = true },
                    Details = new Obs.PawnDetails { Needs = true, Health = true, Equipment = true, Biography = true, Settings = true, Social = true, Animals = true, Work = true, Schedule = true },
                    Page = new Common.PageRequest { Limit = (uint)observed.Emergency.Colonists.Pawns.Count },
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

        // On the main thread. Adds the requested step families (#593), each
        // the exact read its dedicated tool answers for the request shape the
        // controller issues, omitting any that fails or is not observed.
        private static void ReadStepFamilies(Map map, Obs.BundleRequest request, Common.ObservationContext context, Obs.BundleSnapshot observed)
        {
            Obs.ReadScope Scope() => new Obs.ReadScope { ExpectedIdentity = context.Identity.Clone() };
            if (request.HasBuildings && request.Buildings)
            {
                var began = Now();
                var buildings = NativeBuildingObservationTools.Read(map, new Obs.ListBuildingsRequest { Scope = Scope(), PlayerOnly = true, Category = "artificial", Page = new Common.PageRequest { Limit = 256 } }, context).Observed;
                ObservationWork.Captured("buildings", Now() - began, buildings != null ? buildings.Buildings.Count : 0);
                if (buildings != null) { observed.Buildings = buildings; }
            }
            if (request.HasBuiltBuildings && request.BuiltBuildings)
            {
                var built = new Obs.ListBuildingsRequest { Scope = Scope(), PlayerOnly = true, Category = "artificial", Page = new Common.PageRequest { Limit = 256 } };
                built.Statuses.Add("built");
                var began = Now();
                var buildings = NativeBuildingObservationTools.Read(map, built, context).Observed;
                ObservationWork.Captured("builtBuildings", Now() - began, buildings != null ? buildings.Buildings.Count : 0);
                if (buildings != null) { observed.BuiltBuildings = buildings; }
            }
            if (request.HasBills && request.Bills)
            {
                var began = Now();
                var bills = NativeBillsObservationTools.Read(map, new Obs.BillsRequest { Scope = Scope(), Page = new Common.PageRequest { Limit = 256 } }, context).Observed;
                ObservationWork.Captured("bills", Now() - began, bills != null ? bills.Benches.Count : 0);
                if (bills != null) { observed.Bills = bills; }
            }
            if (request.HasZones && request.Zones)
            {
                var began = Now();
                var zones = NativeZoneObservationTools.Read(map, new Obs.ListZonesRequest { Scope = Scope(), Page = new Common.PageRequest { Limit = 16 } }, context).Observed;
                ObservationWork.Captured("zones", Now() - began, zones != null ? zones.Zones.Count : 0);
                if (zones != null) { observed.Zones = zones; }
            }
            if (request.HasTraders && request.Traders)
            {
                var began = Now();
                try { observed.Traders = NativeTradeObservation.Traders(map, context); } catch (System.Exception) { }
                ObservationWork.Captured("traders", Now() - began, observed.Traders != null ? observed.Traders.Traders.Count : 0);
            }
            if (request.HasWorldProgression && request.WorldProgression)
            {
                var began = Now();
                try { observed.WorldProgression = NativeWorldProgressionObservation.Build(context, false); } catch (System.Exception) { }
                ObservationWork.Captured("worldProgression", Now() - began);
            }
            foreach (var resource in request.ResourceSources)
            {
                if (!ProtoBoundary.IsIdentifier(resource)) continue;
                var began = Now();
                var sources = NativeResourceSourcesTool.Read(map, new Obs.ResourceSourcesRequest { Scope = Scope(), Resource = resource, IncludeDevelopment = false }, context).Observed;
                ObservationWork.Captured("resourceSources", Now() - began, sources != null ? sources.Sources.Count : 0);
                if (sources != null) { observed.ResourceSources.Add(sources); }
            }
            var window = request.PlanningWindow;
            if (window?.Region?.Minimum != null && window.Region.Maximum != null)
            {
                var width = (long)window.Region.Maximum.X - window.Region.Minimum.X + 1;
                var height = (long)window.Region.Maximum.Z - window.Region.Minimum.Z + 1;
                if (width >= 1 && height >= 1 && width * height <= 65536)
                {
                    var cells = new Obs.GetCellsRequest { Scope = Scope(), Rectangle = window.Region.Clone(), Compact = true,
                        Fields = new Obs.CellFields { Terrain = false, Roof = true, Visibility = true, Traversal = true, Zone = true, Areas = false, Things = false, Designations = false, Room = true, Growth = true },
                        Page = new Common.PageRequest { Limit = (uint)(width * height) } };
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
        }
    }
}
