using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal static class ConstructionMethodCandidates
{
    internal const string FlooringRelief = "flooring"; // one relief per floor site, so each laid floor is credited once
    internal const string RemovalRelief = "removal"; // one relief per standing removal designation, so each removal is credited once (#3557)

    /// <summary>
    /// The pawn activity job a standing removal op binds as Work of, by kind (#3612, #3613, #3615); null for a Haul, which binds
    /// as a Deliver of the stack (#3614), and for a kind no order works yet.
    /// </summary>
    internal static string? RemovalJob(PlannedClearingKind kind) =>
        kind == PlannedClearingKind.Deconstruct ? "Deconstruct"
        : kind == PlannedClearingKind.Uninstall ? "Uninstall"
        : kind == PlannedClearingKind.Cut ? "CutPlant"
        : kind == PlannedClearingKind.RemoveFloor ? "RemoveFloor"
        : kind == PlannedClearingKind.Mine ? "Mine"
        : null;
    /// <summary>A set kind's op (a Cut wave over plants, #3613; a RemoveFloor wave over cells, #3615; a Mine op, #3616) is bound over its members and needs them named.</summary>
    private static bool Bindable(PlannedSiteClearing op) =>
        op.Kind is PlannedClearingKind.Cut or PlannedClearingKind.Mine ? op.Targets.Count > 0
        : op.Kind != PlannedClearingKind.RemoveFloor || op.Cells.Count > 0;

    /// <summary>
    /// The work a pawn's removal row prices, or null when it cannot work it: a Haul is its carried delivery
    /// (#3614, #3625), from the pawn's approach to the stack; another kind is its finish, from the approach to the site.
    /// </summary>
    internal static (double Ticks, ProductiveSkillWork? SkillWork, double ApproachTicks)? Work(PlannedClearingKind kind, ConstructionActivityOption row) =>
        kind == PlannedClearingKind.Haul
            ? row.Delivery is ConstructionDelivery.Carried carried ? (carried.DeliveryTicks, null, carried.ApproachTicks) : null
            : row.Finish is ConstructionFinish.Finishes finishes ? (finishes.Ticks, finishes.SkillWork, row.SiteApproachTicks) : null;

    /// <summary>A pawn's option to do <paramref name="activity"/> in <paramref name="ticks"/> of labor, with no recovery labor.</summary>
    internal static ForecastActivityOption Option(string activity, string pawn, double ticks, ProductiveSkillWork? skillWork = null) =>
        new(activity, pawn, ticks, 0) { SkillWork = skillWork };

    /// <summary>A pawn's leg from its start to <paramref name="location"/>.</summary>
    internal static ForecastActivityRoute Start(string pawn, string location, double ticks) => new(pawn, "start", location, new RouteTicks.Reachable(ticks));

    /// <summary>
    /// A haul row the Deliver task can work: a stack and a carry. A stack larger than one carry is still one task, its
    /// order a Deliver of one carry at a time (CoordinatedActivityBinding clamps the step to Capacity) repeated over the
    /// remainder until the stack is delivered (#3625); the row's Carried delivery prices every trip.
    /// </summary>
    internal static bool Carriable(ConstructionActivityOption row) => row.Count > 0 && row.Capacity > 0;

    /// <summary>
    /// The task a standing removal row binds (#3557, #3612, #3614): a Haul is the pawn's Deliver of the stack (Count the
    /// stack, Destination the row's storage cell), as a support delivery binds; any other kind is Work of the kind's job, a
    /// set op (a Cut wave, #3613; a RemoveFloor wave, #3615) one Work set step over its named <paramref name="targets"/> or
    /// <paramref name="cells"/> (Count the set size). A roof-holder's Deconstruct (#3617) runs a Work/RemoveRoof cell set
    /// step over the op's <paramref name="cells"/> (the roofed cells it leaves unsupported) before the Deconstruct, in the
    /// same order: the same task, relief and output.
    /// </summary>
    internal static ActivityTaskCandidate RemovalBinding(string id, PlannedClearingKind kind, ConstructionActivityOption row, string target, long deadline,
        EquatableArray<string> targets = default, EquatableArray<Cell> cells = default) =>
        kind == PlannedClearingKind.Haul
            ? new CellActivityTaskCandidate(new ActivityTaskCandidate(id, row.Pawn, [], PawnActivityKind.Deliver, target, "", "", row.Count, deadline, row.Origin) { Resource = row.Resource, Source = row.Origin, Capacity = row.Capacity }, row.Location)
            : kind == PlannedClearingKind.Deconstruct
                ? new ActivityTaskCandidate(id, row.Pawn, [], PawnActivityKind.Work, target, row.WorkGiver, RemovalJob(kind)!, 0, deadline, row.Location)
                    { SiteDefinition = row.Definition, RoofCells = cells }
                : new ActivityTaskCandidate(id, row.Pawn, [], PawnActivityKind.Work, target, row.WorkGiver, RemovalJob(kind)!, targets.Count + cells.Count, deadline, row.Location)
                    { SiteDefinition = row.Definition, Targets = targets, Cells = cells };

    /// <summary>A site row whose stack alone covers the site's demand: a whole alternative.</summary>
    internal static bool Whole(ConstructionActivityOption row) => row.AvailableInput >= row.Count;

    /// <summary>
    /// The shares of a multi-stack source set (#3673), by stack id. Native lists every usable stack of a site's material
    /// (#3046); when none covers the demand alone, the set is the stacks nearest the site first (<paramref name="siteDistance"/>,
    /// ties: lowest id) until their sum covers it, every one taken whole but the largest (ties: lowest id), which supplies
    /// the remainder. Null when some stack covers the demand (each such stack is a whole alternative, <see cref="Whole"/>)
    /// or <paramref name="rows"/> name none; empty when the known stacks do not cover the demand.
    /// </summary>
    internal static IReadOnlyDictionary<string, int>? StackShares(IEnumerable<ConstructionActivityOption> rows, Func<ConstructionActivityOption, double> siteDistance)
    {
        var stacks = rows.Where(r => r.Count > 0 && r.Input.Length != 0).GroupBy(r => r.Input).Select(g => g.First()).ToArray();
        if (stacks.Length == 0 || stacks.Any(Whole)) return null;
        var shares = new Dictionary<string, int>(StringComparer.Ordinal);
        if (stacks.Select(r => (r.Resource, r.Count)).Distinct().Count() != 1
            || stacks.Any(r => r.AvailableInput < 1)) return shares;
        var need = stacks[0].Count;
        var set = new List<ConstructionActivityOption>();
        var held = 0L;
        foreach (var r in stacks.OrderBy(siteDistance).ThenBy(r => r.Input, StringComparer.Ordinal))
        {
            if (held >= need) break;
            set.Add(r); held += r.AvailableInput;
        }
        if (held < need) return shares;
        var remainder = set.OrderByDescending(r => r.AvailableInput).ThenBy(r => r.Input, StringComparer.Ordinal).First();
        foreach (var r in set) shares[r.Input] = r == remainder ? (int)(need - (held - r.AvailableInput)) : r.AvailableInput;
        return shares;
    }

    /// <summary>The straight-line distance between two cells.</summary>
    private static double Distance(Cell a, Cell b) => Math.Sqrt(((double)a.X - b.X) * (a.X - b.X) + ((double)a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>
    /// Remote pickup (#2517, #3046, <see cref="RemotePickup"/>): a whole-stack delivery whose stack is remote takes a full
    /// carry load instead of the site's need, the surplus dropped beside the site for its next delivery, and collects the
    /// site's other stacks worth the detour. A local delivery, or one no storage accepts, takes the need alone.
    /// </summary>
    internal static (bool FullLoad, EquatableArray<string> Collect) RemoteLoad(ConstructionActivityOption row, IEnumerable<ConstructionActivityOption> stacks)
    {
        if (row.Storage is not StackStorage.Accepted accepted || row.Capacity <= 0) return (false, default);
        var site = Distance(row.Origin, row.Location);
        if (!RemotePickup.IsRemote(site, accepted.Distance)) return (false, default);
        var candidates = stacks.Where(s => s.Input.Length != 0 && s.Input != row.Input && s.Resource == row.Resource)
            .GroupBy(s => s.Input).Select(g => g.First()).OrderBy(s => s.Input, StringComparer.Ordinal)
            .Select(s => (s.Input, Distance(row.Origin, s.Origin), (double)s.AvailableInput));
        return (true, EquatableArray.From(RemotePickup.Collect(site, row.Capacity, row.AvailableInput, candidates)));
    }

    /// <summary>The placed-site candidates one comparison generates, in site id order; an urgent build's review compares all of its own (#3537).</summary>
    internal const int SiteBound = 16;

    internal static CoordinatedMethodCandidates Generate(ConstructionActivityRead read, long deadline, WorkResourceView resources, IReadOnlySet<string> floors,
        int siteBound = SiteBound)
    {
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        var tick = read.Context.Tick;
        if (deadline <= tick || resources.Claims.Truncated)
            return new(null, [], bindings, resources.Claims.Truncated);
        var horizon = deadline - tick;
        // A site's candidates: one per whole-stack source, or one for its multi-stack set (#3673), whose every stack is
        // delivered. A whole-stack delivery may collect the site's other stacks (RemoteLoad).
        var groups = new List<(ConstructionActivityOption[][] Stacks, IReadOnlyDictionary<string, int>? Shares, ConstructionActivityOption[] Site)>();
        foreach (var site in read.Options.Where(o => !resources.Claims.Pawns.Contains(o.Pawn) && !resources.Claims.Targets.Contains(o.Site)
            && (o.Input.Length == 0 || !resources.Claims.Targets.Contains(o.Input))).GroupBy(o => o.Site).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rows = site.ToArray();
            var stacks = site.GroupBy(o => o.Input).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
            var shares = StackShares(rows, r => Distance(r.Origin, r.Location));
            if (shares is null) groups.AddRange(stacks.Where(s => Whole(s[0])).Select(s => (new[] { s }, (IReadOnlyDictionary<string, int>?)null, rows)));
            else if (shares.Count > 0 && shares.Keys.All(k => stacks.Any(s => s[0].Input == k)))
                groups.Add((stacks.Where(s => shares.ContainsKey(s[0].Input)).ToArray(), shares, rows));
        }
        var truncated = groups.Count > siteBound;
        var activities = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>(); var outcomes = new List<ForecastActivityOutcome>();
        var workers = new HashSet<string>(StringComparer.Ordinal);
        var operations = new List<ForecastCompositionOperation>();
        var observed = new Dictionary<string, ForecastCompositionSupply>(StringComparer.Ordinal);
        foreach (var (stacks, shares, siteRows) in groups.Take(siteBound))
        {
            var id = "site-" + outcomes.Count;
            var finish = id + "-finish";
            var all = stacks.SelectMany(s => s).ToArray();
            var supplied = all.All(o => o.Count == 0);
            // Every option describes the same single-resource site demand; unobserved/mixed demand cannot be guessed.
            if (!supplied && stacks.Any(s => s.Where(o => o.Count > 0).Select(o => (o.Resource, o.Count, o.Origin)).Distinct().Count() != 1)) continue;
            var first = all[0];
            var siteLocation = ForecastLocations.Of(first.Location);
            var capability = new Resource("ConstructionReady");
            // One delivery per stack: a whole alternative's single stack carries the demand, a set's stacks their shares.
            var deliveries = supplied ? [] : stacks.Select((s, k) => (Rows: s, Id: shares is null ? id + "-delivery" : id + "-delivery-" + k,
                Units: shares?[s[0].Input] ?? s[0].Count)).ToArray();
            var readies = new List<string>();
            foreach (var (rows, delivery, units) in deliveries)
            {
                var stack = rows[0];
                var sourceLocation = ForecastLocations.Of(stack.Origin);
                var ready = "construction-ready/" + delivery;
                activities.Add(new(delivery, sourceLocation, siteLocation, []) { Inputs = [new(new(stack.Resource), units) { Lot = stack.Input }] });
                var source = "construction-source/" + stack.Input;
                observed[source] = new(source, new(stack.Resource), stack.AvailableInput, sourceLocation, 0, horizon)
                { Lot = stack.Input, Provenance = "native-construction-source:" + stack.Site + ":" + stack.Input };
                operations.Add(new(activities[^1] with { Inputs = [], ChoiceIdentity = "construction-delivery/" + stack.Site + (shares is null ? "" : "/" + stack.Input) },
                    [new(delivery + "/material", new(stack.Resource), units, sourceLocation,
                        ForecastRequirementKind.Consumed, [source], 0, horizon)],
                    [new(ready, capability, 1, siteLocation, 0, horizon)
                    { Producer = delivery, Kind = ForecastRequirementKind.Reusable, Provenance = "native-site-delivery:" + stack.Site }]));
                readies.Add(ready);
                foreach (var row in rows.GroupBy(o => o.Pawn).Select(g => g.First()).Where(r => r.Count > 0 && r.Capacity > 0))
                {
                    if (row.Delivery is not ConstructionDelivery.Carried carried) continue;
                    workers.Add(row.Pawn);
                    options.Add(Option(delivery, row.Pawn, carried.DeliveryTicks));
                    routes.Add(Start(row.Pawn, sourceLocation, carried.ApproachTicks));
                    // A set's next stack is fetched from the site: native reads that leg for each of its stacks.
                    if (shares is not null)
                        foreach (var back in read.Routes.Where(r => r.Pawn == row.Pawn && r.From == row.Location && r.To == row.Origin && !r.FromStart))
                            routes.Add(new(row.Pawn, siteLocation, sourceLocation, back.Ticks));
                    var (fullLoad, collect) = shares is null ? RemoteLoad(row, siteRows) : (false, default);
                    bindings[(delivery, row.Pawn)] = new(delivery, row.Pawn, [], PawnActivityKind.Supply,
                        row.Input, "", "", units, deadline, row.Location)
                    { Resource = row.Resource, Source = row.Origin, Container = row.Site, SiteDefinition = row.Definition, Capacity = row.Capacity,
                        FullLoad = fullLoad, Collect = collect };
                }
            }
            var prerequisites = deliveries.Select(d => d.Id).ToArray();
            activities.Add(new(finish, siteLocation, siteLocation, prerequisites));
            if (supplied)
            {
                var ready = "construction-ready/" + id;
                observed[ready] = new(ready, capability, 1, siteLocation, 0, horizon)
                { Kind = ForecastRequirementKind.Reusable, Provenance = "observed-site:" + first.Site };
                readies.Add(ready);
            }
            operations.Add(new(activities[^1] with { Prerequisites = [], ChoiceIdentity = "construction-finish/" + first.Site },
                EquatableArray.From(readies.Select((ready, k) => new ForecastCompositionRequirement(finish + "/ready" + (readies.Count == 1 ? "" : "/" + k), capability, 1,
                    siteLocation, ForecastRequirementKind.Reusable, [ready], 0, horizon))), []));
            foreach (var row in all.GroupBy(o => o.Pawn).Select(g => g.First()))
            {
                workers.Add(row.Pawn);
                if (row.Finish is ConstructionFinish.Finishes finishes)
                {
                    options.Add(Option(finish, row.Pawn, finishes.Ticks, finishes.SkillWork) with { ServicePosition = row.ServicePosition });
                    routes.Add(Start(row.Pawn, siteLocation, row.SiteApproachTicks));
                    bindings[(finish, row.Pawn)] = new(finish, row.Pawn, [.. prerequisites], PawnActivityKind.Work,
                        row.Site, row.WorkGiver, "FinishFrame", 0, deadline, row.Location)
                    { SiteDefinition = row.Definition, CompletionMustBeObserved = true };
                }
            }
            // A floor is a declared service investment (#3047): it relieves kitchen cleanliness and room beauty, so the selector may spend labor on it.
            outcomes.Add(new(finish, "construction-method", finish)
            {
                Requirements = floors.Contains(first.Definition)
                    ? new([new(FlooringRelief + "/" + first.Site, ProductionNeedPriority.Maintenance, 1.0)], 0, 0) : null
            });
        }
        // A standing removal designation (#3557, #3612, #3613, #3614, #3615): the room reconcile stood a Deconstruct over an
        // interior ruin or a stale piece, an Uninstall over a piece it packs, a Haul over a stack it moves out (on an ordinary
        // stack a haul-out record, no designation, #3625), CutPlant over a
        // wave of plants or RemoveFloor over a wave of wrong-floor cells, and under voluntary control only an order works it.
        // Each building is the pawn's Work task of the op's kind on it, each stack the pawn's Deliver of it to the row's
        // storage cell, and each Cut or RemoveFloor wave one Work set step over its members. Its requested relief, like a
        // floor's, exempts it from the thermal gate; its room's service day prices the work. A kind with no job binds nothing.
        var removals = read.Removals.Where(op => (op.Kind == PlannedClearingKind.Haul || RemovalJob(op.Kind) != null) && Bindable(op) && !resources.Claims.Targets.Contains(op.Id))
            .Select(op => (op.Id, op.Kind, op.Targets, op.Cells, Rows: op.Options.Where(o => !resources.Claims.Pawns.Contains(o.Pawn) && (op.Kind != PlannedClearingKind.Haul || Carriable(o)))
                .SelectMany(o => Work(op.Kind, o) is { } work ? new[] { (Row: o, Work: work) } : [])
                .GroupBy(o => o.Row.Pawn).Select(g => g.First()).ToArray()))
            .Where(op => op.Rows.Length > 0).OrderBy(op => op.Id, StringComparer.Ordinal).Take(17).ToArray();
        foreach (var op in removals.Take(16))
        {
            var id = "removal-" + outcomes.Count;
            var first = op.Rows[0].Row;
            var haul = op.Kind == PlannedClearingKind.Haul;
            // A haul is worked from the stack's cell; a building's removal at the building.
            var location = ForecastLocations.Of(haul ? first.Origin : first.Location);
            activities.Add(new(id, location, location, []));
            // The building stands: like a supplied site's finish, the op needs only its observed readiness to compose.
            var ready = "removal-ready/" + id;
            var capability = new Resource("ConstructionReady");
            observed[ready] = new(ready, capability, 1, location, 0, horizon)
            { Kind = ForecastRequirementKind.Reusable, Provenance = "observed-designation:" + op.Id };
            operations.Add(new(activities[^1] with { ChoiceIdentity = "construction-removal/" + op.Id },
                [new(id + "/ready", capability, 1, location, ForecastRequirementKind.Reusable, [ready], 0, horizon)], []));
            foreach (var (row, work) in op.Rows)
            {
                workers.Add(row.Pawn);
                options.Add(Option(id, row.Pawn, work.Ticks, work.SkillWork) with { ServicePosition = row.ServicePosition });
                routes.Add(Start(row.Pawn, location, work.ApproachTicks));
                bindings[(id, row.Pawn)] = RemovalBinding(id, op.Kind, row, op.Id, deadline, op.Targets, op.Cells);
            }
            outcomes.Add(new(id, "construction-method", id)
            {
                Requirements = new([new(RemovalRelief + "/" + op.Id, ProductionNeedPriority.Maintenance, 1.0)], 0, 0),
            });
        }
        var capacities = workers.Order(StringComparer.Ordinal).Select(pawn => new ForecastPawnCapacity(pawn, "start",
            resources.Capacity.TryGetValue(pawn, out var capacity) && capacity.Capacity.Windows is ForecastCapacityWindows.Read(var windows)
                ? new ForecastCapacityWindows.Read(EquatableArray.From(windows.Select(w => w with
                {
                    Start = Math.Max(0, w.Start - (tick - resources.Evidence.Tick.Value)),
                    End = w.End - (tick - resources.Evidence.Tick.Value)
                }).Where(w => w.End > w.Start))) : new ForecastCapacityWindows.Unread(), 1.0)).ToArray();
        return new(activities.Count == 0 ? null : new()
        {
            Activities = activities.ToArray(),
            Options = options.ToArray(),
            Routes = ForecastLocations.Merge(routes),
            Pawns = capacities,
            Deadline = horizon
        }, outcomes.ToArray(), bindings,
            truncated || removals.Length > 16)
        { Operations = EquatableArray.From(operations), Observed = EquatableArray.From(observed.Values) };
    }
}
