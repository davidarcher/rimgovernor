using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal sealed record CoupledShelterCandidatesResult(
    ForecastActivity[] Tasks, ForecastActivityOption[] Options, ForecastActivityRoute[] Routes,
    Dictionary<(string, string), ActivityTaskCandidate> Bindings,
    ForecastCompositionOperation[] Operations, ForecastCompositionSupply[] Observed,
    Dictionary<string, EquatableArray<ForecastCompositionRequirement>> Roots, ForecastShelterPlan[] Plans, EquatableArray<string> Findings, bool Truncated);

internal static class CoupledShelterCandidates
{
    // Native rooms are current physical opportunities, not reservations of strategic layout intent.
    // Prefer observed heated rooms within the same finite bound; omitted rooms remain explicit.
    private static (ForecastShelterGeometry[] Rooms, EquatableArray<string> Findings, bool Truncated) SelectGeometry(
        IEnumerable<ForecastShelterGeometry> planned, Fact<RoomObservation> physical, CoupledThermalRead? read)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var candidates = new List<ForecastShelterGeometry>();
        if (!physical.TryGetValue(out var census)) findings.Add("unknown_observed_shelter_geometry");
        else foreach (var room in census.Rooms
            .OrderByDescending(r => read?.Sources.Any(s => r.Cells.Contains(s.Cell)) == true)
            .ThenByDescending(r => r.Beds.Count > 0 || r.Pawns.Count > 0).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            if (room.Id.Length == 0 || room.Cells.Count == 0 || room.Cells.Distinct().Count() != room.Cells.Count)
            { findings.Add("unknown_observed_shelter_geometry:" + room.Id); continue; }
            var x = room.Cells.Min(c => c.X); var z = room.Cells.Min(c => c.Z);
            var bounds = new Rectangle(x, z, room.Cells.Max(c => c.X) - x + 1, room.Cells.Max(c => c.Z) - z + 1);
            // Enclosed includes a complete roof; requiring it would erase roof-repair opportunities.
            candidates.Add(new(new(ForecastShelterSource.Observed, room.Id), room.Cells, bounds, default, false));
        }
        candidates.AddRange(planned);
        var unique = new List<ForecastShelterGeometry>();
        foreach (var room in candidates)
        {
            var same = unique.FirstOrDefault(r => r.Interior.Count == room.Interior.Count && r.Interior.All(room.Interior.Contains));
            if (same is not null) continue;
            if (unique.Any(r => r.Interior.Any(room.Interior.Contains)))
            { findings.Add("overlapping_shelter_geometry:" + room.Id); continue; }
            unique.Add(room);
        }
        var truncated = unique.Count > 4;
        if (truncated) findings.Add("shelter_geometry_truncated");
        return (unique.Take(4).ToArray(), EquatableArray.From(findings), truncated);
    }
    internal static CoupledShelterCandidatesResult Generate(IEnumerable<ForecastShelterGeometry> rooms, Fact<RoomObservation> physicalRooms, CoupledThermalRead? read,
        IEnumerable<ForecastCompositionOperation> constructionOperations,
        IReadOnlyDictionary<(string, string), ActivityTaskCandidate> constructionBindings, long deadline, double horizon)
    {
        var tasks = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new Dictionary<(string, string, string), ForecastActivityRoute>();
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        var operations = constructionOperations.ToDictionary(o => o.Activity.Id, StringComparer.Ordinal);
        var roots = new Dictionary<string, EquatableArray<ForecastCompositionRequirement>>(StringComparer.Ordinal);
        var plans = new List<ForecastShelterPlan>();
        var capability = new Resource("CompletedPhysicalSite");
        var observed = read?.Cells.ToDictionary(c => c.Cell) ?? [];
        var finishes = constructionBindings.Values.Where(b => b.Kind == PawnActivityKind.Work && b.Job == "FinishFrame")
            .GroupBy(b => b.Target).ToDictionary(g => g.Key, g => g.Select(b => b.Id).Distinct().Order(StringComparer.Ordinal).ToArray());
        foreach (var (site, ids) in finishes)
            foreach (var id in ids)
                if (operations.TryGetValue(id, out var operation))
                    operations[id] = operation with { Supplies = [.. operation.Supplies,
                        new("completed-site/" + id, capability, Known(1), operation.Activity.EndLocation, Known(0d), Known(horizon))
                        { Producer = id, Lot = site, Kind = ForecastRequirementKind.Reusable, Provenance = "native-finish-site:" + site }] };
        var geometry = SelectGeometry(rooms, physicalRooms, read);
        foreach (var room in geometry.Rooms)
        {
            var notes = new List<string>(); var demands = new List<ForecastCompositionRequirement>();
            var groups = new List<EquatableArray<string>>();
            var interior = room.Interior; var bounds = room.Bounds;
            if (room.Outdoor || bounds.Width * bounds.Height != interior.Count || interior.Count == 0)
                notes.Add("unsupported_shelter_geometry");
            var ring = new List<Cell>();
            for (var x = bounds.X - 1; x <= bounds.X + bounds.Width; x++)
                for (var z = bounds.Z - 1; z <= bounds.Z + bounds.Height; z++)
                    if (x < bounds.X || x >= bounds.X + bounds.Width || z < bounds.Z || z >= bounds.Z + bounds.Height) ring.Add(new(x, z));
            bool RequireSite(string site)
            {
                if (!finishes.TryGetValue(site, out var ids) || ids.Length == 0) return false;
                var available = ids.Where(operations.ContainsKey).ToArray();
                if (available.Length == 0) return false;
                var locations = available.Select(id => operations[id].Activity.EndLocation).Distinct().ToArray();
                if (locations.Length != 1) return false;
                if (demands.Any(r => r.Id == "site/" + site)) return true;
                groups.Add(EquatableArray.From(available));
                demands.Add(new("site/" + site, capability, Known(1), locations[0], ForecastRequirementKind.Reusable,
                    Known(EquatableArray.From(available.Select(id => "completed-site/" + id))), Known(0d), Known(horizon)));
                return true;
            }
            foreach (var cell in ring)
            {
                if (!observed.TryGetValue(cell, out var row)) { notes.Add("missing_shelter_geometry"); continue; }
                if (row.Solid && (!row.Door || row.DoorClosed)) continue;
                if (!row.FutureSolid || row.FutureDoor && cell != room.Door || !RequireSite(row.Site)) notes.Add("unmodeled_shell_work");
            }
            var shell = EquatableArray.From(demands);
            foreach (var cell in interior)
            {
                if (!observed.TryGetValue(cell, out var row)) { notes.Add("missing_shelter_geometry"); continue; }
                if (row.Roof == "thin") continue;
                if (row.Roof != "none" || !row.BuildRoof || bounds.Width > 10 || bounds.Height > 10)
                { notes.Add("unsupported_roof_geometry_or_designation"); continue; }
                var id = "roof/" + room.Id + "/" + cell.X + "/" + cell.Z;
                var location = ForecastLocations.Of(cell);
                var task = new ForecastActivity(id, location, location, []);
                tasks.Add(task);
                var supply = new ForecastCompositionSupply("completed-roof/" + id, new("CompletedRoofCell"), Known(1), location, Known(0d), Known(horizon))
                    { Producer = id, Kind = ForecastRequirementKind.Reusable, Provenance = "native-roof-cell:" + cell.X + "/" + cell.Z };
                operations.Add(id, new(task, shell, [supply]) { ChoiceGroup = "roof-cell/" + cell.X + "/" + cell.Z });
                foreach (var worker in read!.Workers)
                {
                    var approach = read.RoofRoutes.SingleOrDefault(r => r.Pawn == worker.Pawn && r.Cell == cell)?.ApproachTicks ?? default;
                    options.Add(new(id, worker.Pawn, worker.RoofCellWorkTicks, Known(0d)));
                    routes.TryAdd((worker.Pawn, "start", location), new(worker.Pawn, "start", location, approach));
                    bindings[(id, worker.Pawn)] = new(id, worker.Pawn, [], PawnActivityKind.Work,
                        // One claim target per cell: a shared "roof" target made two pawns on different cells refuse each other at admission.
                        "roof/" + cell.X + "/" + cell.Z, "BuildRoofs", "BuildRoof", 0, deadline, cell) { Destination = cell };
                }
                groups.Add([id]);
                demands.Add(new("roof/" + id, supply.Resource, Known(1), location, ForecastRequirementKind.Reusable,
                    Known<EquatableArray<string>>([supply.Id]), Known(0d), Known(horizon)));
                notes.Add("roof_per_cell_labor_conservative_native_jobs_may_cover_neighbors");
            }
            var sources = read?.Sources.Where(s => interior.Contains(s.Cell)).ToArray() ?? [];
            var source = sources.Length == 1 ? sources[0] : null;
            if (sources.Length > 1) notes.Add("unsupported_multiple_heat_sources");
            else if (source is null) notes.Add("unknown_heat_source");
            else if (!source.Complete && !RequireSite(source.Target)) notes.Add("unmodeled_heat_source_work");
            var valid = notes.All(n => n == "roof_per_cell_labor_conservative_native_jobs_may_cover_neighbors");
            var cells = interior.Where(observed.ContainsKey).Select(c => observed[c]).ToArray();
            var temperature = valid && cells.Length != 0 && cells.All(c => c.Temperature.IsKnown) ? Known(cells.Average(c => c.Temperature.Value)) : default;
            var exchange = valid && read is { ThinRoofExchangePerTick.IsKnown: true, WallExchangePerTick.IsKnown: true }
                ? Known(read.ThinRoofExchangePerTick.Value + (ring.Count * read.WallExchangePerTick.Value
                    + ring.Where(observed.ContainsKey).Sum(c => observed[c].DoorExchangePerTick * .5)) / interior.Count) : default;
            plans.Add(new(room.Id, interior, [], temperature, read?.Outdoors ?? default, exchange, source?.HeatPerTick ?? default,
                source?.TargetTemperature ?? default, source?.SupplyTicks ?? default, EquatableArray.From(notes.Distinct()))
                { AlternativePrerequisites = EquatableArray.From(groups), PowerNetwork = source?.PowerNetwork ?? "", Source = source?.Target ?? "",
                    AdditionalWatts = source?.AdditionalWatts ?? default, SurplusWatts = source?.SurplusWatts ?? default });
            if (demands.Count != 0) roots.Add("shelter/" + room.Id, EquatableArray.From(demands));
        }
        return new(tasks.ToArray(), options.ToArray(), routes.Values.ToArray(), bindings, operations.Values.ToArray(), [], roots, plans.ToArray(), geometry.Findings, geometry.Truncated);
    }
}
