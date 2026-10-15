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
    /// Makes the advance strict: only the listed (label, def) pairs may pause it and the acknowledged letter defs no
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
/// AdvanceGame: runs the clock for at least <c>ticks</c> native ticks, then holds it for the harness's review. There is
/// no native tick budget, so the clock may run a few ticks past the target between the poll that saw it and the
/// verified pause; the overshoot is recorded as <c>overshootTicks</c>. It acknowledges inspected fixture warnings and,
/// unless the advance is strict, the informational letters in <see cref="ScenarioRuntime.AcknowledgedLetterDefs"/>
/// (dismissed through <see cref="ScenarioRuntime.DismissLetterTool"/> when the runtime's tools carry it, recorded under
/// the interruption either way). A combat advance also continues past a colonist_health hold while every colonist still
/// stands. It never clears player holds or retries game orders; an unexpected injury, new threat, or drifted identity
/// raises <see cref="ScenarioInterruptedException"/> with the failing evidence retained on the runtime's report.
/// </summary>
public static class AdvanceGame
{
    /// <summary>The brain hold reason the harness holds the clock with once the tick target is reached.</summary>
    private const string ReviewHold = "REVIEW";

    /// <summary>
    /// The bound on the colonist_health holds one combat advance continues past; the native guard needs a fresh drop
    /// of healthDropFraction per run, so this many is a colonist near downing.
    /// </summary>
    private const int CombatHealthStopsPerAdvance = 8;

    /// <summary>
    /// The bound on the pacing holds (continuable holds) one advance continues past, per reason. Each is a clean hold
    /// that consumed game time, so a healthy advance sees a handful; a watch that re-fires every tick would otherwise
    /// spin the tick target out one hold at a time until the timeout, with the reason buried in the evidence.
    /// </summary>
    private const int PacingStopsPerAdvance = 64;

    /// <summary>
    /// Whether a clock hold is pacing rather than an interruption. The set is global, not a case field beside
    /// Letters: notification_batch is the native batching a pending notification, which says nothing about the advance
    /// in any case, and hostile is the threat watch firing, which is the situation under test exactly when the advance
    /// commits combat targets and an unexpected threat everywhere else. Letters is per-case because which letters are
    /// expected is; which hold reasons are pacing is not.
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

        var evidence = new JsonObject { ["ticks"] = ticks, ["holds"] = new JsonArray(), ["interruptions"] = new JsonArray() };
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

    // The clock is left held when the advance failed: a clock still running would keep consuming game time under the
    // failing evidence. It runs on its own deadline, not the advance's, which may be what ended.
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
            if (current["active"].AsBool() != true)
            {
                return;
            }

            try
            {
                var held = await supervisor.HoldAsync(ReviewHold, "advance_game failed", token).ConfigureAwait(false);
                evidence["cleanup"] = held;
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

    // Holds the clock for the harness's own review once the tick target is reached, and returns the held status once
    // native has verified the pause. Native keeps the first hold reason, so a hazard that won the race shows in the status.
    private static async Task<JsonObject> HoldAtTargetAsync(ScenarioClock supervisor, CancellationToken cancellationToken)
    {
        _ = await supervisor.HoldAsync(ReviewHold, "advance_game target reached", cancellationToken).ConfigureAwait(false);
        return await supervisor.WaitAsync(
            status => status["held"].AsBool() == true && status["pauseVerified"].AsBool() == true,
            TimeSpan.FromSeconds(20),
            "the held clock to verify its pause",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonObject> LoopAsync(ScenarioRuntime rt, ulong ticks, AdvanceSettings settings, JsonObject evidence, JsonObject identity, CancellationToken cancellationToken)
    {
        var supervisor = rt.Clock;
        var combat = settings.CombatTargets.Count > 0;
        JsonObject? state = null;
        ulong previousTick = 0;
        var havePreviousTick = false;
        ulong targetTick = 0;
        var seenLetters = new HashSet<string>(StringComparer.Ordinal);
        var healthStops = 0;
        var pacingStops = new Dictionary<string, int>(StringComparer.Ordinal);
        var holds = (JsonArray)evidence["holds"]!;
        var interruptions = (JsonArray)evidence["interruptions"]!;

        while (true)
        {
            var now = await rt.IdentityNowAsync(cancellationToken).ConfigureAwait(false);
            Require(evidence, Dyn.DeepEqual(now, identity), "Native identity changed");
            var current = await supervisor.CallAsync("status", null, cancellationToken).ConfigureAwait(false);
            var currentTick = current["lastTick"].AsUInt64() ?? 0;
            if (targetTick == 0)
            {
                targetTick = currentTick + ticks;
                evidence["startTick"] = currentTick;
                evidence["targetTick"] = targetTick;
            }
            else if (currentTick >= targetTick)
            {
                // The interruption just handled already carried the clock to the target: nothing left to run.
                return await FinishAsync(rt, evidence, current, targetTick, cancellationToken).ConfigureAwait(false);
            }

            var speed = supervisor.TestAcceleration ? "Ultrafast" : "Superfast";
            var started = await supervisor.RunAsync(speed, cancellationToken).ConfigureAwait(false);
            var startTick = started["startTick"].AsUInt64() ?? 0;
            Require(evidence, !havePreviousTick || startTick == previousTick, "Native ticks changed between runs");
            var cursor = started["newestCursor"].AsUInt64() ?? 0;

            // The clock runs until a hold. The harness is the reviewer: it keeps the heartbeat, and holds the clock
            // itself the moment the tick target is reached. The overshoot is the ticks the game ran between the poll that saw
            // the target and the verified pause.
            while (true)
            {
                await supervisor.BeatIfDueAsync(cancellationToken).ConfigureAwait(false);
                state = await supervisor.CallAsync("status", null, cancellationToken).ConfigureAwait(false);
                if (state["held"].AsBool() == true)
                {
                    break;
                }

                if ((state["lastTick"].AsUInt64() ?? 0) >= targetTick)
                {
                    state = await HoldAtTargetAsync(supervisor, cancellationToken).ConfigureAwait(false);
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }

            holds.Add(state.DeepClone());
            now = await rt.IdentityNowAsync(cancellationToken).ConfigureAwait(false);
            Require(evidence, Dyn.DeepEqual(now, identity), "Native identity changed");
            if (state["pauseVerified"].AsBool() != true)
            {
                state = await supervisor.WaitAsync(
                    status => status["held"].AsBool() == true && status["pauseVerified"].AsBool() == true,
                    TimeSpan.FromSeconds(20),
                    "the held clock to verify its pause",
                    cancellationToken).ConfigureAwait(false);
            }

            Require(evidence, state["pauseVerified"].AsBool() == true, "Native pause unverified");
            var lastTick = state["lastTick"].AsUInt64() ?? 0;
            Require(evidence, lastTick >= startTick, "Invalid native tick progress");
            previousTick = lastTick;
            havePreviousTick = true;
            var stopReason = state["stopReason"].AsString();
            if (string.Equals(stopReason, ReviewHold, StringComparison.OrdinalIgnoreCase) && lastTick >= targetTick)
            {
                return await FinishAsync(rt, evidence, state, targetTick, cancellationToken).ConfigureAwait(false);
            }

            var detail = new JsonObject { ["clock"] = state.DeepClone(), ["events"] = new JsonArray() };
            interruptions.Add(detail);
            if (stopReason == "colonist_health" && combat)
            {
                // A colonist ordered to engage the committed targets may take the wounds the combat policy flags; the
                // stop is recorded and the next run continues so the fight can resolve. Each run baselines
                // health afresh, so a repeat needs a further drop; the cap keeps a pawn who bleeds out under the
                // guard from consuming the whole tick target hold by hold. A downed or dead colonist still
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
                // A pacing hold invalidates nothing: record it and run on.
                detail["pacing"] = true;
                Require(evidence, CountPacingStop(pacingStops, stopReason), "Pacing holds exhausted: clock held on " + stopReason + " past the per-advance bound");
                continue;
            }

            // The reason belongs in the message: a case that dies here reported only "Unexpected native
            // interruption", so nine nightly rows could not be told apart without the evidence tree, and two of them
            // turned out to be different holds.
            Require(evidence, stopReason == "letter_pause", "Unexpected native interruption: clock held on " + ScenarioProjection.NamedStopReason(stopReason));
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
                if (raw is JsonObject row && row["kind"].AsString() == "letter_pause")
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
            var sameFacts = Dyn.DeepEqual(fresh["lastTick"], state["lastTick"])
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
    }

    // The tick target is reached and the clock is held for the harness's review: deliver the journal and record how far
    // past the target the clock ran before the pause took.
    private static async Task<JsonObject> FinishAsync(ScenarioRuntime rt, JsonObject evidence, JsonObject state, ulong targetTick, CancellationToken cancellationToken)
    {
        var supervisor = rt.Clock;
        var events = await supervisor.PollAsync(cancellationToken).ConfigureAwait(false);
        rt.AddClockEvents(events);
        rt.ReceiveClockEvents();
        Require(evidence, supervisor.Hold.Length == 0, "External clock hold");
        evidence["overshootTicks"] = (state["lastTick"].AsUInt64() ?? 0) - targetTick;
        evidence["completed"] = true;
        return state;
    }
}
