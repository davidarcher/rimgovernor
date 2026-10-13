using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Journal = RimGovernor.Brain.Store.Store;
using ReviewRecord = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>route/stockpile</c> (Go <c>route/route.go</c>): the MaintainRoutes vertical. A stockpile zone sits in a roofed
/// room walled on every side with no door. The native reachability census (the game's own pathing per colonist, never
/// a flood fill) must read it unreachable and list breach walls; the service must latch the facility, admit exactly
/// one door on a listed breach wall, the colonists build it, and the next measured census must read the stockpile
/// reachable with a measured path and release the latch. An independent native read then confirms the reachability
/// and that observed traffic samples exist (actual travel evidence), reporting the door cell's own. Uses the private
/// disposable <c>test/routes_prepare</c> fixture.
/// </summary>
internal static class RouteStockpile
{
    private const string Prefix = "route-accept";

    internal static AcceptanceCase Case() => new()
    {
        Name = "route/stockpile",
        Scope = "Native MaintainRoutes vertical: a walled-in stockpile the native reachability census reads unreachable "
            + "drives the live Go rounder/planner to admit exactly one door on a listed breach wall; the colonists "
            + "build it and the measured census, not the receipt, releases the latch, confirmed by an independent native read "
            + "with observed traffic samples.",
        Start = new FixtureStart { Op = "test/routes_prepare", On = new LabStart() },

        // The census paths between the colonists and a staged room; the wild map is unobserved.
        QuietWorld = true,
        Service = true,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static CellRect RectOf(JsonNode? value)
    {
        var rect = value.AsMap();
        return new CellRect((int)rect?["minX"].AsNumber()!, (int)rect["minZ"].AsNumber(), (int)rect["maxX"].AsNumber(), (int)rect["maxZ"].AsNumber());
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        var prepared = s.Prepared ?? [];
        ServiceProcess? service = null;
        var facility = "";
        var door = new Cell(0, 0);

        try
        {
            facility = prepared["facility"].AsString();
            var room = RectOf(prepared["room"]);
            var interior = RectOf(prepared["interior"]);
            var colonists = (int)prepared["colonists"].AsNumber();
            var routes = PolicyFunctions.DefaultRoutesPolicy();

            // Before: the typed routes census must list the stockpile with every mobile colonist's reachability false
            // and at least one breach wall on the room's border -- the exact facts the review latches on.
            var before = await ReadRoutesAsync(h, identity, "routes-before", facility, cancellationToken).ConfigureAwait(false);
            report["routes_before"] = before.Evidence();
            if (before.Kind != "ROUTE_FACILITY_KIND_STOCKPILE" || before.Travel.Count != colonists || before.Reachable() != 0)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"routes-before: {facility} reads kind {before.Kind}, {before.Travel.Count} of {colonists} travel rows, {before.Reachable()} reachable"));
            }

            if (before.Breaches.Count == 0)
            {
                throw new AcceptanceException($"routes-before: {facility} lists no breach wall");
            }

            foreach (var b in before.Breaches)
            {
                if (!room.Contains(b.Cell) || interior.Contains(b.Cell) || b.Edifice != "Wall")
                {
                    throw new AcceptanceException($"routes-before: breach {b} is not a wall of the fixture room {room}");
                }
            }

            // "work" rides along because every building method's builder check requires the colony's work priorities
            // to match the controller's own assignment, which only the work family applies.
            service = await s.LaunchAsync(new ServiceLaunch { Families = ["routes", "work"] }, cancellationToken).ConfigureAwait(false);
            var token = await service.SessionTokenAsync(cancellationToken).ConfigureAwait(false);
            var attached = await service.WaitAttachedAsync(identity, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            report["service_state_attached"] = attached.DeepClone();
            var rootPlanId = await service.ResumeAsync(Prefix, identity, token, report, cancellationToken).ConfigureAwait(false);
            report["root_plan"] = rootPlanId;
            var keepAlive = new AuthorityKeepAlive(service, Prefix, identity, token);
            var stopKeepAlive = keepAlive.Start(cancellationToken);
            try
            {
                using var journal = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), cancellationToken).ConfigureAwait(false);
                var (review, diagnostics) = await ServiceJournal.WaitRoundsAsync(service, journal, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
                report["diagnostic_post_acquire"] = new JsonArray([.. diagnostics.Select(d => (JsonNode?)d.DeepClone())]);
                report["rounds_review_first"] = review.Revision;

                // The review must latch the facility and bind MaintainRoutes.
                ReviewRecord latched;
                try
                {
                    latched = await ServiceJournal.BoundedAsync(
                        TimeSpan.FromMinutes(3),
                        ct => WaitLatchAsync(journal, service, facility, ct),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    throw new AcceptanceException($"routes latch: {e.Message}", e);
                }

                report["latched_review_revision"] = latched.Revision;

                // The routes method: one door of the policy's material on a breach wall of the room, never inside it.
                // Incidental cancellations renew the method; the measured census releases the facility once reachable.
                string? previous = null;
                var plans = new List<string>();
                var released = false;
                for (var renewals = 0; !released && plans.Count < 4;)
                {
                    ServiceMethod method;
                    try
                    {
                        method = await ServiceJournal.BoundedAsync(
                        TimeSpan.FromMinutes(8),
                        ct => service.WaitMethodAsync(PolicyFunctions.MaintainRoutes.Value, previous, ct),
                        cancellationToken).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        throw new AcceptanceException($"routes method: {e.Message}", e);
                    }

                    report["concern_id"] = method.Concern;
                    previous = method.Plan;
                    var plan = await journal.LoadPlanAsync(new PlanId(method.Plan), cancellationToken).ConfigureAwait(false);
                    var actions = plan.Spec.Actions;
                    if (actions.Count != 1)
                    {
                        throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"routes plan {method.Plan} has {actions.Count} actions; expected one door"));
                    }

                    if (actions[0].Building is not { } b || b.Definition != routes.Door || b.Stuff != routes.Stuff)
                    {
                        throw new AcceptanceException($"routes admitted {actions[0]}; expected a {routes.Door} of {routes.Stuff}");
                    }

                    var listed = before.Breaches.Any(breach => breach.Cell == b.Cell);
                    if (!listed || !room.Contains(b.Cell) || interior.Contains(b.Cell))
                    {
                        throw new AcceptanceException($"door placed at {b.Cell}: not a listed breach wall of {room} (breaches {string.Join(", ", before.Breaches)})");
                    }

                    door = b.Cell;
                    (JsonObject State, bool Incidental) done;
                    try
                    {
                        done = await ServiceJournal.BoundedAsync(
                        TimeSpan.FromMinutes(10),
                        ct => service.WaitPlanTerminalAsync(method.Plan, ct),
                        cancellationToken).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        throw new AcceptanceException($"routes plan: {e.Message}", e);
                    }

                    if (done.Incidental)
                    {
                        renewals++;
                        report["incidental_renewals"] = renewals;
                        continue;
                    }

                    plans.Add(method.Plan);
                    report["routes_plans"] = new JsonArray([.. plans.Select(p => (JsonNode?)p)]);
                    var finished = await journal.LoadPlanAsync(new PlanId(method.Plan), cancellationToken).ConfigureAwait(false);
                    report["routes_completed_tick"] = finished.Progress[0].View.Tick.Value;
                    report["door_cell"] = new JsonObject { ["x"] = door.X, ["z"] = door.Z };
                    ReviewRecord releasedReview;
                    try
                    {
                        releasedReview = await ServiceJournal.BoundedAsync(
                        TimeSpan.FromMinutes(4),
                        ct => WaitReleaseAsync(journal, service, facility, ct),
                        cancellationToken).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        throw new AcceptanceException($"routes release: {e.Message}", e);
                    }

                    released = true;
                    report["released_review_revision"] = releasedReview.Revision;
                }

                if (!released)
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"facility {facility} still latched after {plans.Count} plans"));
                }

                await Rounds.AssertRoundsRunningAsync(service.GetAsync, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                report["authority_reacquisitions"] = await stopKeepAlive().ConfigureAwait(false);
            }

            // Independent native read after the service releases the game slot.
            _ = await service.StopAsync().ConfigureAwait(false);
            h = s.Reattach();
            _ = await h.CallAsync("pause-after", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
            var after = await ReadRoutesAsync(h, identity, "routes-after", facility, cancellationToken).ConfigureAwait(false);
            report["routes_after"] = after.Evidence();
            if (after.Reachable() == 0)
            {
                throw new AcceptanceException($"routes-after: {facility} still reads unreachable by every colonist");
            }

            if (!after.Travel.Any(t => t.Reachable && t.PathCost > 0 && t.PathCells > 0))
            {
                throw new AcceptanceException($"routes-after: no colonist reports a measured path to {facility}: {string.Join(", ", after.Travel)}");
            }

            if (after.Breaches.Count != 0)
            {
                throw new AcceptanceException($"routes-after: a reachable facility still lists breaches: {string.Join(", ", after.Breaches)}");
            }

            if (after.TrafficSamples == 0)
            {
                throw new AcceptanceException("routes-after: no observed traffic samples");
            }

            report["door_traffic_samples"] = after.SamplesAt(door);
        }
        finally
        {
            // Failure evidence: the same independent routes read a pass ends with.
            if (report["routes_after"] is null)
            {
                await PostmortemAsync(s, service, identity, facility).ConfigureAwait(false);
            }
        }
    }

    private static async Task PostmortemAsync(ISession s, ServiceProcess? service, JsonObject identity, string facility)
    {
        var report = s.Report;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            if (service is not null)
            {
                _ = await service.StopAsync().ConfigureAwait(false);
            }

            var ph = s.Reattach();
            try
            {
                var after = await ReadRoutesAsync(ph, identity, "routes-postmortem", facility, cts.Token).ConfigureAwait(false);
                report["routes_postmortem"] = after.Evidence();
            }
            catch (AcceptanceException e)
            {
                report["routes_postmortem_error"] = e.Message;
            }
        }
        catch (Exception e) when (e is AcceptanceException or OperationCanceledException or InvalidOperationException)
        {
            report["postmortem_error"] = "reopen session for the postmortem: " + e.Message;
        }
    }

    private static bool LatchedOn(ReviewRecord review, string key) => review.Latches.Routes.Contains(key);

    private static Wait StoreWait(ServiceProcess service) => new() { Stall = Wait.StallBudget(), Terminal = service.Exited };

    private static async Task<ReviewRecord> WaitLatchAsync(Journal journal, ServiceProcess service, string key, CancellationToken cancellationToken)
    {
        try
        {
            return await ServiceJournal.WaitReviewAsync(
                journal,
                StoreWait(service),
                r => LatchedOn(r, key) && r.Standards.Any(binding => binding.Concern == PolicyFunctions.MaintainRoutes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"review never latched facility {key} with a bound MaintainRoutes standard: {e.Message}", e);
        }
    }

    private static async Task<ReviewRecord> WaitReleaseAsync(Journal journal, ServiceProcess service, string key, CancellationToken cancellationToken)
    {
        try
        {
            return await ServiceJournal.WaitReviewAsync(journal, StoreWait(service), r => !LatchedOn(r, key), cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"review never released the routes latch on facility {key}: {e.Message}", e);
        }
    }

    private readonly record struct CellRect(int MinX, int MinZ, int MaxX, int MaxZ)
    {
        public bool Contains(Cell c) => c.X >= MinX && c.X <= MaxX && c.Z >= MinZ && c.Z <= MaxZ;
    }

    private sealed record TravelRow(string Pawn, bool Reachable, int PathCost, int PathCells);

    private sealed record BreachRow(Cell Cell, string Edifice, string Pending, int Distance);

    private sealed class RoutesSummary
    {
        public string Kind { get; set; } = "";

        public string RoomId { get; set; } = "";

        public Cell Cell { get; set; }

        public List<TravelRow> Travel { get; } = [];

        public List<BreachRow> Breaches { get; } = [];

        public Dictionary<Cell, uint> Traffic { get; } = [];

        public uint TrafficSamples { get; set; }

        public int Reachable() => Travel.Count(t => t.Reachable);

        public uint SamplesAt(Cell c) => Traffic.TryGetValue(c, out var samples) ? samples : 0;

        public JsonObject Evidence()
        {
            var travel = new JsonArray([.. Travel.Select(t => (JsonNode?)new JsonObject { ["pawn"] = t.Pawn, ["reachable"] = t.Reachable, ["path_cost"] = t.PathCost, ["path_cells"] = t.PathCells })]);
            var breaches = new JsonArray([.. Breaches.Select(b => (JsonNode?)new JsonObject { ["x"] = b.Cell.X, ["z"] = b.Cell.Z, ["edifice"] = b.Edifice, ["pending"] = b.Pending, ["distance"] = b.Distance })]);
            var keys = Traffic.Keys.OrderByDescending(c => Traffic[c]).ThenBy(c => c.Z).ThenBy(c => c.X).ToList();
            var traffic = new JsonArray([.. keys.Select(c => (JsonNode?)new JsonObject { ["x"] = c.X, ["z"] = c.Z, ["samples"] = Traffic[c] })]);
            return new JsonObject
            {
                ["kind"] = Kind,
                ["room_id"] = RoomId,
                ["cell"] = new JsonObject { ["x"] = Cell.X, ["z"] = Cell.Z },
                ["travel"] = travel,
                ["reachable"] = Reachable(),
                ["breaches"] = breaches,
                ["traffic"] = traffic,
                ["traffic_samples"] = TrafficSamples,
            };
        }
    }

    private static Cell CellOf(JsonNode? value)
    {
        var map = value.AsMap();
        return new Cell((int)map?["x"].AsNumber()!, (int)map["z"].AsNumber());
    }

    /// <summary>Decodes the typed upkeep routes section, keeping the fixture facility's row and the traffic census.</summary>
    private static async Task<RoutesSummary> ReadRoutesAsync(Harness h, JsonObject identity, string label, string facility, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "observations_read_colony_facts",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["planning"] = false },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var upkeepSection = observed["upkeep"].AsMap() ?? [];
        JsonObject upkeep;
        try
        {
            upkeep = Harness.Outcome(upkeepSection, "observed").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: upkeep unavailable: {e.Message}", e);
        }

        foreach (var raw in upkeep["issues"].AsSlice())
        {
            var issue = raw.AsMap() ?? [];
            if (issue["field"].AsString() == "routes")
            {
                throw new AcceptanceException($"{label}: routes census unavailable: {issue.ToJson()}");
            }
        }

        var section = upkeep["routes"].AsMap() ?? [];
        JsonObject facts;
        try
        {
            facts = Harness.Outcome(section, "observed").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: routes section unavailable: {e.Message}", e);
        }

        var summary = new RoutesSummary { TrafficSamples = (uint)facts["trafficSamples"].AsNumber() };
        foreach (var raw in facts["traffic"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            summary.Traffic[CellOf(row["cell"])] = (uint)row["samples"].AsNumber();
        }

        foreach (var raw in facts["facilities"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var reference = row["facility"].AsMap();
            if (reference?["id"].AsString() != facility)
            {
                continue;
            }

            summary.Kind = row["kind"].AsString();
            summary.RoomId = row["room"].RefId();
            summary.Cell = CellOf(row["cell"]);
            foreach (var rawTravel in row["travel"].AsSlice())
            {
                var t = rawTravel.AsMap() ?? [];
                summary.Travel.Add(new TravelRow(t["pawnId"].AsString(), t["reachable"].AsBool() == true, (int)t["pathCost"].AsNumber(), (int)t["pathCells"].AsNumber()));
            }

            foreach (var rawBreach in row["breaches"].AsSlice())
            {
                var b = rawBreach.AsMap() ?? [];
                summary.Breaches.Add(new BreachRow(CellOf(b["cell"]), b["edifice"].AsString(), b["pending"].AsString(), (int)b["distance"].AsNumber()));
            }

            return summary;
        }

        throw new AcceptanceException($"{label}: routes census lists no facility {facility}");
    }
}
