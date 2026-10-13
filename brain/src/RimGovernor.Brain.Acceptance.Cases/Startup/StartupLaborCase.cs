using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Journal = RimGovernor.Brain.Store.Store;
using ReviewRecord = RimGovernor.Brain.Store.Rounds;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>startup/labor</c> (Go <c>startup/labor.go</c>): the startup-labor reproduction. The eight-colonist fixture and
/// the bounded diagnosis that makes startup starvation visible. The case is diagnostic infrastructure, not the
/// scheduling fix: it asserts the fixture's preconditions and that the diagnosis classified what it saw, and a run
/// that still starves passes and says so in its artifact.
/// </summary>
internal static class StartupLaborCase
{
    /// <summary>Selects the fixture variant: wood_sufficient (the default), wood_shortage or bed_blocked.</summary>
    internal const string VariantEnv = "RIMGOVERNOR_ACCEPT_STARTUP_LABOR_VARIANT";

    /// <summary>Overrides the observation window in game ticks.</summary>
    internal const string TicksEnv = "RIMGOVERNOR_ACCEPT_STARTUP_LABOR_TICKS";

    // The observed stretch: a third of a game day, long enough for several reviews and the first construction rung.
    private const long WindowTicks = 20000;

    // What the fixture establishes and the case validates.
    private const int ExpectedPawns = 8;

    // The shelter ladder's beds rung (BuildingRuntime's ShelterBedsMethod).
    private const string ShelterBedsMethod = "shelter-beds";

    internal static AcceptanceCase Case() => new()
    {
        Name = "startup/labor",
        Scope = "Issue #639: the eight-colonist startup fixture (no shelter, mixed construction skill, reachable wood, an animal-feed deficit and scattered supplies) "
            + "comes up on its declared preconditions, and the startup-labor diagnosis classifies every reviewed standard from evidence the run already holds.",
        Start = new FixtureStart
        {
            Op = "test/startup_labor_setup",
            Args = new JsonObject { ["variant"] = Variant() },
            On = new SaveStart { Name = Baselines.BaselineName },
        },
        RequiredOps = ["test/startup_labor_setup", "test/startup_labor_read"],
        Keep = [NeedDef.LiveNeeds.Value],
        Serve = new ServeSpec { NativeTimeout = TimeSpan.FromSeconds(30), Prefix = "startuplabor" },
        Budget = TimeSpan.FromMinutes(15),
        Crew = new Crew { Size = 3 },
        Reason = "one window of a third of a game day over an eight-colonist fixture",
        Run = RunAsync,
    };

    private static string Variant() => Environment.GetEnvironmentVariable(VariantEnv) switch
    {
        "wood_sufficient" => "wood_sufficient",
        "wood_shortage" => "wood_shortage",
        "bed_blocked" => "bed_blocked",
        _ => "wood_sufficient",
    };

    private static long Window() =>
        long.TryParse(Environment.GetEnvironmentVariable(TicksEnv), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : WindowTicks;

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var variant = Variant();
        report["variant"] = variant;
        report["window_ticks"] = Window();
        var prepared = ValidateFixture(s.Prepared);
        report["fixture"] = prepared;
        var obs = await ObserveAsync(s, variant, cancellationToken).ConfigureAwait(false);
        report["observation"] = obs.Row();
    }

    /// <summary>
    /// Checks the precondition the fixture reports: eight colonists with mixed construction skill, reachable wood, no
    /// player bed, a hungry animal with no feed and scattered supply stacks. A refusal or a missing precondition fails
    /// the case here, before any play.
    /// </summary>
    private static JsonObject ValidateFixture(JsonObject? prepared)
    {
        if (prepared is null)
        {
            throw new AcceptanceException("the startup-labor fixture returned no reply");
        }

        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"startup-labor fixture refused: {prepared["reason"].ToJson()}");
        }

        var row = new JsonObject
        {
            ["variant"] = prepared["variant"].AsString(),
            ["seed"] = prepared["seed"].AsString(),
            ["tick"] = prepared["tick"].AsNumber(),
            ["wood"] = prepared["wood"].AsNumber(),
            ["wood_forbidden"] = prepared["woodForbidden"]?.DeepClone(),
            ["beds"] = prepared["beds"].AsNumber(),
            ["feed_stacks"] = prepared["feedStacks"].AsNumber(),
            ["pet_food"] = prepared["petFood"].AsNumber(),
            ["scattered"] = prepared["scattered"]?.DeepClone(),
            ["colonists"] = prepared["colonists"]?.DeepClone(),
            ["wood_reachable_by"] = prepared["woodReachableBy"].AsNumber(),
        };
        var colonists = prepared["colonists"].AsSlice();
        if (colonists.Count != ExpectedPawns)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"fixture reports {colonists.Count} colonists, want {ExpectedPawns}"));
        }

        var skills = new HashSet<double>();
        foreach (var raw in colonists)
        {
            var c = raw.AsMap() ?? [];
            if (c["id"].AsString().Length == 0 || c["name"].AsString().Length == 0)
            {
                throw new AcceptanceException($"a fixture colonist has no identity: {c.ToJson()}");
            }

            _ = skills.Add(c["construction"].AsNumber());
        }

        if (skills.Count < 2)
        {
            throw new AcceptanceException($"construction skill is not mixed across the eight colonists: {prepared["colonists"].ToJson()}");
        }

        if (prepared["beds"].AsNumber() != 0)
        {
            throw new AcceptanceException($"the fixture left {prepared["beds"].ToJson()} player bed(s) standing");
        }

        if (prepared["feedStacks"].AsNumber() != 0 || prepared["petFood"].AsNumber() <= 0)
        {
            throw new AcceptanceException($"no animal-feed deficit: {prepared["feedStacks"].ToJson()} feed stack(s), pet food {prepared["petFood"].ToJson()}");
        }

        if (prepared["scattered"].AsSlice().Count == 0)
        {
            throw new AcceptanceException("the fixture scattered no supplies");
        }

        if (prepared["wood"].AsNumber() <= 0)
        {
            throw new AcceptanceException("the fixture placed no wood");
        }

        var reach = prepared["woodReachableBy"].AsNumber();
        if (reach < ExpectedPawns)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the wood pile is reachable by {reach} of {ExpectedPawns} colonists"));
        }

        return row;
    }

    /// <summary>
    /// Plays one window and derives the window's diagnosis: the per-review classification, the idle accounting off the
    /// reviews' own pawn census, and the two shelter milestones.
    /// </summary>
    private static async Task<LaborObservation> ObserveAsync(ISession s, string variant, CancellationToken cancellationToken)
    {
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        var stopped = false;
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            using var st = await ServiceJournal.OpenWithRetryAsync(ServiceJournal.PathOf(s.Config), cancellationToken).ConfigureAwait(false);
            var window = Window();
            long? firstEnclosure = null;
            long? shelterRecovery = null;
            var w = new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(2), Terminal = service.Exited };
            var diagnoses = new List<LaborDiagnosis>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long start = 0;
            try
            {
                await w.WaitProgressAsync(async ct =>
                {
                    ReviewRecord review;
                    try
                    {
                        review = await st.LoadRoundsAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        return (Wait.Signature("no-review", e.Message), false);
                    }

                    if (review.Tick.Value == 0)
                    {
                        return ("no-review-yet", false);
                    }

                    if (start == 0)
                    {
                        start = review.Tick.Value;
                    }

                    var rows = await ReviewDiagnosesAsync(st, review, ct).ConfigureAwait(false);
                    foreach (var d in rows)
                    {
                        var key = string.Create(CultureInfo.InvariantCulture, $"{d.ReviewTick}/{d.Concern}/{d.Action}");
                        if (seen.Add(key))
                        {
                            diagnoses.Add(d);
                        }
                    }

                    shelterRecovery = await ShelterRecoveryAsync(st, review, shelterRecovery, ct).ConfigureAwait(false);
                    firstEnclosure = await FirstEnclosureAsync(st, firstEnclosure, ct).ConfigureAwait(false);
                    var done = review.Tick.Value - start >= window;
                    return (Wait.Signature(review.Tick.Value, diagnoses.Count), done);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (WaitError e)
            {
                // A window that never advances is itself the observation this case exists to record, not a harness
                // failure to hide: the error carries the reviews seen so far.
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"observed {diagnoses.Count} diagnoses before the window closed: {e.Message}"), e);
            }

            _ = await service.StopAsync().ConfigureAwait(false);
            stopped = true;
            var obs = new LaborObservation
            {
                Variant = variant,
                WindowTicks = window,
                FirstEnclosure = firstEnclosure,
                ShelterRecovery = shelterRecovery,
                Blocked = StartupLabor.BlockedTicks(diagnoses, window, 0),
                Idle = IdleAccount(service.LogPath),
                Revision = s.Report["revision"].AsString(),
            };
            s.Report["diagnoses"] = new JsonArray([.. diagnoses.Select(d => (JsonNode?)d.Row())]);
            return obs;
        }
        finally
        {
            if (!stopped)
            {
                _ = await service.StopAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>One subject per ranked goal of the review: the ranking row, the goal's open action (when it bound one) and whether shelter is waiting on its beds rung.</summary>
    private static async Task<List<LaborDiagnosis>> ReviewDiagnosesAsync(Journal st, ReviewRecord review, CancellationToken cancellationToken)
    {
        var world = new LaborWorld(review.Snapshot.Colony.Value, review.Snapshot.Load.Value, review.Snapshot.Map.Value);
        var result = new List<LaborDiagnosis>();
        foreach (var row in review.Progress)
        {
            var subject = new LaborSubject { World = world, ReviewTick = review.Tick.Value, Concern = row.Concern.Value };
            if (await OwnerMethodsAsync(st, review, row.Concern, cancellationToken).ConfigureAwait(false) is { } methods)
            {
                var (method, action, progress, beds) = await OpenWorkAsync(st, methods, cancellationToken).ConfigureAwait(false);
                subject = subject with { Method = method, Action = action, Progress = progress };
                if (row.Concern == PolicyFunctions.MaintainHousing)
                {
                    subject = subject with { ShelterBeds = beds };
                }
            }

            result.Add(StartupLabor.Diagnose(subject));
        }

        return result;
    }

    /// <summary>The open methods of the goal or Project the review binds to <paramref name="need"/>; null when it binds none or the row is gone.</summary>
    private static async Task<IReadOnlyList<Method>?> OwnerMethodsAsync(Journal st, ReviewRecord review, ConcernId need, CancellationToken cancellationToken)
    {
        if (!review.StandardFor(need, out var id))
        {
            return null;
        }

        try
        {
            var owner = await st.LoadStandardAsync(id, cancellationToken).ConfigureAwait(false);
            return owner.Standard.Id.Value.Length == 0 ? null : [.. owner.Methods];
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <summary>The first open action among methods: its method, action id, the progress view and whether the open rung is the shelter-beds one.</summary>
    private static async Task<(string Method, string Action, LaborProgress? Progress, bool Beds)> OpenWorkAsync(Journal st, IReadOnlyList<Method> methods, CancellationToken cancellationToken)
    {
        foreach (var m in methods)
        {
            PlanState plan;
            try
            {
                plan = await st.LoadPlanAsync(m.Plan, cancellationToken).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                continue;
            }

            foreach (var p in plan.Progress)
            {
                var v = p.View;
                if (v.Stage is Stage.Completed or Stage.Cancelled)
                {
                    continue;
                }

                IReadOnlyList<string> fresh = v.TryGetFreshHeldReason(out var reasons) ? [.. reasons.Select(r => Wire.Name(r))] : [];
                return (m.MethodId.Value, v.Action.Value, new LaborProgress(Wire.Name(v.Stage), v.Unresolved, fresh), m.MethodId.Value == ShelterBedsMethod);
            }
        }

        return ("", "", null, false);
    }

    /// <summary>The first tick at which the shelter goal's beds rung is completed for every colonist; once known it is never moved.</summary>
    private static async Task<long?> ShelterRecoveryAsync(Journal st, ReviewRecord review, long? known, CancellationToken cancellationToken)
    {
        if (known is not null)
        {
            return known;
        }

        foreach (var b in review.Standards)
        {
            if (b.Concern != PolicyFunctions.MaintainHousing)
            {
                continue;
            }

            StandardState state;
            try
            {
                state = await st.LoadStandardAsync(b.Standard, cancellationToken).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                return known;
            }

            foreach (var m in state.Methods)
            {
                if (m.MethodId.Value != ShelterBedsMethod)
                {
                    continue;
                }

                PlanState plan;
                try
                {
                    plan = await st.LoadPlanAsync(m.Plan, cancellationToken).ConfigureAwait(false);
                }
                catch (NotFoundException)
                {
                    continue;
                }

                var done = plan.Progress.Count > 0;
                long last = 0;
                foreach (var p in plan.Progress)
                {
                    var v = p.View;
                    done = done && v.Stage == Stage.Completed;
                    last = Math.Max(last, v.Tick.Value);
                }

                if (done)
                {
                    return last;
                }
            }
        }

        return known;
    }

    /// <summary>The first tick a shell plan completed every one of its actions: the enclosure the comparison reports.</summary>
    private static async Task<long?> FirstEnclosureAsync(Journal st, long? known, CancellationToken cancellationToken)
    {
        if (known is not null)
        {
            return known;
        }

        var plans = await st.LoadPlansAsync(cancellationToken).ConfigureAwait(false);
        foreach (var plan in plans)
        {
            if (!StorePackage.IsRoomShellMethod(plan.Method) || plan.Progress.Count == 0)
            {
                continue;
            }

            var done = true;
            long last = 0;
            foreach (var p in plan.Progress)
            {
                var v = p.View;
                done = done && v.Stage == Stage.Completed;
                last = Math.Max(last, v.Tick.Value);
            }

            if (done)
            {
                return last;
            }
        }

        return known;
    }

    /// <summary>
    /// The window's idle accounting from the pawn censuses the service's own reviews already took, as its OTLP log ring
    /// holds them. No sample is taken for the diagnosis itself.
    /// </summary>
    private static IdleAccount IdleAccount(string logPath)
    {
        IReadOnlyList<LogRow> rows;
        try
        {
            rows = ServiceLogs.ReadLogs(logPath);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new IdleAccount();
        }

        var samples = new List<PawnSample>();
        foreach (var row in rows)
        {
            if (row.Kind != "native_call" || NativeReplyFailed(row) || (row.Payload.GetValueOrDefault("tool") as string) != "rimgovernor/observations_list_pawns")
            {
                continue;
            }

            if (!row.HasTick || RecordedReply(row.Payload) is not { } reply)
            {
                continue;
            }

            samples.AddRange(StartupLabor.SamplesFromPawnSnapshot(row.Tick, reply, false));
        }

        return StartupLabor.Accumulate(samples, 0);
    }

    // Whether a native_call row records a failure: ok false or an error text.
    private static bool NativeReplyFailed(LogRow row) =>
        (row.Payload.GetValueOrDefault("ok") is false) || ((row.Payload.GetValueOrDefault("error") as string) ?? "").Length > 0;

    // A recorded native_call row's reply as a JSON object: the JSON text its result.payload holds.
    private static JsonObject? RecordedReply(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.GetValueOrDefault("result") is not IReadOnlyDictionary<string, object?> result || result.GetValueOrDefault("payload") is not string text)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
