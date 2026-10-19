using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>The composition operations one planned site was generated as, and its finish.</summary>
internal sealed record PlannedSiteOperations(string Site, int Room, string Stuff, string[] Operations, string Finish);

internal sealed record PlannedRoomCandidatesResult(
    ForecastCompositionOperation[] Operations, ForecastCompositionSupply[] Observed, ForecastActivityOption[] Options,
    ForecastActivityRoute[] Routes, ForecastCommitment[] Roots, ForecastMaterialLot[] Lots,
    PlannedSiteOperations[] Sites, PlannedRoomAlternative[] Rooms,
    IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings, EquatableArray<string> Findings, bool Truncated);

/// <summary>
/// Planned rooms as construction activities (#3276). Each planned site of a valued alternative is its own comparison
/// root, named by the site id and shared by the room's alternatives: its delivery, its clearing prerequisites and its
/// finish, in the placed-site shape (ConstructionMethodCandidates). A completed site is one unit of requested development
/// whichever root builds it. An alternative root requires every site of its alternative and carries no relief of its own. Nothing binds
/// until placement (#3278), except a Deconstruct clearing: its building stands now, so it binds as the pawn's Deconstruct work on it (#3557).
/// </summary>
internal static class PlannedRoomCandidates
{
    /// <summary>Planned composition operations generated per review; a room that would pass it is left out whole.</summary>
    private const int OperationBudget = 96;

    /// <summary>Material sources one site's delivery may choose between.</summary>
    private const int SourcesPerSite = 4;

    private enum Assessment { Valued, Unvalued, Infeasible }

    internal static PlannedRoomCandidatesResult Generate(EquatableArray<PlannedBuildSite> planned, IReadOnlyList<PlannedRoomAlternative> alternatives,
        EquatableArray<CoupledRoute> between, CoupledThermalRead? thermal, WorkResourceView resources, IReadOnlySet<string> pawns, double horizon, long deadline)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var bindings = new Dictionary<(string Task, string Pawn), ActivityTaskCandidate>();
        var operations = new List<ForecastCompositionOperation>(); var observed = new Dictionary<string, ForecastCompositionSupply>(StringComparer.Ordinal);
        var options = new List<ForecastActivityOption>(); var routes = new List<ForecastActivityRoute>();
        var roots = new List<ForecastCommitment>(); var lots = new List<ForecastMaterialLot>();
        var sites = new List<PlannedSiteOperations>(); var rooms = new List<PlannedRoomAlternative>();
        var truncated = false;
        if (resources.Claims.Truncated)
            return new([], [], [], [], [], [], [], [], bindings, ["planned_room_claims_truncated"], true);
        var byId = planned.Where(s => s.Room > 0 && s.Zone.Length == 0).ToDictionary(s => s.Id, StringComparer.Ordinal);
        bool Free(ConstructionActivityOption row) => pawns.Contains(row.Pawn) && !resources.Claims.Pawns.Contains(row.Pawn)
            && (row.Input.Length == 0 || !resources.Claims.Targets.Contains(row.Input));
        var capability = new Resource("ConstructionReady"); var cleared = new Resource("PlannedSiteCleared");
        var completed = new Resource("CompletedPhysicalSite");
        // Routes between planned sites (#3313): a site is worked at its first position, a route reaching its nearest one.
        bool Routed(string pawn, Cell to) => between.Any(r => r.Pawn == pawn && r.To == to && r.Ticks.IsKnown);
        ForecastActivityOption[] RoofOptions(PlannedBuildSite site, string id) => thermal is null ? [] : thermal.Workers
            .Where(w => w.RoofCellWorkTicks.IsKnown && pawns.Contains(w.Pawn) && !resources.Claims.Pawns.Contains(w.Pawn)
                && Routed(w.Pawn, site.Positions[0]))
            .Select(w => new ForecastActivityOption(id, w.Pawn, Known(w.RoofCellWorkTicks.Value * site.Positions.Count), Known(0.0))).ToArray();
        (Assessment Verdict, string Reason) Assess(PlannedBuildSite site)
        {
            if (site.Op == OpKind.RoofIn)
                return thermal is null || !thermal.Workers.Any(w => w.RoofCellWorkTicks.IsKnown) ? (Assessment.Unvalued, "roof_work_unknown")
                    : RoofOptions(site, "").Length == 0 ? (Assessment.Unvalued, "roof_unrouted") : (Assessment.Valued, "");
            if (site.Verdict == PlannedSiteVerdict.Illegal) return (Assessment.Infeasible, "illegal");
            if (site.Verdict == PlannedSiteVerdict.Unestimable) return (Assessment.Unvalued, "unestimable");
            if (site.Verdict != PlannedSiteVerdict.Feasible) return (Assessment.Unvalued, "unknown");
            var rows = site.Options.Where(Free).ToArray();
            if (!rows.Any(r => r.FinishTicks.IsKnown)) return (Assessment.Unvalued, "no_finish_option");
            if (site.Material.Count > 0 && !Deliveries(rows).Any()) return (Assessment.Unvalued, "no_delivery_option");
            foreach (var clearing in site.Clearing)
                if (clearing.Kind == PlannedClearingKind.Unspecified || !ClearingRows(clearing).Any())
                    return (Assessment.Unvalued, "clearing_unrouted");
            return (Assessment.Valued, "");
        }
        // One delivery per material source: every row of a source names the same single-resource demand.
        IEnumerable<ConstructionActivityOption[]> Deliveries(ConstructionActivityOption[] rows) => rows.Where(r => r.Count > 0)
            .GroupBy(r => r.Input).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Where(g => g.Select(r => (r.Resource, r.Count, r.Origin)).Distinct().Count() == 1)
            .Select(g => g.Where(r => r.Capacity > 0 && r.DeliveryTicks.IsKnown).GroupBy(r => r.Pawn).Select(p => p.First()).ToArray())
            .Where(g => g.Length > 0);
        IEnumerable<ConstructionActivityOption> ClearingRows(PlannedSiteClearing clearing) => clearing.Options.Where(Free)
            .Where(r => (clearing.Kind == PlannedClearingKind.Haul ? r.DeliveryTicks : r.FinishTicks).IsKnown)
            .Where(r => clearing.Kind != PlannedClearingKind.Deconstruct || r.Input.Length > 0).GroupBy(r => r.Pawn).Select(p => p.First());
        string RingKind(OpKind op) => op switch { OpKind.WallIn => "wall", OpKind.DoorIn => "door", _ => "vent" };
        var assessed = new Dictionary<string, (Assessment Verdict, string Reason)>(StringComparer.Ordinal);
        foreach (var room in alternatives.GroupBy(a => a.Room).OrderBy(g => g.Key))
        {
            var valued = new List<PlannedRoomAlternative>();
            foreach (var alternative in room)
            {
                var missing = alternative.Sites.Where(id => !byId.ContainsKey(id)).ToArray();
                if (missing.Length > 0) { findings.Add("planned_room_alternative_unvalued:" + alternative.Id + ":" + missing[0] + ":unknown"); continue; }
                var verdicts = alternative.Sites.Select(id => (Id: id, Result: assessed.TryGetValue(id, out var known) ? known : assessed[id] = Assess(byId[id]))).ToArray();
                if (verdicts.FirstOrDefault(v => v.Result.Verdict == Assessment.Infeasible) is { Id: not null } illegal)
                    findings.Add("planned_room_alternative_infeasible:" + alternative.Id + ":" + illegal.Id);
                else if (verdicts.FirstOrDefault(v => v.Result.Verdict == Assessment.Unvalued) is { Id: not null } unknown)
                    findings.Add("planned_room_alternative_unvalued:" + alternative.Id + ":" + unknown.Id + ":" + unknown.Result.Reason);
                else valued.Add(alternative);
            }
            if (valued.Count == 0) continue;
            var roomSites = valued.SelectMany(a => a.Sites).Distinct().Order(StringComparer.Ordinal).Select(id => byId[id])
                .OrderBy(s => s.Op is OpKind.VentIn or OpKind.RoofIn ? 1 : 0).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
            var roomOperations = new List<ForecastCompositionOperation>(); var roomObserved = new List<ForecastCompositionSupply>();
            var roomOptions = new List<ForecastActivityOption>(); var roomRoutes = new List<ForecastActivityRoute>();
            var roomLots = new List<ForecastMaterialLot>(); var roomSitesOps = new List<PlannedSiteOperations>();
            var roomBindings = new Dictionary<(string Task, string Pawn), ActivityTaskCandidate>();
            var finishes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var site in roomSites)
            {
                var finish = site.Id + "/finish";
                var ids = new List<string>();
                var requirements = new List<ForecastCompositionRequirement>();
                string siteLocation;
                if (site.Op == OpKind.RoofIn)
                {
                    // The roof has no native placement: it is worked at its own cells, cell by cell (RoofCellWorkTicks),
                    // reached from each pawn's start and from the room's other sites by the planned routes.
                    siteLocation = ForecastLocations.Of(site.Positions[0]);
                    roomOptions.AddRange(RoofOptions(site, finish));
                }
                else
                {
                    var rows = site.Options.Where(Free).ToArray();
                    siteLocation = ForecastLocations.Of(rows[0].Location);
                    if (site.Material.Count == 0 || rows.All(r => r.Count == 0))
                    {
                        var ready = site.Id + "/ready";
                        roomObserved.Add(new(ready, capability, Known(1), siteLocation, Known(0.0), Known(horizon))
                        { Kind = ForecastRequirementKind.Reusable, Provenance = "observed-planned-site:" + site.Id });
                        requirements.Add(new(finish + "/ready", capability, Known(1), siteLocation, ForecastRequirementKind.Reusable,
                            Known<EquatableArray<string>>([ready]), Known(0.0), Known(horizon)));
                    }
                    else
                    {
                        var readies = new List<string>();
                        foreach (var group in Deliveries(rows).Take(SourcesPerSite + 1).Select((g, i) => (Rows: g, Index: i)))
                        {
                            if (group.Index == SourcesPerSite) { findings.Add("planned_site_sources_truncated:" + site.Id); break; }
                            var first = group.Rows[0];
                            var delivery = site.Id + "/delivery/" + first.Input;
                            var ready = delivery + "/ready";
                            var source = "construction-source/" + first.Input;
                            var sourceLocation = ForecastLocations.Of(first.Origin);
                            roomObserved.Add(new(source, new(first.Resource), first.AvailableInput.TryGetValue(out var available)
                                && available >= 0 && available <= int.MaxValue && available == Math.Floor(available) ? Known((int)available) : default,
                                sourceLocation, Known(0.0), Known(horizon))
                            { Lot = first.Input, Provenance = "native-construction-source:" + site.Id + ":" + first.Input });
                            roomLots.Add(new(first.Input, sourceLocation, new(first.Resource), first.AvailableInput));
                            roomOperations.Add(new(new(delivery, sourceLocation, siteLocation, []) { ChoiceIdentity = site.Id + "/delivery" },
                                [new(delivery + "/material", new(first.Resource), Known(first.Count), sourceLocation,
                                    ForecastRequirementKind.Consumed, Known<EquatableArray<string>>([source]), Known(0.0), Known(horizon))],
                                [new(ready, capability, Known(1), siteLocation, Known(0.0), Known(horizon))
                                { Producer = delivery, Kind = ForecastRequirementKind.Reusable, Provenance = "native-planned-delivery:" + site.Id }]));
                            foreach (var row in group.Rows)
                            {
                                roomOptions.Add(new(delivery, row.Pawn, row.DeliveryTicks, Known(0.0)));
                                roomRoutes.Add(new(row.Pawn, "start", sourceLocation, row.ApproachTicks));
                            }
                            readies.Add(ready); ids.Add(delivery);
                        }
                        requirements.Add(new(finish + "/ready", capability, Known(1), siteLocation, ForecastRequirementKind.Reusable,
                            Known(EquatableArray.From(readies)), Known(0.0), Known(horizon)));
                    }
                    foreach (var row in rows.Where(r => r.FinishTicks.IsKnown).GroupBy(r => r.Pawn).Select(p => p.First()))
                    {
                        roomOptions.Add(new(finish, row.Pawn, row.FinishTicks, Known(0.0)));
                        roomRoutes.Add(new(row.Pawn, "start", siteLocation, row.SiteApproachTicks));
                    }
                    foreach (var clearing in site.Clearing.OrderBy(c => c.Id, StringComparer.Ordinal))
                    {
                        var clearingRows = ClearingRows(clearing).ToArray();
                        var haul = clearing.Kind == PlannedClearingKind.Haul;
                        var from = ForecastLocations.Of(haul ? clearingRows[0].Origin : clearingRows[0].Location);
                        var to = ForecastLocations.Of(clearingRows[0].Location);
                        var done = clearing.Id + "/cleared";
                        roomOperations.Add(new(new(clearing.Id, from, to, []), [],
                            [new(done, cleared, Known(1), siteLocation, Known(0.0), Known(horizon))
                            { Producer = clearing.Id, Kind = ForecastRequirementKind.Reusable, Provenance = "native-planned-clearing:" + site.Id }]));
                        foreach (var row in clearingRows)
                        {
                            roomOptions.Add(new(clearing.Id, row.Pawn, haul ? row.DeliveryTicks : row.FinishTicks, Known(0.0)));
                            roomRoutes.Add(new(row.Pawn, "start", from, haul ? row.ApproachTicks : row.SiteApproachTicks));
                            // The building under the footprint stands now: its removal binds before placement (#3557).
                            if (clearing.Kind == PlannedClearingKind.Deconstruct)
                                roomBindings[(clearing.Id, row.Pawn)] = new(clearing.Id, row.Pawn, [], PawnActivityKind.Work, row.Input, row.WorkGiver, "Deconstruct",
                                    0, deadline, row.Location) { SiteDefinition = row.Resource };
                        }
                        requirements.Add(new(finish + "/cleared/" + clearing.Id, cleared, Known(1), siteLocation, ForecastRequirementKind.Reusable,
                            Known<EquatableArray<string>>([done]), Known(0.0), Known(horizon)));
                        ids.Add(clearing.Id);
                    }
                }
                // A vent stands in the wall and a roof on the ring: they need its finished sites, in whichever stuff. A
                // completed planned site is supplied at its room, not its cells, so a dependent at other cells can link it.
                var under = site.Op == OpKind.VentIn ? new[] { OpKind.WallIn } : site.Op == OpKind.RoofIn ? [OpKind.WallIn, OpKind.DoorIn] : [];
                var roomLocation = "planned/room" + site.Room;
                foreach (var kind in under)
                {
                    var sources = roomSites.Where(s => s.Op == kind && finishes.ContainsKey(s.Id)).Select(s => "completed-site/" + finishes[s.Id]).ToArray();
                    if (sources.Length == 0) continue;
                    requirements.Add(new(finish + "/" + RingKind(kind), completed, Known(1), roomLocation, ForecastRequirementKind.Reusable,
                        Known(EquatableArray.From(sources)), Known(0.0), Known(horizon)));
                }
                var ring = site.Op is OpKind.WallIn or OpKind.DoorIn or OpKind.VentIn;
                roomOperations.Add(new(new(finish, siteLocation, siteLocation, [])
                    { ChoiceIdentity = ring ? "planned/room" + site.Room + "/" + RingKind(site.Op) + "/finish" : "" },
                    EquatableArray.From(requirements),
                    [new("completed-site/" + finish, completed, Known(1), roomLocation, Known(0.0), Known(horizon))
                    { Producer = finish, Lot = site.Id, Kind = ForecastRequirementKind.Reusable, Provenance = "planned-finish-site:" + site.Id }]));
                ids.Add(finish); finishes[site.Id] = finish;
                roomSitesOps.Add(new(site.Id, site.Room, ring ? site.Stuff : "", [.. ids], finish));
            }
            if (operations.Count + roomOperations.Count > OperationBudget)
            {
                findings.Add("planned_room_candidates_truncated:" + room.Key); truncated = true; continue;
            }
            operations.AddRange(roomOperations); options.AddRange(roomOptions); routes.AddRange(roomRoutes); lots.AddRange(roomLots);
            foreach (var supply in roomObserved) observed.TryAdd(supply.Id, supply);
            sites.AddRange(roomSitesOps); rooms.AddRange(valued);
            foreach (var binding in roomBindings) bindings[binding.Key] = binding.Value;
            foreach (var site in roomSitesOps)
                roots.Add(new(site.Site, [])
                {
                    ActivityCompletions = [site.Finish], ExclusiveOutcome = site.Site,
                    // Its relief marks the site's work as requested development (the thermal-relief gate); the unit itself
                    // is counted from the evaluated outcome, whichever root completes the site (#3334).
                    Outcome = new(site.Site, ForecastPlannedSite.DevelopmentNeed, site.Finish, Known(horizon), default)
                    {
                        ProgressUnits = Known(1.0),
                        Requirements = new([new(site.Site, ProductionNeedPriority.Maintenance, 1.0)], default, default),
                    },
                });
            foreach (var alternative in valued)
                roots.Add(new(alternative.Id, [])
                {
                    ActivityCompletions = EquatableArray.From(alternative.Sites.Select(id => finishes[id]).Order(StringComparer.Ordinal)),
                    ExclusiveOutcome = "planned/room" + alternative.Room,
                });
        }
        // A pawn chains a room's sites over the planned routes: from where one activity ends to where the next starts.
        if (operations.Count > 0)
            routes.AddRange(between.Where(r => pawns.Contains(r.Pawn) && !resources.Claims.Pawns.Contains(r.Pawn))
                .Select(r => new ForecastActivityRoute(r.Pawn, r.FromStart ? "start" : ForecastLocations.Of(r.From), ForecastLocations.Of(r.To), r.Ticks)));
        return new([.. operations], [.. observed.Values], [.. options], ForecastLocations.Merge(routes), [.. roots], [.. lots],
            [.. sites], [.. rooms], bindings, EquatableArray.From(findings), truncated);
    }
}
