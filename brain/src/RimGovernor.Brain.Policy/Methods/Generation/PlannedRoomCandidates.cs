using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>
/// The composition operations one planned site was generated as, and its finish. WorkGroups (#3744, #3796) are its owed
/// work: its finish, then its deliveries (the alternative sources as one group, or each stack of its set as its own),
/// then each clearing; the owed work is the sum over groups of the least work of a group's options.
/// </summary>
internal sealed record PlannedSiteOperations(string Site, int Room, string Stuff, string[] Operations, string Finish)
{
    public string[][] WorkGroups { get; init; } = [];
}

internal sealed record PlannedRoomCandidatesResult(
    ForecastCompositionOperation[] Operations, ForecastCompositionSupply[] Observed, ForecastActivityOption[] Options,
    ForecastActivityRoute[] Routes, ForecastCommitment[] Roots, ForecastMaterialLot[] Lots,
    PlannedSiteOperations[] Sites, PlannedRoomAlternative[] Rooms,
    IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> Bindings, EquatableArray<string> Findings, bool Truncated);

/// <summary>
/// Planned rooms as construction activities (#3276). Each planned site of a valued alternative is its own comparison
/// root, named by the site id and shared by the room's alternatives: its delivery, its clearing prerequisites and its
/// finish, in the placed-site shape (ConstructionMethodCandidates). Completed sites are physical evidence;
/// their room's service supplies its value. An alternative root requires every site of its
/// alternative and is not exempt from the thermal-relief gate. Nothing binds
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
        EquatableArray<CoupledRoute> between, CoupledThermalRead? thermal, WorkResourceView resources, IReadOnlySet<string> pawns, double horizon, long deadline, bool schedulable = true)
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
        bool Routed(string pawn, Cell to) => between.Any(r => r.Pawn == pawn && r.To == to && r.Ticks is RouteTicks.Reachable);
        ForecastActivityOption[] RoofOptions(PlannedBuildSite site, string id) => thermal is null ? [] : thermal.Workers
            .Where(w => pawns.Contains(w.Pawn) && !resources.Claims.Pawns.Contains(w.Pawn) && Routed(w.Pawn, site.Positions[0]))
            .Select(w => new ForecastActivityOption(id, w.Pawn, w.RoofCellWorkTicks * site.Positions.Count, 0)
            { SkillWork = w.SkillWork is { } learning
                ? learning with { WorkAmount = learning.WorkAmount * site.Positions.Count, WorkQuantum = learning.WorkAmount } : null }).ToArray();
        (Assessment Verdict, string Reason) Assess(PlannedBuildSite site)
        {
            if (site.Op == OpKind.RoofIn)
                return thermal is null ? (Assessment.Unvalued, "roof_work_unread") : thermal.Workers.Count == 0 ? (Assessment.Unvalued, "no_roof_worker")
                    : RoofOptions(site, "").Length == 0 ? (Assessment.Unvalued, "roof_unrouted") : (Assessment.Valued, "");
            if (site.Answer is not { } answer) return (Assessment.Unvalued, "unanswered");
            if (answer.Verdict == PlannedSiteVerdict.Illegal) return (Assessment.Infeasible, "illegal");
            if (answer.Verdict == PlannedSiteVerdict.Unestimable) return (Assessment.Unvalued, "unestimable");
            if (answer.Verdict == PlannedSiteVerdict.Unresolved) return (Assessment.Unvalued, "unresolved");
            var rows = site.Options.Where(Free).ToArray();
            if (!rows.Any(r => r.Finish is ConstructionFinish.Finishes)) return (Assessment.Unvalued, "no_finish_option");
            if (site.Material.Count > 0 && Sourcing(site, rows).Groups.Length == 0) return (Assessment.Unvalued, "no_delivery_option");
            foreach (var clearing in site.Clearing)
                if (ClearingRows(clearing).Length == 0)
                    return (Assessment.Unvalued, "clearing_unrouted");
            return (Assessment.Valued, "");
        }
        // One delivery per material source: every row of a source names the same single-resource demand.
        IEnumerable<ConstructionActivityOption[]> Deliveries(ConstructionActivityOption[] rows) => rows.Where(r => r.Count > 0)
            .GroupBy(r => r.Input).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Where(g => g.Select(r => (r.Resource, r.Count, r.Origin)).Distinct().Count() == 1)
            .Select(g => g.Where(r => r.Capacity > 0 && r.Delivery is ConstructionDelivery.Carried).GroupBy(r => r.Pawn).Select(p => p.First()).ToArray())
            .Where(g => g.Length > 0);
        // A site's deliveries (#3673): its whole-stack sources, alternatives (Shares null), or its multi-stack set, every
        // stack delivered for its share, when each of them has a deliverable row and the set fits SourcesPerSite (else none).
        (ConstructionActivityOption[][] Groups, IReadOnlyDictionary<string, int>? Shares) Sourcing(PlannedBuildSite site, ConstructionActivityOption[] rows)
        {
            var groups = Deliveries(rows).ToArray();
            // Native lists every usable stack (#3046); a set is the stacks nearest the site's pieces.
            var shares = ConstructionMethodCandidates.StackShares(rows, r => site.Positions.Count == 0 ? 0 : site.Positions.Min(p => ((double)p.X - r.Origin.X) * (p.X - r.Origin.X) + ((double)p.Z - r.Origin.Z) * (p.Z - r.Origin.Z)));
            if (shares is null) return (groups.Where(g => ConstructionMethodCandidates.Whole(g[0])).ToArray(), null);
            var set = groups.Where(g => shares.ContainsKey(g[0].Input)).ToArray();
            return shares.Count > 0 && shares.Count <= SourcesPerSite && set.Length == shares.Count ? (set, shares) : ([], shares);
        }
        (ConstructionActivityOption Row, (double Ticks, ProductiveSkillWork? SkillWork, double ApproachTicks) Work)[] ClearingRows(PlannedSiteClearing clearing) =>
            clearing.Options.Where(Free).Where(r => clearing.Kind != PlannedClearingKind.Deconstruct || r.Input.Length > 0)
            .SelectMany(r => ConstructionMethodCandidates.Work(clearing.Kind, r) is { } work ? new[] { (Row: r, Work: work) } : [])
            .GroupBy(r => r.Row.Pawn).Select(p => p.First()).ToArray();
        string RingKind(OpKind op) => op switch
        {
            OpKind.WallIn => "wall",
            OpKind.DoorIn => "door",
            OpKind.WallUp => "wallup",
            OpKind.VentIn => "vent",
            OpKind.None or OpKind.RoofOff or OpKind.DoorOut or OpKind.WallOut or OpKind.Pack
                or OpKind.FurnitureOut or OpKind.PackInUse or OpKind.FloorOut or OpKind.FloorIn
                or OpKind.Install or OpKind.Build or OpKind.Claim or OpKind.Cut or OpKind.HaulOut
                or OpKind.RoofIn => throw new DomainException($"planned room: operation {op} is not a ring kind"),
            var invalid => throw new DomainException($"planned room: invalid operation {invalid}"),
        };
        var assessed = new Dictionary<string, (Assessment Verdict, string Reason)>(StringComparer.Ordinal);
        foreach (var room in alternatives.GroupBy(a => a.Room).OrderBy(g => g.Key))
        {
            var valued = new List<PlannedRoomAlternative>();
            foreach (var whole in room)
            {
                // A Build site whose piece no builder can work (PlannedBuildSite.Blocked, #3838) is left out of its
                // alternative, which is valued with the pieces that can be built; one left the same as another is dropped.
                var blocked = whole.Sites.Where(id => byId.TryGetValue(id, out var s) && s.Blocked).ToArray();
                foreach (var id in blocked) findings.Add("planned_room_site_blocked:" + whole.Id + ":" + id);
                var alternative = blocked.Length == 0 ? whole : whole with { Sites = EquatableArray.From(whole.Sites.Except(blocked, StringComparer.Ordinal)) };
                if (blocked.Length > 0 && (alternative.Sites.Count == 0
                    || room.Any(a => a.Id != whole.Id && a.Sites.Order(StringComparer.Ordinal).SequenceEqual(alternative.Sites.Order(StringComparer.Ordinal), StringComparer.Ordinal))))
                    continue;
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
                var hauls = new List<string[]>();
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
                        roomObserved.Add(new(ready, capability, 1, siteLocation, 0, horizon)
                        { Kind = ForecastRequirementKind.Reusable, Provenance = "observed-planned-site:" + site.Id });
                        requirements.Add(new(finish + "/ready", capability, 1, siteLocation, ForecastRequirementKind.Reusable,
                            [ready], 0, horizon));
                    }
                    else
                    {
                        var readies = new List<string>();
                        var (sourced, shares) = Sourcing(site, rows);
                        foreach (var group in sourced.Take(SourcesPerSite + 1).Select((g, i) => (Rows: g, Index: i)))
                        {
                            if (group.Index == SourcesPerSite) { findings.Add("planned_site_sources_truncated:" + site.Id); break; }
                            var first = group.Rows[0];
                            var units = shares?[first.Input] ?? first.Count;
                            var delivery = site.Id + "/delivery/" + first.Input;
                            var ready = delivery + "/ready";
                            var source = "construction-source/" + first.Input;
                            var sourceLocation = ForecastLocations.Of(first.Origin);
                            roomObserved.Add(new(source, new(first.Resource), first.AvailableInput, sourceLocation, 0, horizon)
                            { Lot = first.Input, Provenance = "native-construction-source:" + site.Id + ":" + first.Input });
                            roomLots.Add(new(first.Input, sourceLocation, new(first.Resource), (double)first.AvailableInput));
                            // A whole-stack source is one of the site's alternative deliveries; a set's stacks are each delivered.
                            roomOperations.Add(new(new(delivery, sourceLocation, siteLocation, []) { ChoiceIdentity = shares is null ? site.Id + "/delivery" : delivery },
                                [new(delivery + "/material", new(first.Resource), units, sourceLocation,
                                    ForecastRequirementKind.Consumed, [source], 0, horizon)],
                                [new(ready, capability, 1, siteLocation, 0, horizon)
                                { Producer = delivery, Kind = ForecastRequirementKind.Reusable, Provenance = "native-planned-delivery:" + site.Id }]));
                            foreach (var row in group.Rows)
                            {
                                if (row.Delivery is not ConstructionDelivery.Carried carried) continue;
                                roomOptions.Add(ConstructionMethodCandidates.Option(delivery, row.Pawn, carried.DeliveryTicks));
                                roomRoutes.Add(ConstructionMethodCandidates.Start(row.Pawn, sourceLocation, carried.ApproachTicks));
                            }
                            readies.Add(ready); ids.Add(delivery);
                        }
                        var delivered = ids.Where(id => id.StartsWith(site.Id + "/delivery/", StringComparison.Ordinal)).ToArray();
                        if (delivered.Length > 0) hauls.AddRange(shares is null ? [delivered] : delivered.Select(id => new[] { id }));
                        if (shares is null)
                            requirements.Add(new(finish + "/ready", capability, 1, siteLocation, ForecastRequirementKind.Reusable,
                                EquatableArray.From(readies), 0, horizon));
                        else requirements.AddRange(readies.Select((ready, k) => new ForecastCompositionRequirement(finish + "/ready/" + k, capability, 1,
                            siteLocation, ForecastRequirementKind.Reusable, [ready], 0, horizon)));
                    }
                    foreach (var row in rows.Where(r => r.Finish is ConstructionFinish.Finishes).GroupBy(r => r.Pawn).Select(p => p.First()))
                    {
                        if (row.Finish is not ConstructionFinish.Finishes work) continue;
                        roomOptions.Add(ConstructionMethodCandidates.Option(finish, row.Pawn, work.Ticks, work.SkillWork) with { ServicePosition = row.ServicePosition });
                        roomRoutes.Add(ConstructionMethodCandidates.Start(row.Pawn, siteLocation, row.SiteApproachTicks));
                    }
                    foreach (var clearing in site.Clearing.OrderBy(c => c.Id, StringComparer.Ordinal))
                    {
                        var clearingRows = ClearingRows(clearing);
                        var haul = clearing.Kind == PlannedClearingKind.Haul;
                        var from = ForecastLocations.Of(haul ? clearingRows[0].Row.Origin : clearingRows[0].Row.Location);
                        var to = ForecastLocations.Of(clearingRows[0].Row.Location);
                        var done = clearing.Id + "/cleared";
                        roomOperations.Add(new(new(clearing.Id, from, to, []), [],
                            [new(done, cleared, 1, siteLocation, 0, horizon)
                            { Producer = clearing.Id, Kind = ForecastRequirementKind.Reusable, Provenance = "native-planned-clearing:" + site.Id }]));
                        foreach (var (row, work) in clearingRows)
                        {
                            roomOptions.Add(ConstructionMethodCandidates.Option(clearing.Id, row.Pawn, work.Ticks, work.SkillWork) with { ServicePosition = row.ServicePosition });
                            roomRoutes.Add(ConstructionMethodCandidates.Start(row.Pawn, from, work.ApproachTicks));
                            // The building, stack or plants under the footprint stand now: their removal binds before placement, a
                            // building as Work/Deconstruct on it (#3557; a roof-holder's order removes the roof over the op's
                            // Cells first, #3617), a stack as a Deliver of it to the row's storage cell (#3614; one carry at a
                            // time over a stack larger than one, #3625), the plants as one Work/CutPlant set step
                            // over the op's named Targets (#3613) and the natural rock as one Work/Mine set step over its
                            // named rocks (#3616).
                            if (clearing.Kind == PlannedClearingKind.Deconstruct || haul && ConstructionMethodCandidates.Carriable(row))
                                roomBindings[(clearing.Id, row.Pawn)] = ConstructionMethodCandidates.RemovalBinding(clearing.Id, clearing.Kind, row with { Definition = row.Resource }, row.Input, deadline, cells: clearing.Cells);
                            else if (clearing.Kind is PlannedClearingKind.Cut or PlannedClearingKind.Mine && clearing.Targets.Count > 0)
                                roomBindings[(clearing.Id, row.Pawn)] = ConstructionMethodCandidates.RemovalBinding(clearing.Id, clearing.Kind, row, clearing.Id, deadline, clearing.Targets);
                        }
                        requirements.Add(new(finish + "/cleared/" + clearing.Id, cleared, 1, siteLocation, ForecastRequirementKind.Reusable,
                            [done], 0, horizon));
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
                    requirements.Add(new(finish + "/" + RingKind(kind), completed, 1, roomLocation, ForecastRequirementKind.Reusable,
                        EquatableArray.From(sources), 0, horizon));
                }
                var ring = site.Op is OpKind.WallIn or OpKind.DoorIn or OpKind.VentIn or OpKind.WallUp;
                roomOperations.Add(new(new(finish, siteLocation, siteLocation, [])
                    { ChoiceIdentity = ring ? "planned/room" + site.Room + "/" + RingKind(site.Op) + "/finish" : "" },
                    EquatableArray.From(requirements),
                    [new("completed-site/" + finish, completed, 1, roomLocation, 0, horizon)
                    { Producer = finish, Lot = site.Id, Kind = ForecastRequirementKind.Reusable, Provenance = "planned-finish-site:" + site.Id }]));
                ids.Add(finish); finishes[site.Id] = finish;
                roomSitesOps.Add(new(site.Id, site.Room, ring ? site.Stuff : "", [.. ids], finish)
                    { WorkGroups = [[finish], .. hauls, .. site.Clearing.Where(c => ids.Contains(c.Id)).Select(c => new[] { c.Id })] });
            }
            if (schedulable && operations.Count + roomOperations.Count > OperationBudget)
            {
                findings.Add("planned_room_candidates_truncated:" + room.Key); truncated = true; continue;
            }
            operations.AddRange(roomOperations); options.AddRange(roomOptions); routes.AddRange(roomRoutes); lots.AddRange(roomLots);
            foreach (var supply in roomObserved) observed.TryAdd(supply.Id, supply);
            sites.AddRange(roomSitesOps); rooms.AddRange(valued);
            foreach (var binding in roomBindings) bindings[binding.Key] = binding.Value;
            foreach (var site in schedulable ? roomSitesOps : [])
                roots.Add(new(site.Site, [])
                {
                    ActivityCompletions = [site.Finish], ExclusiveOutcome = site.Site,
                });
            foreach (var alternative in schedulable ? valued : [])
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
