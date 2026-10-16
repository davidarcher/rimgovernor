using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Stages one deficit, names the families that own it, follows the journal to recovery and audits the native
/// outcome (Go <c>upkeep.scenario</c>).
/// </summary>
internal sealed class UpkeepScenario
{
    internal required string Name { get; init; }

    internal required string Fixture { get; init; }

    internal required IReadOnlyList<string> Families { get; init; }

    /// <summary>The colonist needs the deficit's recovery depends on.</summary>
    internal IReadOnlyList<string> Keep { get; init; } = [];

    internal required Func<Harness, JsonObject, Report, CancellationToken, Task<JsonObject>> Prepare { get; init; }

    internal required Func<ControllerJournal, JsonObject, Report, CancellationToken, Task> Watch { get; init; }

    internal required Func<Harness, JsonObject, JsonObject, UpkeepCensus, Report, CancellationToken, Task> Verify { get; init; }
}

internal sealed record UpkeepItem(string Definition, int X, int Z, long Count, bool Roofed, bool InStorage, bool Forbidden, bool Medicine);

internal sealed record UpkeepStructure(string Definition, bool Home, long HitPoints, long Max);

internal sealed record UpkeepFire(bool Home, double Size);

/// <summary>The typed upkeep census the review reads: loose items with their storage state, damageable structures, fires and filth.</summary>
internal sealed class UpkeepCensus
{
    internal long Tick { get; set; }

    internal SortedDictionary<string, UpkeepItem> Items { get; } = new(StringComparer.Ordinal);

    internal SortedDictionary<string, UpkeepStructure> Structures { get; } = new(StringComparer.Ordinal);

    internal SortedDictionary<string, UpkeepFire> Fires { get; } = new(StringComparer.Ordinal);

    internal int Filth { get; set; }

    /// <summary>The census in the report's shape (the Go struct's JSON tags).</summary>
    internal JsonObject ToJson()
    {
        var items = new JsonObject();
        foreach (var (id, row) in Items)
        {
            items[id] = new JsonObject
            {
                ["definition"] = row.Definition,
                ["x"] = row.X,
                ["z"] = row.Z,
                ["count"] = row.Count,
                ["roofed"] = row.Roofed,
                ["inStorage"] = row.InStorage,
                ["forbidden"] = row.Forbidden,
                ["medicine"] = row.Medicine,
            };
        }

        var structures = new JsonObject();
        foreach (var (id, row) in Structures)
        {
            structures[id] = new JsonObject
            {
                ["definition"] = row.Definition,
                ["home"] = row.Home,
                ["hitPoints"] = row.HitPoints,
                ["maxHitPoints"] = row.Max,
            };
        }

        var fires = new JsonObject();
        foreach (var (id, row) in Fires)
        {
            fires[id] = new JsonObject { ["home"] = row.Home, ["size"] = row.Size };
        }

        return new JsonObject { ["tick"] = Tick, ["items"] = items, ["structures"] = structures, ["fires"] = fires, ["filth"] = Filth };
    }
}

/// <summary>The upkeep family's shared scenarios, census reads and journal follow-ups (Go <c>upkeep.go</c>).</summary>
internal static class UpkeepSupport
{
    internal const string Prefix = "upkeep";

    /// <summary>
    /// Ends <see cref="WaitNeedAsync"/> once the rounds stops advancing: the governor parked the game (no work) or it
    /// died, and the need cannot move.
    /// </summary>
    internal static readonly TimeSpan NeedStall = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The Impressiveness room-stat score the native game calls slightly impressive. The Go cases read it from the
    /// live definition catalog (<c>catalog.ImpressivenessLevels()</c>); the out-of-process harness has no catalog, so
    /// the vanilla value the cases' comments already name (50) stands in.
    /// </summary>
    internal const double SlightlyImpressive = 50.0;

    private static readonly Dictionary<string, UpkeepScenario> s_scenarios = BuildScenarios();

    /// <summary>The sleeping scenario's Keep: recovery is every colonist observed asleep in an owned bed.</summary>
    internal static IReadOnlyList<string> SleepingKeep => s_scenarios["sleeping"].Keep;

    internal static IReadOnlyDictionary<string, UpkeepScenario> Scenarios() => s_scenarios;

    private static Dictionary<string, UpkeepScenario> BuildScenarios()
    {
        var s = new Dictionary<string, UpkeepScenario>(StringComparer.Ordinal);

        // Every medicine stack destroyed and forty mature healroot within fifteen cells: eight harvests, all
        // admitted on one stop, recover the reserve in about a minute of wall time.
        s["medicine"] = new UpkeepScenario
        {
            Name = "medicine",
            Fixture = "test/medicine_setup",
            Families = ["medical", "resource", "bill", "acquisition", "work"],
            Prepare = (h, identity, report, ct) => CallFixtureAsync(h, identity, "test/medicine_setup", [], ct),
            Watch = WatchMedicineAsync,
            Verify = VerifyMedicineAsync,
        };
        s["sleeping"] = new UpkeepScenario
        {
            Name = "sleeping",
            Fixture = "test/sleeping_setup",
            Families = ["sleeping", "work"],
            Prepare = async (h, identity, report, ct) =>
            {
                var prepared = await CallFixtureAsync(h, identity, "test/sleeping_setup", [], ct).ConfigureAwait(false);
                report["fixture_owned_beds"] = prepared["ownedBeds"].AsSlice().Count;
                report["fixture_room_temperature_c"] = prepared["roomTemperatureC"]?.DeepClone();
                return prepared;
            },
            Watch = WatchSleepingAsync,
            Verify = VerifySleepingAsync,
            // Recovery is every colonist observed asleep in an owned bed.
            Keep = [NeedDef.Rest.Value],
        };
        s["cold"] = new UpkeepScenario
        {
            Name = "cold",
            Fixture = "test/routine_temperature_prepare",
            Families = ["temperature", "work"],
            Prepare = PrepareColdWith(false),
            Watch = WatchColdAsync,
            Verify = VerifyColdAsync,
        };
        return s;
    }

    /// <summary>Calls a fixture op and requires its reply to report success.</summary>
    internal static async Task<JsonObject> CallFixtureAsync(Harness h, JsonObject identity, string tool, JsonObject args, CancellationToken cancellationToken)
    {
        var prepared = await h.CallAsync("prepare", tool, args, cancellationToken).ConfigureAwait(false);
        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{tool} refused: {prepared.ToJson()}");
        }

        return prepared;
    }

    /// <summary>The observed body of <c>observations_read_colony_facts</c>.</summary>
    internal static async Task<JsonObject> ReadColonyFactsAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "observations_read_colony_facts", new JsonObject
        {
            ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
            ["planning"] = false,
        }, cancellationToken).ConfigureAwait(false);
        return Harness.Outcome(reply, "observed").Value;
    }

    /// <summary>The upkeep section of the colony facts, with the label naming a section that is not observed.</summary>
    internal static JsonObject UpkeepSection(JsonObject observed, string label)
    {
        var section = observed["upkeep"].AsMap() ?? [];
        try
        {
            return Harness.Outcome(section, "observed").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: upkeep facts unavailable: {e.Message}", e);
        }
    }

    /// <summary>
    /// Decodes the typed upkeep census the review reads: loose items with their storage state, damageable structures,
    /// fires and filth.
    /// </summary>
    internal static async Task<UpkeepCensus> ReadUpkeepAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        var c = new UpkeepCensus();
        var observed = await ReadColonyFactsAsync(h, identity, label, cancellationToken).ConfigureAwait(false);
        var context = observed["context"].Obj();
        c.Tick = (long)context["tick"].AsNumber();
        var upkeep = UpkeepSection(observed, label);
        foreach (var raw in upkeep["items"].AsSlice())
        {
            var row = raw.Obj();
            var item = row["item"].Obj();
            var position = item["position"].Obj();
            c.Items[item["id"].AsString()] = new UpkeepItem(
                item["defName"].AsString(),
                (int)position["x"].AsNumber(),
                (int)position["z"].AsNumber(),
                (long)row["count"].AsNumber(),
                row["roofed"].AsBool() == true,
                row["inStorage"].AsBool() == true,
                row["forbidden"].AsBool() == true,
                row["medicine"].AsBool() == true);
        }

        var rows = await h.BuildingRowsAsync(label + "-buildings", identity, cancellationToken).ConfigureAwait(false);
        foreach (var raw in upkeep["structures"].AsSlice())
        {
            var row = raw.Obj();
            var building = row["building"].Obj();
            var id = building["id"].AsString();
            if (!rows.TryGetValue(id, out var state))
            {
                throw new AcceptanceException($"{label}: structure {building["id"].ToJson()} is not in the building table");
            }

            var head = state["building"].Obj();
            c.Structures[id] = new UpkeepStructure(
                head["defName"].AsString(),
                row["home"].AsBool() == true,
                (long)state["hitPoints"].AsNumber(),
                (long)state["maxHitPoints"].AsNumber());
        }

        foreach (var raw in upkeep["fires"].AsSlice())
        {
            var row = raw.Obj();
            var fire = row["fire"].Obj();
            c.Fires[fire["id"].AsString()] = new UpkeepFire(row["home"].AsBool() == true, row["size"].AsNumber());
        }

        c.Filth = upkeep["filth"].AsSlice().Count;
        return c;
    }

    // ---- journal helpers -----------------------------------------------------

    /// <summary>
    /// Polls until <paramref name="need"/> reports <paramref name="met"/>: unmet while the review carries the Need's
    /// progress row, met once a later review has pruned it.
    /// </summary>
    internal static async Task WaitNeedRowAsync(ControllerJournal journal, NeedId need, bool met, CancellationToken cancellationToken)
    {
        try
        {
            await new Wait { Stall = NeedStall, Interval = TimeSpan.FromSeconds(1) }.WaitProgressAsync(async ct =>
            {
                var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
                var row = review.Progress.Any(p => p.Concern == need.FilingKey);
                if (review.Revision != 0 && row != met)
                {
                    return ("", true);
                }

                // A parked or dead game stops producing reviews at new ticks.
                return (string.Create(CultureInfo.InvariantCulture, $"tick={review.Tick.Value} revision={review.Revision}"), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"{need.Value} never reported {(met ? "met" : "unmet")}: {e.Message}", e);
        }
    }

    /// <summary>Polls until need's goal binding reports state (deficit or recovered) and returns the goal.</summary>
    internal static async Task<RimGovernor.Brain.Store.StandardState> WaitNeedAsync(ControllerJournal journal, ConcernId need, Finding state, CancellationToken cancellationToken)
    {
        RimGovernor.Brain.Store.StandardState? found = null;
        try
        {
            await new Wait { Stall = NeedStall, Interval = TimeSpan.FromSeconds(1) }.WaitProgressAsync(async ct =>
            {
                var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
                foreach (var binding in review.Standards)
                {
                    if (binding.Concern != need)
                    {
                        continue;
                    }

                    RimGovernor.Brain.Store.StandardState? goal = null;
                    try
                    {
                        goal = await journal.Store.LoadStandardAsync(binding.Standard, ct).ConfigureAwait(false);
                    }
                    catch (RimGovernor.Brain.Store.NotFoundException tolerated)
                    {
                        // The binding is ahead of the goal's row; read again next poll.
                        Tolerated.Note("goal row not yet written", tolerated);
                    }

                    if (goal is not null && goal.Standard.Finding == state)
                    {
                        found = goal;
                        return ("", true);
                    }
                }

                // A parked or dead game stops producing reviews at new ticks.
                return (string.Create(CultureInfo.InvariantCulture, $"tick={review.Tick.Value} revision={review.Revision}"), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"{need.Value} never reported {JournalReads.FindingName(state)}: {e.Message}", e);
        }

        return found!;
    }

    /// <summary>
    /// Waits for methods on <paramref name="need"/> whose plan's actions all satisfy <paramref name="accept"/> (a
    /// non-null reply is the reason an action is not acceptable), following the goal's method lineage across incidental
    /// (authority-discontinuity) cancellations until one plan completes. The controller's order supplements ordinary
    /// colonist work rather than replacing it: when the goal recovers on the native postcondition before the
    /// controller's plan completes (a colonist hauled or repaired it on their own priorities), the review invalidates
    /// the goal and cancels its never-dispatched plan; that counts as recovery too and is recorded as
    /// &lt;label&gt;_recovered_by=ordinary_work instead of &lt;label&gt;_completed_tick. Either way the scenario's verify
    /// step confirms the postcondition natively.
    /// </summary>
    internal static Task FollowMethodsAsync(ControllerJournal journal, ConcernId need, string label, Func<DomainAction, string?> accept, Report report, CancellationToken cancellationToken) =>
        FollowMethodsExcludingAsync(journal, need, label, new HashSet<string>(StringComparer.Ordinal), accept, report, cancellationToken);

    /// <summary>
    /// <see cref="FollowMethodsAsync"/> over a caller-owned seen set, so a goal whose deficit needs two successive
    /// methods (a bed built, then that bed assigned) is followed method by method without revisiting the first.
    /// </summary>
    internal static Task FollowMethodsExcludingAsync(ControllerJournal journal, ConcernId need, string label, HashSet<string> seen, Func<DomainAction, string?> accept, Report report, CancellationToken cancellationToken) =>
        FollowCoreAsync(journal, need.Value, ct => StandardMetAsync(journal, need, ct), label, seen, accept, report, cancellationToken);

    /// <summary>The family Need's <see cref="FollowMethodsAsync"/>: its Methods file under its id, and it is met once its progress row is gone.</summary>
    internal static Task FollowMethodsAsync(ControllerJournal journal, NeedId need, string label, Func<DomainAction, string?> accept, Report report, CancellationToken cancellationToken) =>
        FollowMethodsExcludingAsync(journal, need, label, new HashSet<string>(StringComparer.Ordinal), accept, report, cancellationToken);

    /// <summary>The family Need's <see cref="FollowMethodsExcludingAsync(ControllerJournal, ConcernId, string, HashSet{string}, Func{DomainAction, string?}, Report, CancellationToken)"/>.</summary>
    internal static Task FollowMethodsExcludingAsync(ControllerJournal journal, NeedId need, string label, HashSet<string> seen, Func<DomainAction, string?> accept, Report report, CancellationToken cancellationToken) =>
        FollowCoreAsync(journal, need.Value, async ct => NeedMet(await journal.LoadRoundsAsync(ct).ConfigureAwait(false), need), label, seen, accept, report, cancellationToken);

    /// <summary>
    /// Polls until the family Need reports <paramref name="state"/> (#3231 decision 18): an unmet Need has a progress
    /// row under its filing key, a met one has none. Met is read only after the row was seen, or after three reviews
    /// at distinct ticks without one, so a review before the family's first run does not read as met.
    /// </summary>
    internal static async Task WaitNeedAsync(ControllerJournal journal, NeedId need, Finding state, CancellationToken cancellationToken)
    {
        var seenUnmet = false;
        var quiet = new HashSet<long>();
        try
        {
            await new Wait { Stall = NeedStall, Interval = TimeSpan.FromSeconds(1) }.WaitProgressAsync(async ct =>
            {
                var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
                if (review.Revision == 0)
                {
                    return ("no-review", false);
                }
                var met = NeedMet(review, need);
                seenUnmet |= !met;
                if (met)
                {
                    quiet.Add(review.Tick.Value);
                }
                var done = state == Finding.Unmet ? !met : met && (seenUnmet || quiet.Count >= 3);
                return done ? ("", true) : (string.Create(CultureInfo.InvariantCulture, $"tick={review.Tick.Value} revision={review.Revision}"), false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (WaitError e)
        {
            throw new AcceptanceException($"{need.Value} never reported {JournalReads.FindingName(state)}: {e.Message}", e);
        }
    }

    /// <summary>Whether the review files no progress row for the family Need: a reported unmet Need always has one.</summary>
    private static bool NeedMet(RimGovernor.Brain.Store.Rounds review, NeedId need) =>
        review.Revision != 0 && !review.Progress.Any(p => p.Concern == need.FilingKey);

    /// <summary>Whether a goal the review binds to <paramref name="need"/> is met.</summary>
    private static async Task<bool> StandardMetAsync(ControllerJournal journal, ConcernId need, CancellationToken cancellationToken)
    {
            var review = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
            if (new NeedId(need.Value).IsValid)
            {
                // A Need (its filing key) is met once the review prunes its progress row.
                return review.Revision != 0 && !review.Progress.Any(p => p.Concern == need);
            }
            foreach (var binding in review.Standards)
            {
                if (binding.Concern != need)
                {
                    continue;
                }

                try
                {
                    var goal = await journal.Store.LoadStandardAsync(binding.Standard, cancellationToken).ConfigureAwait(false);
                    if (goal.Standard.Finding == Finding.Met)
                    {
                        return true;
                    }
                }
                catch (RimGovernor.Brain.Store.NotFoundException)
                {
                    continue;
                }
            }

            return false;
    }

    private static async Task FollowCoreAsync(ControllerJournal journal, string concern, Func<CancellationToken, Task<bool>> met, string label, HashSet<string> seen, Func<DomainAction, string?> accept, Report report, CancellationToken cancellationToken)
    {
        var renewals = 0;
        var rejected = 0;
        Task<bool> RecoveredAsync() => met(cancellationToken);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
        while (true)
        {
            ServiceMethod method;
            while (true)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new AcceptanceException($"{label} method: no method within 10 minutes");
                }

                using var methodCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                methodCts.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    method = await journal.Service.WaitMethodExcludingAsync(concern, seen, methodCts.Token).ConfigureAwait(false);
                    break;
                }
                catch (Exception e) when (e is AcceptanceException or OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new AcceptanceException($"{label} method: {e.Message}", e);
                    }
                }

                if (await RecoveredAsync().ConfigureAwait(false))
                {
                    report[label + "_recovered_by"] = "ordinary_work";
                    return;
                }
            }

            seen.Add(method.Plan);
            var plan = await journal.Store.LoadPlanAsync(new PlanId(method.Plan), cancellationToken).ConfigureAwait(false);
            // A harvest acquisition designates one action per plant, so a plan may carry several actions; every one
            // must be acceptable.
            var actions = plan.Spec.Actions;
            if (actions.Count == 0)
            {
                throw new AcceptanceException($"{label} plan {method.Plan} has no actions");
            }

            string? rejectedReason = null;
            foreach (var action in actions)
            {
                rejectedReason = accept(action);
                if (rejectedReason is not null)
                {
                    break;
                }
            }

            if (rejectedReason is not null)
            {
                rejected++;
                report[label + "_rejected_methods"] = rejected;
                if (rejected > 16)
                {
                    throw new AcceptanceException($"{label}: {rejected} methods without an acceptable action; last: {rejectedReason}");
                }

                continue;
            }

            report[label + "_standard_id"] = method.Concern;
            report[label + "_method"] = method.Method;
            report[label + "_plan"] = method.Plan;
            // The plan is followed to a terminal stage, but a need that recovers on its own while the plan is still
            // undispatched ends the follow: a recovered routine goal never authorizes that write again, it merely
            // keeps the plan for a returning deficit, so no terminal stage is coming.
            JsonObject? state;
            bool incidental;
            bool undispatched;
            try
            {
                (state, incidental, undispatched) = await WaitPlanOrRecoveryAsync(journal, method.Plan, RecoveredAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{label} plan {method.Plan}: {e.Message}", e);
            }

            if (undispatched)
            {
                report[label + "_recovered_by"] = "ordinary_work";
                report[label + "_plan_undispatched"] = true;
                return;
            }

            if (incidental)
            {
                if (await RecoveredAsync().ConfigureAwait(false))
                {
                    report[label + "_recovered_by"] = "ordinary_work";
                    return;
                }

                renewals++;
                report[label + "_incidental_renewals"] = renewals;
                if (renewals > 12)
                {
                    throw new AcceptanceException($"{label}: {renewals} incidental renewals without completion");
                }

                deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
                continue;
            }

            long completed = 0;
            foreach (var action in state!["actions"].AsSlice())
            {
                completed = Math.Max(completed, (long)action.AsMap()?["progress"].AsMap()?["tick"].AsNumber()!);
            }

            report[label + "_completed_tick"] = completed;
            report[label + "_actions"] = actions.Count;
            report[label + "_recovered_by"] = "controller_order";
            return;
        }
    }

    /// <summary>
    /// Waits up to ten minutes for the plan to reach a terminal stage (<see cref="ServiceProcess.WaitPlanTerminalAsync"/>),
    /// returning undispatched=true instead when the goal recovers while every action of the plan is still at attempt 0.
    /// </summary>
    internal static async Task<(JsonObject? State, bool Incidental, bool Undispatched)> WaitPlanOrRecoveryAsync(ControllerJournal journal, string planId, Func<Task<bool>> recovered, CancellationToken cancellationToken)
    {
        using var doneCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        doneCts.CancelAfter(TimeSpan.FromMinutes(10));
        var terminal = journal.Service.WaitPlanTerminalAsync(planId, doneCts.Token);
        while (true)
        {
            var first = await Task.WhenAny(terminal, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)).ConfigureAwait(false);
            if (first == terminal)
            {
                var (state, incidental) = await terminal.ConfigureAwait(false);
                return (state, incidental, false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await recovered().ConfigureAwait(false))
            {
                continue;
            }

            RimGovernor.Brain.Store.PlanState current;
            try
            {
                current = await journal.Store.LoadPlanAsync(new PlanId(planId), cancellationToken).ConfigureAwait(false);
            }
            catch (RimGovernor.Brain.Store.NotFoundException)
            {
                continue;
            }

            var pending = current.Progress.Count > 0;
            foreach (var progress in current.Progress)
            {
                var v = progress.View;
                if (v.Attempt.Value != 0 || (v.Stage != Stage.Pending && v.Stage != Stage.Prepared))
                {
                    pending = false;
                }
            }

            if (pending)
            {
                await doneCts.CancelAsync().ConfigureAwait(false);
                try
                {
                    _ = await terminal.ConfigureAwait(false);
                }
                catch (Exception e) when (e is AcceptanceException or OperationCanceledException)
                {
                    // The wait was ended on purpose.
                    Tolerated.Note("terminal wait ended", e);
                }

                return (null, false, true);
            }
        }
    }

    // ---- medicine ------------------------------------------------------------

    private static async Task WatchMedicineAsync(ControllerJournal journal, JsonObject prepared, Report report, CancellationToken cancellationToken)
    {
        using (var deficitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deficitCts.CancelAfter(TimeSpan.FromMinutes(4));
            _ = await WaitNeedAsync(journal, PolicyFunctions.MedicalReservesNeed.FilingKey, Finding.Unmet, deficitCts.Token).ConfigureAwait(false);
        }

        // The method is an ordinary resource acquisition of the native herbal-medicine definition; follow it to
        // completion, then require the observed reserve rather than the receipt.
        await FollowMethodsAsync(journal, PolicyFunctions.MedicalReservesNeed.FilingKey, "replenish", a => a.Kind switch
        {
            ActionKind.Acquisition or ActionKind.ProductionBill or ActionKind.MineAcquisition => null,
            _ => $"unexpected {a.Kind} action",
        }, report, cancellationToken).ConfigureAwait(false);
        using var recoverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        recoverCts.CancelAfter(TimeSpan.FromMinutes(12));
        _ = await WaitNeedAsync(journal, PolicyFunctions.MedicalReservesNeed.FilingKey, Finding.Met, recoverCts.Token).ConfigureAwait(false);
    }

    private static async Task VerifyMedicineAsync(Harness h, JsonObject identity, JsonObject prepared, UpkeepCensus after, Report report, CancellationToken cancellationToken)
    {
        long units = 0;
        foreach (var item in after.Items.Values)
        {
            if (item.Medicine && !item.Forbidden)
            {
                units += item.Count;
            }
        }

        var colonists = await CountColonistsAsync(h, identity, cancellationToken).ConfigureAwait(false);
        var reserve = MedicalReservePolicy.Default();
        report["medicine_units_after"] = units;
        report["colonists"] = colonists;
        if (units < reserve.TargetPerColonist * colonists)
        {
            throw new AcceptanceException($"{units} medicine units for {colonists} colonists is under the recovery reserve of {reserve.TargetPerColonist} each");
        }
    }

    private static async Task<int> CountColonistsAsync(Harness h, JsonObject identity, CancellationToken cancellationToken)
    {
        var observed = await ReadColonyFactsAsync(h, identity, "colonists-after", cancellationToken).ConfigureAwait(false);
        return (int)observed["colonistCount"].AsNumber();
    }

    // ---- sleeping ------------------------------------------------------------

    private static async Task WatchSleepingAsync(ControllerJournal journal, JsonObject prepared, Report report, CancellationToken cancellationToken)
    {
        using (var deficitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deficitCts.CancelAfter(TimeSpan.FromMinutes(4));
            await WaitNeedAsync(journal, ShelterFamily.Sleeping, Finding.Unmet, deficitCts.Token).ConfigureAwait(false);
        }

        // The fixture leaves no vacant suitable bed, so the first method builds one (a Building action). Ownership
        // then comes either from the controller's AssignIntent (a bed_assign action, followed as its own method) or
        // from the colonist claiming the new bed on their own; the goal recovers only on observed sleep in an owned
        // suitable bed, which WaitNeedAsync below confirms and VerifySleepingAsync checks natively.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new JsonArray();
        await FollowMethodsExcludingAsync(journal, ShelterFamily.Sleeping, "bed", seen, a =>
        {
            if (a.Building is not null)
            {
                kinds.Add(a.Kind.ToString());
                return null;
            }

            return $"unexpected {a.Kind} action before a bed was built";
        }, report, cancellationToken).ConfigureAwait(false);
        if (report["bed_recovered_by"].AsString() != "ordinary_work")
        {
            await FollowMethodsExcludingAsync(journal, ShelterFamily.Sleeping, "assign", seen, a =>
            {
                if (a.Assign is not null)
                {
                    kinds.Add(a.Kind.ToString());
                    return null;
                }

                return $"unexpected {a.Kind} action after the bed was built";
            }, report, cancellationToken).ConfigureAwait(false);
        }

        report["sleeping_action_kinds"] = kinds;
        using var recoverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        recoverCts.CancelAfter(TimeSpan.FromMinutes(15));
        await WaitNeedAsync(journal, ShelterFamily.Sleeping, Finding.Met, recoverCts.Token).ConfigureAwait(false);
    }

    private static async Task VerifySleepingAsync(Harness h, JsonObject identity, JsonObject prepared, UpkeepCensus after, Report report, CancellationToken cancellationToken)
    {
        var observed = await ReadColonyFactsAsync(h, identity, "beds-after", cancellationToken).ConfigureAwait(false);
        var upkeep = UpkeepSection(observed, "beds-after");
        var beds = upkeep["beds"].AsSlice();
        report["beds_after"] = beds.Count;
        // Recovery needs an owned humanlike bed that is a real bed (a sleeping spot never satisfies ReviewSleeping)
        // under a roof.
        var owned = 0;
        var suitable = 0;
        foreach (var raw in beds)
        {
            var row = raw.AsMap();
            if (row?["owners"].AsSlice().Count is not > 0)
            {
                continue;
            }

            owned++;
            var definition = row["bed"].AsMap()?["defName"] is { } d ? d.AsString() : "";
            if (definition != "SleepingSpot" && row["roofed"].AsBool() == true && row["humanlike"].AsBool() == true)
            {
                suitable++;
            }
        }

        report["owned_beds_after"] = owned;
        report["owned_suitable_beds_after"] = suitable;
        var colonists = (int)observed["colonistCount"].AsNumber();
        report["colonists"] = colonists;
        if (colonists == 0 || suitable < colonists)
        {
            throw new AcceptanceException($"{suitable} of {colonists} colonists own a roofed bed after shelter/sleeping was met");
        }
    }

    // ---- cold ----------------------------------------------------------------

    /// <summary>
    /// Stages the cold room; <paramref name="coldSnap"/> lets the fixture register ordinary ColdSnap conditions when the
    /// map has warmed past the campfire threshold (a colony that has already played for days, as in the campaign)
    /// instead of failing fast.
    /// </summary>
    internal static Func<Harness, JsonObject, Report, CancellationToken, Task<JsonObject>> PrepareColdWith(bool coldSnap) =>
        async (h, identity, report, cancellationToken) =>
        {
            var room = await CallFixtureAsync(h, identity, "test/routine_sleeping_prepare", new JsonObject { ["outdoorSite"] = false }, cancellationToken).ConfigureAwait(false);
            report["room"] = room;
            var center = room["center"].Obj();
            JsonObject prepared;
            try
            {
                prepared = await h.CallAsync("prepare-temperature", "test/routine_temperature_prepare", new JsonObject
                {
                    ["x"] = (int)center["x"].AsNumber(),
                    ["z"] = (int)center["z"].AsNumber(),
                    ["hot"] = false,
                    ["coldSnap"] = coldSnap,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e) when (e.Message.Contains("outdoor temperature", StringComparison.Ordinal))
            {
                throw new AcceptanceException($"rolled map is not cold enough for the campfire fixture; rerun as a fresh process: {e.Message}", e);
            }

            if (prepared["success"].AsBool() != true)
            {
                throw new AcceptanceException($"routine_temperature_prepare refused: {prepared.ToJson()}");
            }

            report["cold_snaps"] = prepared["coldSnaps"]?.DeepClone();
            return prepared;
        };

    private static async Task WatchColdAsync(ControllerJournal journal, JsonObject prepared, Report report, CancellationToken cancellationToken)
    {
        using (var deficitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deficitCts.CancelAfter(TimeSpan.FromMinutes(4));
            await WaitNeedAsync(journal, ShelterFamily.Temperature, Finding.Unmet, deficitCts.Token).ConfigureAwait(false);
        }

        var definition = prepared["definition"].AsString();
        await FollowMethodsAsync(journal, ShelterFamily.Temperature, "heat", a =>
        {
            if (a.Building is not { } b)
            {
                return $"unexpected {a.Kind} action";
            }

            return b.Definition != definition ? $"placed {b.Definition}, expected the fixture's {definition}" : null;
        }, report, cancellationToken).ConfigureAwait(false);
        using var recoverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        recoverCts.CancelAfter(TimeSpan.FromMinutes(12));
        await WaitNeedAsync(journal, ShelterFamily.Temperature, Finding.Met, recoverCts.Token).ConfigureAwait(false);
    }

    private static async Task VerifyColdAsync(Harness h, JsonObject identity, JsonObject prepared, UpkeepCensus after, Report report, CancellationToken cancellationToken)
    {
        var observed = await ReadColonyFactsAsync(h, identity, "temperature-after", cancellationToken).ConfigureAwait(false);
        var minC = observed["sleepingTemperatureMinC"];
        report["sleeping_temperature_min_c"] = minC?.DeepClone();
        report["outdoor_temperature_c"] = observed["outdoorTemperatureC"]?.DeepClone();
        if (minC is null)
        {
            throw new AcceptanceException("native sleeping temperature unknown after recovery");
        }

        var upkeep = UpkeepSection(observed, "temperature-after");
        var ids = new List<string>();
        foreach (var raw in upkeep["beds"].AsSlice())
        {
            ids.Add(raw.AsMap()?["bed"].AsMap()?["id"].AsString() ?? "");
        }

        ids.Sort(StringComparer.Ordinal);
        report["beds_after"] = new JsonArray([.. ids.Select(id => (JsonNode?)id)]);
        var min = minC.AsNumber();
        var outdoors = observed["outdoorTemperatureC"].AsNumber();
        if (min <= outdoors)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"sleeping minimum {min:F1} C is no warmer than outdoors {outdoors:F1} C"));
        }
    }
}
