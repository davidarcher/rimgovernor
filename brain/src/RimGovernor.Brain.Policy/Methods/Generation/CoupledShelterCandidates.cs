using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal static class CoupledShelterCandidates
{
    internal static ForecastShelterPlan[] Append(IEnumerable<ForecastShelterGeometry> rooms, CoupledThermalRead? read,
        List<ForecastActivity> tasks, List<ForecastActivityOption> options,
        Dictionary<(string, string, string), ForecastActivityRoute> routes,
        Dictionary<(string, string), ActivityTaskCandidate> bindings, List<ForecastCommitment> alternatives, long deadline)
    {
        var plans = new List<ForecastShelterPlan>();
        foreach (var room in rooms.OrderBy(r => r.Id).Take(4))
        {
            var notes = new List<string>(); var prerequisites = new List<string>();
            var interior = room.Interior; var bounds = room.Bounds;
            var observed = read?.Cells.ToDictionary(c => c.Cell) ?? [];
            if (room.Outdoor || bounds.Width * bounds.Height != interior.Count || interior.Count == 0)
                notes.Add("unsupported_shelter_geometry");
            var ring = new List<Cell>();
            for (var x = bounds.X - 1; x <= bounds.X + bounds.Width; x++)
                for (var z = bounds.Z - 1; z <= bounds.Z + bounds.Height; z++)
                    if (x < bounds.X || x >= bounds.X + bounds.Width || z < bounds.Z || z >= bounds.Z + bounds.Height) ring.Add(new(x, z));
            string? Completion(string site) => bindings.Values.Where(b => b.Kind == PawnActivityKind.Work && b.Target == site
                && b.Job == "FinishFrame").Select(b => b.Id).Distinct().Order(StringComparer.Ordinal).FirstOrDefault();
            foreach (var cell in ring)
            {
                if (!observed.TryGetValue(cell, out var row)) { notes.Add("missing_shelter_geometry"); continue; }
                if (row.Solid && (!row.Door || row.DoorClosed)) continue;
                var finish = row.FutureSolid && (!row.FutureDoor || cell == room.Door) ? Completion(row.Site) : null;
                if (finish is null) notes.Add("unmodeled_shell_work"); else prerequisites.Add(finish);
            }
            var shell = prerequisites.Distinct().ToArray();
            foreach (var cell in interior)
            {
                if (!observed.TryGetValue(cell, out var row)) { notes.Add("missing_shelter_geometry"); continue; }
                if (row.Roof == "thin") continue;
                if (row.Roof != "none" || !row.BuildRoof || bounds.Width > 10 || bounds.Height > 10)
                { notes.Add("unsupported_roof_geometry_or_designation"); continue; }
                var id = "roof/" + cell.X + "/" + cell.Z;
                if (tasks.All(t => t.Id != id))
                {
                    var location = ForecastLocations.Of(cell);
                    tasks.Add(new(id, location, location, shell));
                    foreach (var worker in read!.Workers)
                    {
                        var approach = read.RoofRoutes.SingleOrDefault(r => r.Pawn == worker.Pawn && r.Cell == cell)?.ApproachTicks ?? default;
                        options.Add(new(id, worker.Pawn, worker.RoofCellWorkTicks, Known(0.0)));
                        routes.TryAdd((worker.Pawn, "start", location), new(worker.Pawn, "start", location, approach));
                        bindings[(id, worker.Pawn)] = new(id, worker.Pawn, EquatableArray.From(shell), PawnActivityKind.Work,
                            "roof", "BuildRoofs", "BuildRoof", 0, deadline, cell)
                        { Destination = cell };
                    }
                }
                prerequisites.Add(id);
                notes.Add("roof_per_cell_labor_conservative_native_jobs_may_cover_neighbors");
            }
            var source = read?.Sources.Where(s => interior.Contains(s.Cell)).OrderBy(s => s.Target, StringComparer.Ordinal).FirstOrDefault();
            if (source is null) notes.Add("unknown_heat_source");
            else if (!source.Complete)
            {
                var finish = Completion(source.Target);
                if (finish is null) notes.Add("unmodeled_heat_source_work"); else prerequisites.Add(finish);
            }
            var valid = notes.All(n => n == "roof_per_cell_labor_conservative_native_jobs_may_cover_neighbors");
            var cells = interior.Where(observed.ContainsKey).Select(c => observed[c]).ToArray();
            var temperature = valid && cells.All(c => c.Temperature.IsKnown) ? Known(cells.Average(c => c.Temperature.Value)) : default;
            var exchange = valid && read is { ThinRoofExchangePerTick.IsKnown: true, WallExchangePerTick.IsKnown: true }
                ? Known(read.ThinRoofExchangePerTick.Value + (ring.Count * read.WallExchangePerTick.Value
                    + ring.Where(observed.ContainsKey).Sum(c => observed[c].DoorExchangePerTick * .5)) / interior.Count) : default;
            plans.Add(new(room.Id, EquatableArray.From(interior), EquatableArray.From(prerequisites.Distinct()), temperature,
                read?.Outdoors ?? default, exchange, source?.HeatPerTick ?? default, source?.TargetTemperature ?? default,
                source?.SupplyTicks ?? default, EquatableArray.From(notes.Distinct()))
            { PowerNetwork = source?.PowerNetwork ?? "", Source = source?.Target ?? "", AdditionalWatts = source?.AdditionalWatts ?? default, SurplusWatts = source?.SurplusWatts ?? default });
            if (prerequisites.Count != 0) alternatives.Add(new("shelter/" + room.Id, [])
            { ActivityCompletions = EquatableArray.From(prerequisites.Distinct()), ExclusiveOutcome = "shelter/" + room.Id });
        }
        return plans.ToArray();
    }
}
