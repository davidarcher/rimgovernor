using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal sealed record CoupledShelterCandidatesResult(
    ForecastActivityOption[] Options, ForecastActivityRoute[] Routes,
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
            .OrderByDescending(r => read?.Sources.Any(s => census.Cells.TryGetValue(r.Id, out var held) && held.Contains(s.Cell)) == true)
            .ThenByDescending(r => r.Beds.Count > 0 || r.Pawns.Count > 0).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            var cells = EquatableArray.From(census.Cells.TryGetValue(room.Id, out var held) ? held : []);
            if (room.Id.Length == 0 || cells.Count == 0 || cells.Distinct().Count() != cells.Count)
            { findings.Add("unknown_observed_shelter_geometry:" + room.Id); continue; }
            var x = cells.Min(c => c.X); var z = cells.Min(c => c.Z);
            var bounds = new Rectangle(x, z, cells.Max(c => c.X) - x + 1, cells.Max(c => c.Z) - z + 1);
            // Enclosed includes a complete roof; requiring it would erase roof-repair opportunities.
            candidates.Add(new(new(ForecastShelterSource.Observed, room.Id), cells, bounds, default, false));
        }
        candidates.AddRange(planned);
        var unique = new List<ForecastShelterGeometry>();
        foreach (var room in candidates)
        {
            var same = unique.FindIndex(r => r.Interior.Count == room.Interior.Count && r.Interior.All(room.Interior.Contains));
            if (same >= 0)
            {
                // An observed room that realizes a planned room keeps the planned room's roof development (#3468 follow-up).
                if (room.Id.Source == ForecastShelterSource.Planned && unique[same] is { Id.Source: ForecastShelterSource.Observed, PlannedRoom: 0 } match
                    && int.TryParse(room.Id.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
                    unique[same] = match with { PlannedRoom = id };
                continue;
            }
            if (unique.Any(r => r.Interior.Any(room.Interior.Contains)))
            { findings.Add("overlapping_shelter_geometry:" + room.Id); continue; }
            unique.Add(room);
        }
        var truncated = unique.Count > 4;
        if (truncated) findings.Add("shelter_geometry_truncated");
        return (unique.Take(4).ToArray(), EquatableArray.From(findings), truncated);
    }
    /// <summary>
    /// Shelter alternatives over observed and planned rooms. A planned room's unbuilt shell and roof (#3277) and its
    /// planned heater (#3323) are its planned sites: planned is the round's planned-site facts and generated the sites PlannedRoomCandidates emitted,
    /// whose operations the composer shares; a shelter requires their finishes and never repeats them.
    /// </summary>
    internal static CoupledShelterCandidatesResult Generate(IEnumerable<ForecastShelterGeometry> rooms, Fact<RoomObservation> physicalRooms, CoupledThermalRead? read,
        IEnumerable<ForecastCompositionOperation> constructionOperations,
        IReadOnlyDictionary<(string, string), ActivityTaskCandidate> constructionBindings,
        IReadOnlyList<PlannedBuildSite> planned, IReadOnlyList<PlannedSiteOperations> generated, long deadline, double horizon)
    {
        var options = new List<ForecastActivityOption>();
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
            var groups = new List<EquatableArray<string>>(); var roofCells = new List<EquatableArray<string>>();
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
            // Planned shelter R is planned room R: its sites are planned/room{R}/... and complete at planned/room{R}.
            var plannedRoom = room.Id.Source == ForecastShelterSource.Planned && int.TryParse(room.Id.Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
            var roomSites = plannedRoom == 0 ? [] : planned.Where(s => s.Room == plannedRoom && s.Zone.Length == 0).ToArray();
            var made = generated.Where(g => plannedRoom != 0 && g.Room == plannedRoom).ToDictionary(g => g.Site, g => g.Finish, StringComparer.Ordinal);
            // The planned room whose roof unit the cells are: one generated as planned sites, or a standing observed room that
            // realizes a planned room. A planned room still raised by placed construction is not requested development (#3334).
            var developmentRoom = made.Count != 0 ? plannedRoom : room.PlannedRoom;
            var roomLocation = "planned/room" + plannedRoom;
            // One demand per planned kind (wall, door, roof), met by the finish of whichever generated site of it the
            // selection builds: the ring stuffs exclude each other, so any one realizes the shell. Each planned heat source is a
            // demand of its own site, so it gates the shelter on its own finish (#3679).
            void RequirePlanned(string id, IEnumerable<PlannedBuildSite> sites)
            {
                if (demands.Any(r => r.Id == id)) return;
                var ends = sites.Where(s => made.ContainsKey(s.Id)).Select(s => made[s.Id]).Distinct().Order(StringComparer.Ordinal).ToArray();
                groups.Add(EquatableArray.From(ends));
                demands.Add(new(id, capability, Known(1), roomLocation, ForecastRequirementKind.Reusable,
                    Known(EquatableArray.From(ends.Select(f => "completed-site/" + f))), Known(0d), Known(horizon)));
            }
            foreach (var cell in ring)
            {
                if (!observed.TryGetValue(cell, out var row)) { notes.Add("missing_shelter_geometry"); continue; }
                // A door shuts behind whoever passes (#3682): only one held open leaves the ring open.
                if (row.Solid && !row.DoorHeldOpen) continue;
                if (row.FutureSolid)
                {
                    if (row.FutureDoor && cell != room.Door || !RequireSite(row.Site)) notes.Add("unmodeled_shell_work");
                    continue;
                }
                var covering = roomSites.Where(s => s.Op is OpKind.WallIn or OpKind.DoorIn && s.Positions.Contains(cell)).ToArray();
                if (covering.Length == 0) notes.Add("unmodeled_shell_work");
                else if (!covering.Any(s => made.ContainsKey(s.Id))) notes.Add("planned_shell_unvalued");
                else
                {
                    var op = covering.First(s => made.ContainsKey(s.Id)).Op;
                    RequirePlanned(roomLocation + (op == OpKind.DoorIn ? "/door" : "/wall"), roomSites.Where(s => s.Op == op));
                }
            }
            var shell = EquatableArray.From(demands);
            // A generated planned roof covers its cells as one site; the per-cell native path stays for the rest.
            var plannedRoof = roomSites.Where(s => s.Op == OpKind.RoofIn && made.ContainsKey(s.Id)).ToArray();
            foreach (var cell in interior)
            {
                if (!observed.TryGetValue(cell, out var row)) { notes.Add("missing_shelter_geometry"); continue; }
                if (row.Roof == "thin") continue;
                if (plannedRoof.Any(s => s.Positions.Contains(cell)))
                {
                    if (row.Roof != "none" || bounds.Width > 10 || bounds.Height > 10) notes.Add("unsupported_roof_geometry_or_designation");
                    else RequirePlanned(roomLocation + "/roof", plannedRoof);
                    continue;
                }
                if (row.Roof != "none" || !row.BuildRoof || bounds.Width > 10 || bounds.Height > 10)
                { notes.Add("unsupported_roof_geometry_or_designation"); continue; }
                var id = "roof/" + room.Id + "/" + cell.X + "/" + cell.Z;
                var location = ForecastLocations.Of(cell);
                var task = new ForecastActivity(id, location, location, []);
                var supply = new ForecastCompositionSupply("completed-roof/" + id, new("CompletedRoofCell"), Known(1), location, Known(0d), Known(horizon))
                    { Producer = id, Kind = ForecastRequirementKind.Reusable, Provenance = "native-roof-cell:" + cell.X + "/" + cell.Z };
                operations.Add(id, new(task, shell, [supply]) { ChoiceGroup = "roof-cell/" + cell.X + "/" + cell.Z });
                foreach (var worker in read!.Workers)
                {
                    var approach = read.RoofRoutes.SingleOrDefault(r => r.Pawn == worker.Pawn && r.Cell == cell)?.ApproachTicks ?? default;
                    options.Add(new(id, worker.Pawn, worker.RoofCellWorkTicks, Known(0d)) { SkillWork = worker.SkillWork });
                    routes.TryAdd((worker.Pawn, "start", location), new(worker.Pawn, "start", location, approach));
                    bindings[(id, worker.Pawn)] = new(id, worker.Pawn, [], PawnActivityKind.Work,
                        // One claim target per cell: a shared "roof" target made two pawns on different cells refuse each other at admission.
                        "roof/" + cell.X + "/" + cell.Z, "BuildRoofs", "BuildRoof", 0, deadline, cell) { Destination = cell };
                }
                groups.Add([id]); roofCells.Add([id]);
                demands.Add(new("roof/" + id, supply.Resource, Known(1), location, ForecastRequirementKind.Reusable,
                    Known<EquatableArray<string>>([supply.Id]), Known(0d), Known(horizon)));
                notes.Add("roof_per_cell_labor_conservative_native_jobs_may_cover_neighbors");
            }
            var sources = read?.Sources.Where(s => interior.Contains(s.Cell)).ToArray() ?? [];
            // Every source in the room heats it (#3679); each one still to be built gates the shelter on its own finish.
            if (sources.Length == 0) notes.Add("unknown_heat_source");
            foreach (var source in sources)
                if (!source.Complete && !RequireSite(source.Target))
                {
                    // A planned heater (#3323) is its room's planned Build site: the shelter requires that site's finish.
                    if (made.ContainsKey(source.Target)) RequirePlanned("heater/" + source.Target, roomSites.Where(s => s.Id == source.Target));
                    else if (roomSites.Any(s => s.Id == source.Target)) notes.Add("planned_heat_source_unvalued");
                    else notes.Add("unmodeled_heat_source_work");
                }
            var valid = notes.All(n => n == "roof_per_cell_labor_conservative_native_jobs_may_cover_neighbors");
            var cells = interior.Where(observed.ContainsKey).Select(c => observed[c]).ToArray();
            var temperature = valid && cells.Length != 0 && cells.All(c => c.Temperature.IsKnown) ? Known(cells.Average(c => c.Temperature.Value)) : default;
            var walls = valid && read is { WallExchangePerTick.IsKnown: true }
                ? Known((ring.Count * read.WallExchangePerTick.Value + ring.Where(observed.ContainsKey).Sum(c => observed[c].DoorExchangePerTick * .5)) / interior.Count) : default;
            var exchange = walls.IsKnown && read is { ThinRoofExchangePerTick.IsKnown: true } ? Known(read.ThinRoofExchangePerTick.Value + walls.Value) : default;
            // The room as it stands (#3534) holds only behind a closed ring; its roof terms are RoomTempTracker's coverage
            // terms, each open cell at the no-roof rate and the rest at the thin-roof rate.
            var open = cells.Count(c => c.Roof == "none");
            var standing = exchange.IsKnown && read is { NoRoofExchangePerTick.IsKnown: true }
                && ring.All(c => observed.TryGetValue(c, out var row) && row.Solid && !row.DoorHeldOpen)
                ? Known((read.ThinRoofExchangePerTick.Value * (interior.Count - open) + read.NoRoofExchangePerTick.Value * open) / interior.Count + walls.Value) : default;
            plans.Add(new(room.Id, interior, [], temperature, read?.Outdoors ?? default, exchange, EquatableArray.From(sources), EquatableArray.From(notes.Distinct()))
                { AlternativePrerequisites = EquatableArray.From(groups), PlannedRoom = developmentRoom, StandingExchangePerTick = standing,
                    RoofCells = developmentRoom == 0 ? [] : EquatableArray.From(roofCells) });
            if (demands.Count != 0) roots.Add("shelter/" + room.Id, EquatableArray.From(demands));
        }
        return new(options.ToArray(), routes.Values.ToArray(), bindings, operations.Values.ToArray(), [], roots, plans.ToArray(), geometry.Findings, geometry.Truncated);
    }
}
