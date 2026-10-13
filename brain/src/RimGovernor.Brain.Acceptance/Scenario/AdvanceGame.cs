using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>An option to <see cref="AdvanceGame.RunAsync"/> that the call's settings are built from (Go <c>AdvanceOption</c>).</summary>
internal interface IAdvanceOptionApply : IScenarioAdvanceOption
{
    void Apply(AdvanceSettings settings);
}

/// <summary>The settings one AdvanceGame call runs with.</summary>
internal sealed class AdvanceSettings
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);

    public IReadOnlyList<(string Label, string LetterDef)> ExpectedLetters { get; set; } = [("Ancient danger", "ThreatBig")];

    public bool StrictLetters { get; set; }

    public IReadOnlyList<string> CombatTargets { get; set; } = [];
}

/// <summary>The AdvanceGame options (Go <c>WithTimeout</c>, <c>WithExpectedLetters</c>, <c>WithCombatTargets</c>).</summary>
public static class AdvanceOptions
{
    /// <summary>Bounds the whole advance, like advance_game's timeout= keyword.</summary>
    public static IScenarioAdvanceOption WithTimeout(TimeSpan timeout) => new TimeoutOption(timeout);

    /// <summary>
    /// Makes the window strict: only the listed (label, def) pairs may pause it and the acknowledged letter defs no
    /// longer apply. Pass no pairs for interruption acceptance, matching advance_game's expected_letters=().
    /// </summary>
    public static IScenarioAdvanceOption WithExpectedLetters(params (string Label, string LetterDef)[] letters) => new ExpectedLettersOption(letters);

    /// <summary>Requires the exact committed defense targets already on the runtime's CombatTargets, mirroring advance_game's combat_targets= keyword.</summary>
    public static IScenarioAdvanceOption WithCombatTargets(params string[] targets) => new CombatTargetsOption(targets);

    internal static void ApplyAll(IEnumerable<IScenarioAdvanceOption> options, AdvanceSettings settings)
    {
        foreach (var option in options)
        {
            if (option is IAdvanceOptionApply apply)
            {
                apply.Apply(settings);
            }
            else
            {
                throw new AcceptanceException($"unsupported advance option {option.GetType().Name}");
            }
        }
    }

    private sealed class TimeoutOption(TimeSpan timeout) : IAdvanceOptionApply
    {
        void IAdvanceOptionApply.Apply(AdvanceSettings settings) => settings.Timeout = timeout;
    }

    private sealed class ExpectedLettersOption((string Label, string LetterDef)[] letters) : IAdvanceOptionApply
    {
        void IAdvanceOptionApply.Apply(AdvanceSettings settings)
        {
            settings.ExpectedLetters = letters;
            settings.StrictLetters = true;
        }
    }

    private sealed class CombatTargetsOption(string[] targets) : IAdvanceOptionApply
    {
        void IAdvanceOptionApply.Apply(AdvanceSettings settings) => settings.CombatTargets = targets;
    }
}

/// <summary>
/// AdvanceGame: advances exactly <c>ticks</c> native ticks, acknowledging inspected fixture warnings and, unless the
/// window is strict, the informational letters in <see cref="ScenarioRuntime.AcknowledgedLetterDefs"/> (dismissed
/// through <see cref="ScenarioRuntime.DismissLetterTool"/> when the runtime's tools carry it, recorded under the
/// interruption either way). A combat window also continues past a colonist_health stop while every colonist still
/// stands. It never clears player holds or retries game orders; an unexpected injury, new threat, or drifted identity
/// raises <see cref="ScenarioInterruptedException"/> with the failing evidence retained on the runtime's report.
/// </summary>
public static class AdvanceGame
{
    /// <summary>
    /// The bound on the colonist_health stops one combat advance continues past; the native guard needs a fresh drop
    /// of healthDropFraction per window, so this many is a colonist near downing.
    /// </summary>
    private const int CombatHealthStopsPerAdvance = 8;

    /// <summary>
    /// The bound on the pacing stops (continuable stops) one advance continues past, per reason. Each is a clean stop
    /// that consumed game time, so a healthy window sees a handful; a watch that re-fires every tick would otherwise
    /// spin the tick budget out one stop at a time until the timeout, with the reason buried in the evidence.
    /// </summary>
    private const int PacingStopsPerAdvance = 64;

    /// <summary>
    /// Whether a clock stop is pacing rather than an interruption. The set is global, not a case field beside
    /// Letters: notification_batch is the native batching a pending notification, which says nothing about the window
    /// in any case, and hostile is the threat watch firing, which is the situation under test exactly when the window
    /// commits combat targets and an unexpected threat everywhere else. Letters is per-case because which letters are
    /// expected is; which stop reasons are pacing is not.
    /// </summary>
    internal static bool ContinuableStop(string reason, bool combat) => reason switch
    {
        "notification_batch" => true,
        "hostile" => combat,
        _ => false,
    };

    /// <summary>Records one continuable stop and reports whether the reason is still within the per-advance bound.</summary>
    internal static bool CountPacingStop(Dictionary<string, int> counts, string reason)
    {
        counts[reason] = counts.GetValueOrDefault(reason) + 1;
        return counts[reason] <= PacingStopsPerAdvance;
    }

    public static async Task<JsonObject> RunAsync(ScenarioRuntime rt, ulong ticks, IEnumerable<IScenarioAdvanceOption>? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rt);
        if (ticks is < 1 or > 1800000)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"ticks must be 1..1800000, got {ticks}"));
        }

        var settings = new AdvanceSettings();
        AdvanceOptions.ApplyAll(options ?? [], settings);
        if (settings.CombatTargets.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in settings.CombatTargets)
            {
                if (target.Length == 0 || !seen.Add(target))
                {
                    throw new AcceptanceException("combat waits require distinct non-empty targets");
                }
            }

            if (!seen.SetEquals(rt.CombatTargets))
            {
                throw new AcceptanceException("combat waits require the exact committed defense targets");
            }
        }

        var evidence = new JsonObject { ["ticks"] = ticks, ["windows"] = new JsonArray(), ["interruptions"] = new JsonArray() };
        rt.ReportArray("simulation").Add(evidence);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(settings.Timeout);
        JsonObject? identity = null;
        try
        {
            try
            {
                identity = await rt.IdentityNowAsync(timeoutSource.Token).ConfigureAwait(false);
                return await LoopAsync(rt, ticks, settings, evidence, identity, timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException e) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new AcceptanceException("advance_game timed out after " + settings.Timeout, e);
            }
        }
        catch (Exception e)
        {
            if (!evidence.ContainsKey("failure"))
            {
                evidence["failure"] = e.Message;
            }

            await CleanupAsync(rt, evidence, identity).ConfigureAwait(false);
            throw;
        }
    }

    private static void Require(JsonObject evidence, bool condition, string reason)
    {
        if (!condition)
        {
            evidence["failure"] = reason;
            throw new ScenarioInterruptedException(reason);
        }
    }

    // The clock is left paused when the advance failed: a window still running would keep consuming game time under
    // the failing evidence. It runs on its own deadline, not the advance's, which may be what ended.
    private static async Task CleanupAsync(ScenarioRuntime rt, JsonObject evidence, JsonObject? identity)
    {
        if (identity is null)
        {
            return;
        }

        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = cleanup.Token;
        var supervisor = rt.Clock;
        try
        {
            var now = await rt.IdentityNowAsync(token).ConfigureAwait(false);
            if (!Dyn.DeepEqual(now, identity))
            {
                return;
            }

            var current = await supervisor.CallAsync("status", null, token).ConfigureAwait(false);
            if (current["active"].AsBool() != true || current["owner"].AsString() != supervisor.Owner)
            {
                return;
            }

            try
            {
                var paused = await supervisor.CallAsync("pause", new JsonObject { ["owner"] = supervisor.Owner, ["epoch"] = current["epoch"]?.DeepClone() }, token).ConfigureAwait(false);
                evidence["cleanup"] = paused;
            }
            catch (AcceptanceException e)
            {
                evidence["cleanupFailure"] = e.Message;
            }
        }
        catch (Exception e) when (e is AcceptanceException or OperationCanceledException)
        {
            // Best effort: the failure being reported is the advance's own.
        }
    }

    private static async Task<JsonObject> LoopAsync(ScenarioRuntime rt, ulong ticks, AdvanceSettings settings, JsonObject evidence, JsonObject identity, CancellationToken cancellationToken)
    {
        var supervisor = rt.Clock;
        var combat = settings.CombatTargets.Count > 0;
        JsonObject? state = null;
        ulong previousTick = 0;
        var havePreviousTick = false;
        var remaining = ticks;
        var seenLetters = new HashSet<string>(StringComparer.Ordinal);
        var healthStops = 0;
        var pacingStops = new Dictionary<string, int>(StringComparer.Ordinal);
        var windows = (JsonArray)evidence["windows"]!;
        var interruptions = (JsonArray)evidence["interruptions"]!;

        while (remaining > 0)
        {
            var now = await rt.IdentityNowAsync(cancellationToken).ConfigureAwait(false);
            Require(evidence, Dyn.DeepEqual(now, identity), "Native identity changed");
            var speed = supervisor.TestAcceleration ? "Ultrafast" : "Superfast";
            var started = await supervisor.ChangeAsync(speed, remaining, cancellationToken).ConfigureAwait(false);
            var startTick = started["startTick"].AsUInt64() ?? 0;
            Require(evidence, !havePreviousTick || startTick == previousTick, "Native ticks changed between windows");
            var cursor = started["newestCursor"].AsUInt64() ?? 0;

            // Poll (only reachable below via the tick_budget completion path) reads from the supervisor's cursor,
            // which starts at zero; without this, a supervisor whose owned epoch began after other activity already
            // wrote retained events (e.g. authority acquire/revoke churn before the clock ever started) would poll
            // from before those events and be refused with FAILURE_CODE_UNAVAILABLE ("retained legacy event lacks
            // canonical ownership"). newestCursor is exactly the exclusive pre-dispatch cursor Start already computed
            // to exclude that noise, so seed the cursor with it (monotonically, never regressing).
            supervisor.SeekEvents(cursor);
            while (true)
            {
                state = await supervisor.CallAsync("status", null, cancellationToken).ConfigureAwait(false);
                if (state["active"].AsBool() != true)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            windows.Add(state.DeepClone());
            now = await rt.IdentityNowAsync(cancellationToken).ConfigureAwait(false);
            Require(evidence, Dyn.DeepEqual(now, identity), "Native identity changed");
            var startedEpoch = started["epoch"].AsUInt64() ?? 0;
            var stateEpoch = state["epoch"].AsUInt64() ?? 0;
            Require(evidence, state["owner"].AsString() == supervisor.Owner && stateEpoch == startedEpoch, "Clock ownership changed");
            Require(evidence, state["pauseVerified"].AsBool() == true, "Native pause unverified");
            var lastTick = state["lastTick"].AsUInt64() ?? 0;
            var advanced = lastTick - startTick;
            Require(evidence, lastTick >= startTick && advanced <= remaining, "Invalid native tick progress");
            remaining -= advanced;
            previousTick = lastTick;
            havePreviousTick = true;
            var stopReason = state["stopReason"].AsString();
            if (stopReason == "tick_budget")
            {
                Require(evidence, remaining == 0, "Native tick budget ended early");
                if (started["freshEpoch"].AsBool() != true)
                {
                    var events = await supervisor.PollAsync(cancellationToken).ConfigureAwait(false);
                    rt.AddClockEvents(events);
                    rt.ReceiveClockEvents();
                }

                Require(evidence, supervisor.Hold.Length == 0, "External clock hold");
                evidence["completed"] = true;
                return state;
            }

            var detail = new JsonObject { ["clock"] = state.DeepClone(), ["events"] = new JsonArray() };
            interruptions.Add(detail);
            if (stopReason == "colonist_health" && combat)
            {
                // A colonist ordered to engage the committed targets may take the wounds the combat policy flags; the
                // stop is recorded and the next window continues so the fight can resolve. Each window baselines
                // health afresh, so a repeat needs a further drop; the cap keeps a pawn who bleeds out under the
                // guard from consuming the whole tick budget window by window. A downed or dead colonist still
                // interrupts.
                healthStops++;
                detail["combatHealth"] = true;
                Require(evidence, healthStops <= CombatHealthStopsPerAdvance, "Combat health stops exhausted");
                var (combatStatus, combatComplete) = await ScenarioRuntime.ReadSafetyStatusAsync(supervisor, "scenario-status", cancellationToken).ConfigureAwait(false);
                detail["status"] = combatStatus?.DeepClone();
                Require(evidence, combatComplete, "Safety observation incomplete");
                var colonists = combatStatus.Member("colonistRows").AsSlice();
                var standing = colonists.Count > 0;
                foreach (var raw in colonists)
                {
                    if (raw is not JsonObject pawn || pawn["dead"].AsBool() == true || pawn["downed"].AsBool() == true)
                    {
                        standing = false;
                    }
                }

                Require(evidence, standing, "Colonist downed under the combat health guard");
                continue;
            }

            if (ContinuableStop(stopReason, combat))
            {
                // A pacing stop invalidates nothing: record it and run the rest of the window.
                detail["pacing"] = true;
                Require(evidence, CountPacingStop(pacingStops, stopReason), "Pacing stops exhausted: clock stopped on " + stopReason + " past the per-advance bound");
                continue;
            }

            // The reason belongs in the message: a case that dies here reported only "Unexpected native
            // interruption", so nine nightly rows could not be told apart without the evidence tree, and two of them
            // turned out to be different stops.
            Require(evidence, stopReason == "letter_pause", "Unexpected native interruption: clock stopped on " + ScenarioProjection.NamedStopReason(stopReason));
            var detailEvents = (JsonArray)detail["events"]!;
            while (true)
            {
                var batch = await supervisor.CallAsync("events", new JsonObject { ["afterCursor"] = cursor, ["limit"] = 128 }, cancellationToken).ConfigureAwait(false);
                foreach (var row in batch["events"].AsSlice())
                {
                    detailEvents.Add(row?.DeepClone());
                }

                Require(evidence, batch["gap"].AsBool() != true, "Native event history incomplete");
                var nextCursor = batch["nextCursor"].AsUInt64() ?? 0;
                if (nextCursor == cursor)
                {
                    break;
                }

                cursor = nextCursor;
            }

            var stops = new List<JsonObject>();
            foreach (var raw in detailEvents)
            {
                if (raw is JsonObject row && row["epoch"].AsUInt64() == stateEpoch && row["kind"].AsString() == "letter_pause")
                {
                    stops.Add(row);
                }
            }

            Require(evidence, stops.Count == 1, "Triggering letter unavailable");
            var trigger = stops[0]["event"].AsMap();
            var letterId = trigger?["letterId"].AsString() ?? "";
            Require(
                evidence,
                trigger?["source"].AsString() == "LetterStack.ReceiveLetter" && letterId.Length != 0 && !seenLetters.Contains(letterId),
                "Letter attribution unavailable or repeated");
            var (status, complete) = await ScenarioRuntime.ReadSafetyStatusAsync(supervisor, "scenario-status", cancellationToken).ConfigureAwait(false);
            detail["status"] = status?.DeepClone();
            Require(evidence, complete, "Safety observation incomplete");
            var letters = await RunAt.ReadLettersAsync(supervisor.Wire, "scenario-status", supervisor.Identity, cancellationToken).ConfigureAwait(false);
            var matchedLetters = letters.Where(row => row["id"].AsString() == letterId).ToList();
            var approved = false;
            var acknowledged = false;
            if (matchedLetters.Count == 1)
            {
                detail["letter"] = matchedLetters[0].DeepClone();
                (approved, acknowledged) = ScenarioRuntime.LetterApproval(
                    matchedLetters[0]["label"].AsString(),
                    matchedLetters[0]["letterDef"].AsString(),
                    settings.ExpectedLetters,
                    settings.StrictLetters);
            }

            Require(evidence, approved, "Letter is not approved for this scenario");
            var pause = await RunAt.ReadPauseStateAsync(supervisor.Wire, "scenario-pause", cancellationToken).ConfigureAwait(false);
            detail["pause"] = pause.DeepClone();
            Require(evidence, pause["windows"].AsSlice().Count == 0 && pause["paused"].AsBool() == true, "Modal or unverified pause");
            Require(evidence, !ScenarioRuntime.ActiveThreat(status!), "Active threat");
            var pawns = status.Member("colonistRows").AsSlice();
            var allSafe = pawns.Count > 0;
            foreach (var raw in pawns)
            {
                if (raw is not JsonObject pawn)
                {
                    allSafe = false;
                    break;
                }

                var bleeding = pawn["health"].AsMap()?["bleeding"].AsBool() == true;
                if (pawn["dead"].AsBool() == true || bleeding || pawn["downed"].AsBool() == true)
                {
                    allSafe = false;
                }
            }

            Require(evidence, allSafe, "Colonist safety unverified");
            now = await rt.IdentityNowAsync(cancellationToken).ConfigureAwait(false);
            Require(evidence, Dyn.DeepEqual(now, identity), "Native identity changed");
            var fresh = await supervisor.CallAsync("status", null, cancellationToken).ConfigureAwait(false);
            var sameFacts = fresh["owner"].AsString() == state["owner"].AsString()
                && Dyn.DeepEqual(fresh["epoch"], state["epoch"])
                && Dyn.DeepEqual(fresh["lastTick"], state["lastTick"])
                && fresh["stopReason"].AsString() == state["stopReason"].AsString()
                && Dyn.DeepEqual(fresh["newestCursor"], state["newestCursor"]);
            Require(evidence, sameFacts && fresh["active"].AsBool() != true, "Clock changed during inspection");
            seenLetters.Add(letterId);
            detail["acknowledgedLetterId"] = letterId;
            if (acknowledged)
            {
                detail["informational"] = true;
                if (Dyn.Contains(rt.Tools, ScenarioRuntime.DismissLetterTool))
                {
                    try
                    {
                        var dismissed = await rt.Query("dismiss-letter", ScenarioRuntime.DismissLetterTool, new JsonObject { ["letterId"] = letterId }, cancellationToken).ConfigureAwait(false);
                        detail["dismissed"] = dismissed.DeepClone();
                    }
                    catch (AcceptanceException e)
                    {
                        throw new AcceptanceException($"{ScenarioRuntime.DismissLetterTool}: {e.Message}", e);
                    }
                }

                rt.Note("scenario_interruption", "Acknowledged informational letter", detail);
            }
            else
            {
                rt.Note("scenario_interruption", "Acknowledged inspected fixture warning", detail);
            }

            var polled = await supervisor.PollAsync(cancellationToken).ConfigureAwait(false);
            rt.AddClockEvents(polled);
            rt.ReceiveClockEvents();
            Require(evidence, supervisor.Hold.Length == 0, "External clock hold");
        }

        evidence["completed"] = true;
        return state ?? new JsonObject();
    }
}
