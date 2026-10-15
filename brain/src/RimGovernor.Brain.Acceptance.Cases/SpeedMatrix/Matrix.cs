using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Telemetry.Otlp;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// What the matrix cases differ by (Go <c>speedmatrix.profile</c>): the stage the fixture prepares, the save it is kept
/// in, the tick target each row runs to and the rows compared. <c>speedmatrix/plain</c> is the whole speed sweep on a quiet
/// flat debug colony; <c>speedmatrix/observations</c> is the rendered observation baseline on the tribal8 colony.
/// </summary>
internal sealed record SpeedmatrixProfile
{
    public int Items { get; init; }

    public int Segments { get; init; }

    public ulong Ticks { get; init; }

    public string Save { get; init; } = "";

    public string Speeds { get; init; } = "";

    /// <summary>Requires every row to carry the frame and observation accounts, rejecting an empty report.</summary>
    public bool Observations { get; init; }
}

/// <summary>
/// Runs identical staged work at each clock speed and checks equal game outcomes (Go <c>cases/speedmatrix</c>).
/// Governor-off measures the simulation ceiling outside that outcome comparison. Governed rows report wall TPS
/// including holds, native paused fraction, hold counts by reason, resume latency and effective speed; sampled pause ratios are diagnostic only. The throughput
/// fixture provides construction and hauling work in a quiet colony with frozen needs.
/// </summary>
internal sealed partial class SpeedmatrixRun
{
    internal const string PrepareTool = "test/throughput_prepare";
    internal const string ControlTool = "test/throughput_control";
    internal const string StageSave = "RimGovernor-speedmatrix-stage";

    // Ticks is the game-tick target each speed runs to after resume (the serve-side tick must advance by this much) unless
    // the stage runs out of work first, which every speed does: the controller's cadence is wall-bound, so the ticks the
    // stage's work takes grow with the pace, from ~2,300 at Normal to ~20,000 at boosted Ultrafast.
    internal const ulong Ticks = 30000;

    // Items and Segments size the stage: Steel stacks spawned and wall segments laid out.
    internal const int Items = 4;
    internal const int Segments = 6;

    // Tolerance is the allowed spread of each pawn-outcome postcondition across speeds.
    private const int Tolerance = 1;

    // MaxPausedFraction and MinUltrafastTpsRatio are the clock-throughput expectations (0.5 and 2); at zero they are
    // reported, not enforced.
    private const double MaxPausedFraction = 0;
    private const double MinUltrafastTpsRatio = 0;

    // MaxLiveStepReads is the step-cost bound: a step planning under a running clock reads its one review bundle plus
    // at most the families an event within the step made stale. The step's wall time is reported, not bounded.
    private const double MaxLiveStepReads = 5;

    private readonly SpeedmatrixProfile _profile;
    private readonly ISession _session;
    private readonly Report _report;
    private readonly IReadOnlyList<SpeedCase> _cases;
    private readonly List<JsonObject> _sites = [];
    private readonly List<SpeedOutcome> _outcomes = [];
    private readonly List<JsonObject> _rows = [];

    // The fixture's cell lists, in the "x:z" form test/throughput_control reads back.
    private string _storage = "";
    private string _walls = "";

    private SpeedmatrixRun(SpeedmatrixProfile profile, ISession session, IReadOnlyList<SpeedCase> cases)
    {
        _profile = profile;
        _session = session;
        _report = session.Report;
        _cases = cases;
    }

    internal static SpeedmatrixProfile PlainProfile { get; } = new()
    {
        Items = Items,
        Segments = Segments,
        Ticks = Ticks,
        Save = StageSave,
        Speeds = SpeedMatrix.DefaultSpeedMatrix,
    };

    /// <summary><c>speedmatrix/plain</c>.</summary>
    internal static AcceptanceCase PlainCase() => new()
    {
        Name = "speedmatrix/plain",
        Scope = "Speed matrix (#111): one staged colony reloaded per clock speed under rimgovernor serve with an "
            + "identical tick target; wall TPS, paused fraction, steps, reads/step, parent hits, hold counts by reason, resume latency and "
            + "effective speed per speed; pawn outcomes equal within a tolerance.",
        Start = new FixtureStart
        {
            Op = PrepareTool,
            Args = new JsonObject { ["itemCount"] = Items, ["wallSegments"] = Segments },
            On = new DebugStart { Size = new DebugStartSize { Flat = true } },
        },

        // The stage is hauling and wall building inside the home area; the wild map is unobserved.
        QuietWorld = true,
        Service = true,
        Budget = AcceptanceCase.MaxBudget,
        Crew = new Crew { Size = 3 },
        Matrix = true,
        Run = RunBody(PlainProfile),
    };

    /// <summary>The matrix body for one profile.</summary>
    internal static Func<ISession, CancellationToken, Task> RunBody(SpeedmatrixProfile profile) =>
        (s, cancellationToken) => RunMatrixAsync(s, profile, cancellationToken);

    private static async Task RunMatrixAsync(ISession s, SpeedmatrixProfile p, CancellationToken cancellationToken)
    {
        // RIMGOVERNOR_SPEED_MATRIX narrows the rows for a targeted comparison (a rendered "uncapped,player" pair); the
        // default is every row.
        var spec = Environment.GetEnvironmentVariable("RIMGOVERNOR_SPEED_MATRIX") ?? "";
        if (spec.Length == 0)
        {
            spec = p.Speeds;
        }

        var speedCases = SpeedMatrix.ParseSpeedCases(spec);
        var m = new SpeedmatrixRun(p, s, speedCases);
        m._report["speeds_spec"] = spec;
        m._report["speeds"] = new JsonArray([.. speedCases.Select(c => (JsonNode?)c.ToJson())]);
        m._report["tick_target"] = p.Ticks;

        // What the baseline was measured on: the revision, the world and mod set, the host, the launch's resolution and
        // the measured interval's bounds. A row's own wall_seconds and frames block complete it.
        m._report["provenance"] = m.Provenance();
        m._report["tolerance"] = Tolerance;
        m._report["max_paused_fraction"] = MaxPausedFraction;
        m._report["max_live_step_reads"] = MaxLiveStepReads;
        m._report["min_ultrafast_tps_ratio"] = MinUltrafastTpsRatio;
        if (!Dyn.Contains(s.Names, ControlTool))
        {
            throw new AcceptanceException($"missing {ControlTool} in discovery; rebuild the native mod with -Fixture ThroughputFixture");
        }

        try
        {
            await m.StageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AcceptanceException or IOException)
        {
            throw new AcceptanceException("stage: " + e.Message, e);
        }

        foreach (var c in m._cases)
        {
            SpeedOutcome? outcome = null;
            try
            {
                if (c.GovernorOff)
                {
                    await m.RunGovernorOffAsync(c, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    outcome = await m.RunCaseAsync(c, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is AcceptanceException or IOException)
            {
                throw new AcceptanceException($"{c.Name}: {e.Message}", e);
            }

            if (!c.Compared || outcome is null)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"DONE {c.Name}: no controller"));
                continue;
            }

            m._outcomes.Add(outcome);
            m._report["outcomes"] = new JsonArray([.. m._outcomes.Select(o => (JsonNode?)o.ToJson())]);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"DONE {c.Name}: stored={outcome.StoredUnits}/{outcome.StoredUnits + outcome.LooseUnits} walls={outcome.WallsBuilt} colonists={outcome.HealthyColonists} unsuccessful={outcome.UnsuccessfulStages}"));
        }

        // Every required row is present and advanced the tick before any comparator, which accept an empty matrix, sees them.
        var metrics = SpeedMetrics.FromRows(m._rows);
        var rowProblems = SpeedMatrix.SpeedRowProblems(m._cases, m._outcomes, metrics);
        if (rowProblems.Count > 0)
        {
            m._report["row_problems"] = Problems(rowProblems);
            throw new AcceptanceException("speed rows missing or empty: " + string.Join("; ", rowProblems));
        }

        if (p.Observations)
        {
            var observationProblems = ObservationRowProblems(m._rows);
            if (observationProblems.Count > 0)
            {
                m._report["row_problems"] = Problems(observationProblems);
                throw new AcceptanceException("observation report incomplete: " + string.Join("; ", observationProblems));
            }
        }

        var ratios = SpeedMatrix.CeilingRatios(metrics);
        if (ratios.Count > 0)
        {
            m._report["ceiling_ratios"] = new JsonArray([.. ratios.Select(r => (JsonNode?)r.ToJson())]);
        }

        var outcomeProblems = SpeedMatrix.CompareOutcomes(m._outcomes, Tolerance);
        if (outcomeProblems.Count > 0)
        {
            m._report["outcome_problems"] = Problems(outcomeProblems);
            throw new AcceptanceException("pawn outcomes differ across speeds: " + string.Join("; ", outcomeProblems));
        }

        // The stage must have been reachable: a matrix where nothing was hauled or built agrees trivially and proves nothing.
        foreach (var o in m._outcomes)
        {
            if (o.StoredUnits == 0 && o.WallsBuilt == 0)
            {
                throw new AcceptanceException($"{o.Case}: nothing was hauled or built within the tick target; raise ticks or check the stage");
            }
        }

        var metricProblems = SpeedMatrix.CheckSpeedMetrics(metrics, MaxPausedFraction, MinUltrafastTpsRatio);
        if (metricProblems.Count > 0)
        {
            m._report["metric_problems"] = Problems(metricProblems);
            throw new AcceptanceException("clock throughput short of the thresholds: " + string.Join("; ", metricProblems));
        }

        var liveProblems = SpeedMatrix.CheckLiveStepCost(metrics, MaxLiveStepReads);
        if (liveProblems.Count > 0)
        {
            m._report["live_step_problems"] = Problems(liveProblems);
            throw new AcceptanceException("live steps over the read bound: " + string.Join("; ", liveProblems));
        }

        // Go checkStartupLog: the run's last assertion (no native error in the game's startup log) is a no-op there.
    }

    private static JsonArray Problems(IEnumerable<string> problems) => new([.. problems.Select(p => (JsonNode?)p)]);

    // A harness over the session's game whose evidence goes under another directory (Go set Harness.Output).
    private Harness HarnessFor(string output)
    {
        var shared = _session.Harness;
        return new Harness(shared.Transport, output) { Tools = shared.Tools };
    }

    /// <summary>Takes the fixture's layout from the prepared colony and saves it as the stage save for every case to reload.</summary>
    private async Task StageAsync(CancellationToken cancellationToken)
    {
        var prepared = _session.Prepared ?? [];
        var stageOutput = Path.Combine(_session.Config.Output, "stage");
        _ = Directory.CreateDirectory(stageOutput);
        var h = HarnessFor(stageOutput);
        _storage = CellList(prepared["storageCells"]);
        _walls = CellList(prepared["sites"]);
        foreach (var raw in prepared["sites"].AsSlice())
        {
            _sites.Add(raw.AsMap() ?? []);
        }

        if (_sites.Count != _profile.Segments)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"stage laid out {_sites.Count} wall sites, want {_profile.Segments}"));
        }

        // Frozen needs do not survive a reload (the op is per game), so the save carries the stage only; each case
        // freezes again after loading.
        var started = DateTime.UtcNow;
        _ = await h.CallAsync("save-stage", "rimgovernor/save_game", new JsonObject { ["saveName"] = _profile.Save }, cancellationToken).ConfigureAwait(false);
        await WaitSavedAsync(started, cancellationToken).ConfigureAwait(false);
    }

    // Waits for the game to finish writing the stage save into the active profile's Saves directory (headless-profile or profile).
    private async Task WaitSavedAsync(DateTime started, CancellationToken cancellationToken)
    {
        var root = _session.Config.Root;
        var deadline = started.AddSeconds(90);
        while (true)
        {
            foreach (var dir in new[] { "headless-profile", "profile" })
            {
                var candidate = Path.Combine(root, dir, "Saves", _profile.Save + ".rws");
                var info = new FileInfo(candidate);
                if (info.Exists && info.Length > 0 && info.LastWriteTimeUtc >= started.AddSeconds(-1))
                {
                    // A save the game is still writing grows; require it stable.
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    var again = new FileInfo(candidate);
                    if (again.Exists && again.Length == info.Length)
                    {
                        _report["stage_save"] = candidate;
                        return;
                    }
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AcceptanceException($"stage save {_profile.Save} did not appear under {root} within 90s");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class StateSource(ServiceProcess service) : IServiceStateSource
    {
        public async Task<int> GetStateAsync(CancellationToken cancellationToken) =>
            (await service.ApiAsync("GET", "/api/state", null, "", cancellationToken).ConfigureAwait(false)).Status;
    }

    // Reloads the stage, hands the game to one serve process at the case's speed, waits out the tick target and reads
    // the outcome back.
    private async Task<SpeedOutcome> RunCaseAsync(SpeedCase c, CancellationToken cancellationToken)
    {
        var output = Path.Combine(_session.Config.Output, c.Name);
        _ = Directory.CreateDirectory(output);
        var report = Report.Create("speed case " + c.Name, _session.Config.Headless);
        report["case"] = c.ToJson();
        try
        {
            var outcome = await RunCaseCoreAsync(c, output, report, cancellationToken).ConfigureAwait(false);
            report["passed"] = true;
            return outcome;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            report["error"] = e.Message;
            throw;
        }
        finally
        {
            _ = report.Finalize(output);
        }
    }

    private async Task<SpeedOutcome> RunCaseCoreAsync(SpeedCase c, string output, Report report, CancellationToken cancellationToken)
    {
        ServiceProcess? service = null;
        Journal? journal = null;
        ObservationReaders? readers = null;
        Func<Task<JsonObject>>? stopKeepAlive = null;
        try
        {
            _ = _session.Reattach();
            var h = HarnessFor(output);
            _ = await h.CallAsync(
                "load-stage",
                "rimgovernor/load_game_ready",
                new JsonObject { ["saveName"] = _profile.Save, ["readiness"] = "visual", ["timeoutMs"] = 90000, ["ignoreModCompatibility"] = false },
                cancellationToken).ConfigureAwait(false);
            _ = await h.CallAsync("pause", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
            var identity = await ReadIdentityAsync(h, cancellationToken).ConfigureAwait(false);
            report["frozen_needs"] = (await Needs.FreezeNeedsAsync(h, null, [], cancellationToken).ConfigureAwait(false)).DeepClone();
            var before = await ControlAsync(h, identity, "control-before", cancellationToken).ConfigureAwait(false);
            report["control_before"] = before.DeepClone();
            if (before["storedUnits"].AsNumber() != 0 || before["wallsBuilt"].AsNumber() != 0 || before["wallBlueprints"].AsNumber() != 0)
            {
                throw new AcceptanceException("stage is not fresh after reload: " + before.ToJson());
            }

            var startTick = (ulong)before["tick"].AsNumber();

            // The brain runs in the game process, so the serve flag the case adds (--clock-test-acceleration)
            // cannot be applied; the launch records them under its env entries.
            var serveArgs = c.ServeArgs();
            service = await _session.LaunchAsync(
                new ServiceLaunch
                {
                    Families = ["work"],
                    Env = serveArgs.Count == 0 ? [] : ["serve_args=" + string.Join(' ', serveArgs)],
                    ClockSpeed = c.Speed,
                    Output = output,
                    Report = report,
                },
                cancellationToken).ConfigureAwait(false);
            var token = await service.SessionTokenAsync(cancellationToken).ConfigureAwait(false);
            var attached = await service.WaitAttachedAsync(identity, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            report["service_state_attached"] = attached.DeepClone();
            var prefix = "speedmatrix-" + c.Name.ToLowerInvariant() + "-" + RandomSuffix();
            journal = await ServiceJournal.OpenWithRetryAsync(_session.Config, cancellationToken).ConfigureAwait(false);

            // The kept game's journal outlives a row, so the previous row's review (and its tick) is still in it.
            var staleRevision = (await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false)).Revision;
            var planIds = await SubmitWallsAsync(journal, prefix, identity, report, cancellationToken).ConfigureAwait(false);
            var rootPlanId = await service.ResumeAsync(prefix, identity, token, report, cancellationToken).ConfigureAwait(false);
            report["root_plan"] = rootPlanId;

            // The observation-load row: concurrent state readers.
            if (c.ObservationLoad)
            {
                readers = ObservationReaders.Start(new StateSource(service), SpeedMatrix.ObservationLoadReaders, SpeedMatrix.ObservationLoadInterval, cancellationToken);
            }

            var resumedAt = DateTimeOffset.UtcNow;
            var keepAlive = new AuthorityKeepAlive(service, prefix, identity, token);
            stopKeepAlive = keepAlive.Start(cancellationToken);

            _ = await ServiceJournal.WaitRoundsAsync(service, journal, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);

            // The target is game time: the wait ends once the tick the service last observed (its OTLP log ring's newest
            // clock sample; the routine review's tick only moves once per full step) has
            // advanced by the target past the reload tick, or earlier once the stage has no work left for the clock to
            // admit. The signature is the tick itself, so a clock that stops advancing with work pending stalls the wait.
            ulong lastTick = 0;
            var workDone = false;
            var logPath = ServiceLogs.ServiceLogsPath(output);
            Exception? waitErr = null;
            try
            {
                await new Wait { Stall = Wait.StallBudget(), Interval = TimeSpan.FromSeconds(2), Terminal = service.Exited }.WaitProgressAsync(
                    async ct =>
                    {
                        var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
                        lastTick = review.Revision > staleRevision ? (ulong)review.Tick.Value : startTick;
                        if (TryReadTimeline(logPath) is { } rows && PhaseSummary.Summarize(rows).Clock.LastTick is var observed && observed > (long)lastTick)
                        {
                            lastTick = (ulong)observed;
                        }

                        if (lastTick >= startTick + _profile.Ticks)
                        {
                            return (Wait.Signature(lastTick), true);
                        }

                        workDone = await StageWorkDoneAsync(journal, planIds, ct).ConfigureAwait(false);
                        return (Wait.Signature(lastTick), workDone);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (WaitError e)
            {
                waitErr = e;
            }

            var wallSeconds = (DateTimeOffset.UtcNow - resumedAt).TotalSeconds;
            report["wait"] = new JsonObject { ["start_tick"] = startTick, ["last_tick"] = lastTick, ["wall_seconds"] = wallSeconds, ["work_done"] = workDone };
            if (waitErr is not null)
            {
                try
                {
                    var final = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
                    report["rounds_review_at_failure"] = new JsonObject { ["revision"] = final.Revision, ["tick"] = final.Tick.Value, ["standards"] = final.Standards.Count };
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _ = e;
                }

                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"tick target wait (start {startTick}, last {lastTick}, want +{_profile.Ticks}): {waitErr.Message}"), waitErr);
            }

            var (unsuccessful, planCount) = await CountUnsuccessfulAsync(journal, planIds, cancellationToken).ConfigureAwait(false);
            report["plans_inspected"] = planCount;
            journal.Dispose();
            journal = null;
            JsonObject? readerSummary = null;
            if (readers is not null)
            {
                readerSummary = await readers.StopAsync().ConfigureAwait(false);
                report["readers"] = readerSummary.DeepClone();
                readers = null;
            }

            report["authority_reacquisitions"] = (await stopKeepAlive().ConfigureAwait(false)).DeepClone();
            stopKeepAlive = null;
            _ = await service.StopAsync().ConfigureAwait(false);
            var timeline = OtlpTimeline.Read(ServiceLogs.ServiceLogsPath(output));
            var phases = PhaseSummary.Summarize(timeline);
            var holds = StopSummaries.SummarizeHolds(timeline, resumedAt.ToUnixTimeMilliseconds());
            report["holds"] = holds.ToJson();
            var metrics = CaseMetrics(c, phases, holds, startTick, lastTick, wallSeconds);

            // The observation capture account and the frame recorder, from the same recording the clock and step phases come from.
            foreach (var (key, value) in ObservationRow(phases.Observation, phases.Frames))
            {
                metrics[key] = value?.DeepClone();
            }

            if (readerSummary is not null)
            {
                metrics["readers"] = readerSummary.DeepClone();
                var readerProblems = ObservationReaders.ReaderProblems(readerSummary);
                if (readerProblems.Count > 0)
                {
                    throw new AcceptanceException("observation-load row: " + string.Join("; ", readerProblems));
                }
            }

            report["metrics"] = metrics.DeepClone();
            AppendMetrics(metrics);

            // Independent native read after the service released the slot.
            _ = _session.Reattach();
            h = HarnessFor(output);
            _ = await h.CallAsync("pause-after", "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);

            // The service was stopped, not shut down: its authority grant lingers until its authority is released, and the
            // next reload would carry it.
            if (await Authority.ReleaseAuthorityAsync(h, identity, cancellationToken).ConfigureAwait(false) is { } revoked)
            {
                report["authority_released"] = revoked.DeepClone();
            }

            var after = await ControlAsync(h, identity, "control-after", cancellationToken).ConfigureAwait(false);
            report["control_after"] = after.DeepClone();
            var pawnsReply = await h.WireAsync(
                "pawns-after",
                "observations_list_pawns",
                new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["filter"] = new JsonObject { ["colonist"] = true } },
                cancellationToken).ConfigureAwait(false);
            ClockFixture.RequireHealthyColonists(pawnsReply);
            var (_, observedPawns) = Harness.Outcome(pawnsReply, "observed");
            var outcome = SpeedMatrix.OutcomeFromControl(c.Name, after) with
            {
                HealthyColonists = observedPawns["pawns"].AsSlice().Count,
                UnsuccessfulStages = unsuccessful,
            };
            report["outcome"] = outcome.ToJson();
            if (c.Player)
            {
                // Player acceleration's bounds, checked on the row's own recording once the outcome above has been read.
                var problems = SpeedMatrix.PlayerRowProblems(new PlayerRow
                {
                    HazardGaps = phases.Clock.NativeHazardGaps,
                    PacedFrames = phases.Clock.NativePacedFrames,
                    OverBudget = phases.Clock.NativePacedOverBudget,
                    LastPacingReason = SpeedMatrix.LastPacingReason(timeline),
                    Dispatch = phases.Observation.Queue,
                });
                if (problems.Count > 0)
                {
                    throw new AcceptanceException("player row: " + string.Join("; ", problems));
                }
            }

            return outcome;
        }
        finally
        {
            if (readers is not null)
            {
                report["readers"] = (await readers.StopAsync().ConfigureAwait(false)).DeepClone();
            }

            if (stopKeepAlive is not null)
            {
                report["authority_reacquisitions"] = (await stopKeepAlive().ConfigureAwait(false)).DeepClone();
            }

            journal?.Dispose();
            if (service is not null)
            {
                _ = await service.StopAsync().ConfigureAwait(false);
            }
        }
    }

    // The service's OTLP log ring while it is still being written: a torn read is not a sample.
    private static List<TimelineRecord>? TryReadTimeline(string path)
    {
        try
        {
            return OtlpTimeline.Read(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _ = e;
            return null;
        }
    }

    // Reads the stage's counters through test/throughput_control.
    private async Task<JsonObject> ControlAsync(Harness h, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync(
            label,
            ControlTool,
            new JsonObject
            {
                ["colonyId"] = identity["colonyId"].AsString(),
                ["loadToken"] = identity["loadToken"].AsString(),
                ["mapId"] = (int)identity["mapId"].AsNumber(),
                ["storageCells"] = _storage,
                ["wallCells"] = _walls,
            },
            cancellationToken).ConfigureAwait(false);
        return reply["success"].AsBool() == true ? reply : throw new AcceptanceException($"{ControlTool} refused: {reply.ToJson()}");
    }

    // Writes every staged wall segment into the service's journal as its own building plan (no player route exists) and
    // returns the plan ids. The service has not resumed yet, so it reads them as the world's standing work.
    private async Task<List<PlanId>> SubmitWallsAsync(Journal journal, string prefix, JsonObject identity, Report report, CancellationToken cancellationToken)
    {
        var world = new World(new ColonyId(identity["colonyId"].AsString()), new LoadId(identity["loadToken"].AsString()), new MapId((int)identity["mapId"].AsNumber()));
        var ids = new List<PlanId>();
        var submissions = new JsonArray();
        for (var i = 0; i < _sites.Count; i++)
        {
            var site = _sites[i];
            var label = i + 1;
            var rotationText = site["rotation"].AsString();
            if (!Enum.TryParse<Rotation>(rotationText.StartsWith("ROTATION_", StringComparison.Ordinal) ? rotationText["ROTATION_".Length..] : rotationText, ignoreCase: true, out var rotation))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"wall {label}: rotation: unknown rotation \"{rotationText}\""));
            }

            Submission submission;
            try
            {
                var building = new Building(site["defName"].AsString(), new Cell((int)site["x"].AsNumber(), (int)site["z"].AsNumber()), rotation, site["stuff"].AsString());
                (submission, _) = await journal.SubmitBuildingAsync(
                    new SubmissionRequest(string.Create(CultureInfo.InvariantCulture, $"{prefix}-wall-{label}"), world, building),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException e)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"wall {label}: {e.Message}"), e);
            }

            ids.Add(submission.Plan);
            submissions.Add(new JsonObject
            {
                ["requestId"] = submission.Request.RequestId,
                ["planId"] = submission.Plan.Value,
                ["revision"] = submission.Revision.Value,
            });
        }

        report["submissions"] = submissions;
        return ids;
    }

    // Whether the stage has run out of work: every wall plan has observed its building completed. After that the clock
    // scheduler refuses every run as no_work and the review tick no longer advances.
    private static async Task<bool> StageWorkDoneAsync(Journal journal, IReadOnlyList<PlanId> walls, CancellationToken cancellationToken)
    {
        foreach (var id in walls)
        {
            PlanState state;
            try
            {
                state = await journal.LoadPlanAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                return false;
            }

            if (state.Progress.Count == 0)
            {
                return false;
            }

            foreach (var progress in state.Progress)
            {
                if (progress.View.Stage != Stage.Completed)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Counts Unsuccessful action stages over the wall plans and every routine method the service dispatched.
    // LoadPlan sees retired plans; LoadPlans would hide them.
    private static async Task<(int Unsuccessful, int Plans)> CountUnsuccessfulAsync(Journal journal, IReadOnlyList<PlanId> walls, CancellationToken cancellationToken)
    {
        var seen = new HashSet<PlanId>();
        var ids = new List<PlanId>();
        foreach (var id in walls)
        {
            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        var review = await journal.LoadRoundsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var binding in review.Standards)
        {
            StandardState goal;
            try
            {
                goal = await journal.LoadStandardAsync(binding.Standard, cancellationToken).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                continue;
            }

            var methods = new List<Method>(goal.Methods);
            try
            {
                methods.AddRange(await journal.LoadMethodsAsync(binding.Standard, goal.Standard.Episode, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception e) when (e is DomainException or NotFoundException)
            {
                _ = e;
            }

            foreach (var method in methods)
            {
                if (seen.Add(method.Plan))
                {
                    ids.Add(method.Plan);
                }
            }
        }

        var unsuccessful = 0;
        foreach (var id in ids)
        {
            PlanState state;
            try
            {
                state = await journal.LoadPlanAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                continue;
            }

            foreach (var progress in state.Progress)
            {
                if (progress.View.Stage == Stage.Unsuccessful)
                {
                    unsuccessful++;
                }
            }
        }

        return (unsuccessful, ids.Count);
    }

    // The per-speed row the issue asks for.
    private static JsonObject CaseMetrics(SpeedCase c, PhaseSummary phases, HoldSummary holds, ulong startTick, ulong lastTick, double wallSeconds)
    {
        var steps = phases.Steps;
        var clock = phases.Clock;
        double readsPerStep = 0;
        double gateWaitMean = 0;
        if (steps.Steps > 0)
        {
            readsPerStep = (double)steps.Reads / steps.Steps;
            gateWaitMean = steps.GateWaitMs / steps.Steps;
        }

        // Time-weighted (ClockSample.PausedFraction): the count ratio over-represents pauses, when the service issues
        // most of its reads. A sampling diagnostic; native's own account of its hold and run time
        // (paused_fraction_native) is the headline.
        var pausedFraction = clock.PausedFraction();

        // Holds per 6000 ticks: how often the clock was held, however the row's length differs across speeds.
        double holdsPer6000 = 0;
        if (lastTick > startTick)
        {
            holdsPer6000 = (double)holds.Holds * 6000 / (lastTick - startTick);
        }

        var reasons = new JsonObject();
        foreach (var (reason, count) in holds.Reasons.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            reasons[reason] = count;
        }

        var runVerdicts = new JsonObject();
        foreach (var (verdict, count) in steps.RunVerdicts)
        {
            runVerdicts[verdict] = count;
        }

        var stepReasons = new JsonObject();
        foreach (var (reason, count) in steps.Reasons)
        {
            stepReasons[reason] = count;
        }

        var hazardGaps = new JsonArray([.. clock.NativeHazardGaps.Select(g => (JsonNode?)new JsonObject
        {
            ["hazard_class"] = g.HazardClass,
            ["bound_ticks"] = g.BoundTicks,
            ["max_tick_gap"] = g.MaxTickGap,
            ["hooked"] = g.Hooked,
        })]);
        var latencies = new JsonArray([.. holds.Latencies.Select(l => (JsonNode?)l.ToJson())]);
        return new JsonObject
        {
            ["case"] = c.Name,
            ["speed"] = c.Speed,
            ["test_acceleration"] = c.TestAcceleration,
            ["player"] = c.Player,

            // Player acceleration's frame account: frames paced, those over the frame budget and the widest frame's tick work.
            ["paced_frames"] = clock.NativePacedFrames,
            ["paced_frames_over_budget"] = clock.NativePacedOverBudget,
            ["max_paced_frame_ms"] = clock.NativeMaxPacedFrameMs,

            ["ticks_advanced"] = lastTick - startTick,
            ["wall_seconds"] = wallSeconds,
            ["wall_tps"] = clock.WallTps,

            // Effective speed: the share of the speed's own rate the row achieved, holds included (1 is a clock that was
            // never held and never slowed); native's own effective ticks per second, averaged over the status samples
            // that reported one, stands beside it.
            ["effective_speed"] = SpeedMatrix.EffectiveSpeed(clock.WallTps, c.Speed),
            ["target_tps"] = SpeedMatrix.TargetTps(c.Speed),
            ["native_effective_tps_mean"] = clock.EffectiveTpsMean(),
            ["paused_ms"] = clock.NativePausedMs,
            ["running_ms"] = clock.NativeRunningMs,
            ["paused_fraction_native"] = clock.PausedFractionNative(),
            ["native_pause_samples"] = clock.NativePauseSamples,

            // The supervisor's probe path split: hazard probe against the fact-change digests, session-cumulative
            // between the first and last status samples, and the detection gaps the session reported.
            ["probe_ms"] = clock.NativeProbeMs,
            ["digest_ms"] = clock.NativeDigestMs,
            ["digest_share"] = clock.DigestShare(),
            ["probes"] = clock.NativeProbes,
            ["digests"] = clock.NativeDigests,
            ["max_probe_tick_gap"] = clock.NativeMaxProbeTickGap,
            ["hazard_gaps"] = hazardGaps,
            ["paused_fraction"] = pausedFraction,
            ["paused_fraction_sampling"] = "status-sample ratio, a sampling diagnostic; paused_fraction_native is the measure",
            ["paused_samples"] = clock.PausedSamples,
            ["clock_samples"] = clock.ClockSamples,
            ["paused_sampled_seconds"] = clock.SampledSecs,
            ["steps"] = steps.Steps,
            ["reads_per_step"] = readsPerStep,

            // The step cost that bounds throughput at speed: the live steps apart from the cold and stopped ones, their
            // reads and wall, and the player-gate wait a step spent queued behind the Worker's dispatch step.
            ["live_steps"] = steps.LiveSteps,
            ["live_step_reads_mean"] = steps.LiveReadsPerStep(),
            ["max_live_step_reads"] = steps.MaxLiveReads,
            ["live_step_ms_mean"] = steps.LiveStepMs(),
            ["live_step_ms_max"] = steps.MaxLiveElapsedMs,
            ["step_ms_mean"] = steps.StepMs(),
            ["step_ms_max"] = steps.MaxElapsedMs,
            ["gate_wait_ms_mean"] = gateWaitMean,
            ["gate_wait_ms_max"] = steps.MaxGateWaitMs,

            // Hold metrics, from the native journal: holds by reason (letter_pause, review, coupled_order, ...), the
            // native time each hold lasted before a Run resumed it, and how long the controller took to see it.
            ["holds"] = holds.Holds,
            ["holds_per_6000_ticks"] = holdsPer6000,
            ["hold_reasons"] = reasons,
            ["resume_latency_ms_mean"] = holds.MeanResumeMs,
            ["resume_latency_ms_max"] = holds.MaxResumeMs,
            ["resume_samples"] = holds.ResumeCount,
            ["observe_latency_ms_mean"] = holds.MeanObserveMs,
            ["observe_latency_ms_max"] = holds.MaxObserveMs,

            // Per hold: detect_ticks, hold_ticks, observe_ms, resume_ms.
            ["hold_latencies"] = latencies,

            // The steps a hold woke and how long the wake took; the brain's own Run, Hold and refusal rows, and the
            // coupled orders that rode on them.
            ["hold_woken_steps"] = steps.HoldWoken,
            ["hold_wake_ms_mean"] = phases.MeanHoldWakeMs,
            ["hold_wake_ms_max"] = phases.MaxHoldWakeMs,
            ["run_verdicts"] = runVerdicts,
            ["coupled_runs"] = steps.CoupledRuns,
            ["coupled_orders"] = steps.CoupledOrders,

            // Live dispatch: steps by reason ("live" plans under a running clock), the worker's native runs made under
            // a running clock and the fraction native refused.
            ["step_reasons"] = stepReasons,
            ["dispatches"] = phases.Dispatch.Calls,
            ["live_dispatches"] = phases.Dispatch.Live,
            ["refused_dispatches"] = phases.Dispatch.Refused,
            ["live_refused_dispatches"] = phases.Dispatch.LiveRefused,
            ["refused_fraction"] = phases.Dispatch.RefusedFraction(),

            // Dispatches held on stale facts before native ran them.
            ["stale_facts_holds"] = phases.Dispatch.StaleHolds,
        };
    }

    // Records a metrics row on the matrix report and for the comparators (the report holds its own copy).
    private void AppendMetrics(JsonObject row)
    {
        _rows.Add(row);
        _report["speed_metrics"] = new JsonArray([.. _rows.Select(r => (JsonNode?)r.DeepClone())]);
    }

    // Renders fixture cell objects as the "x:z,x:z" list test/throughput_control parses.
    private static string CellList(JsonNode? value) =>
        string.Join(',', value.AsSlice().Select(raw =>
        {
            var cell = raw.AsMap();
            return string.Create(CultureInfo.InvariantCulture, $"{(int)cell?["x"].AsNumber()!}:{(int)cell["z"].AsNumber()}");
        }));

    private static string RandomSuffix() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}
