using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Telemetry;
using Clock = RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>hazard/*</c> cases (Go <c>hazard/hazard.go</c>) prove the supervisor's declared detection bounds
/// (docs/developers/architecture/hazard-detection-bounds.md): each injects one hazard class into a clock run running
/// at Ultrafast, the speed that carries the most ticks per frame, and asserts the stop's detect_ticks (detected_tick less
/// occurrence_tick) and the gap from the injection tick to the detection both lie within the class's bound. The letter
/// case delivers through the real LetterStack callback (test/deliver_letter); the others use test/hazard_inject.
/// </summary>
public static class HazardCases
{
    // Mirror SupervisedPlayHazards.cs; the clock accounting read reports the native declaration and the case checks the two agree.
    private const int ProbeIntervalTicks = 30;
    private const int HookedBoundTicks = 1;


    // How far the run goes before the injection, so the detection is measured under a moving clock, never at the start baseline.
    private const ulong SettleTicks = 300;

    // Mirrors policy.InjurySeverityFloorTicks, the floor sent: a wound whose bleed-out lands inside it still holds the
    // hold, so the demoted case fails if its injection ever grows into one. 0 on the wire means the pawn is not bleeding out at all.
    private const long InjurySeverityFloorTicks = 5000;

    private static readonly string[] RequiredTools = ["test/hazard_inject", "test/deliver_letter", "test/letter_pause_mode"];

    /// <summary>Registers every <c>hazard/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var hc in Classes())
        {
            registry.Register(new AcceptanceCase
            {
                Name = hc.Name,
                Scope = hc.Scope,
                Start = new LabStart(),
                QuietWorld = true,
                RequiredOps = RequiredTools,
                Budget = TimeSpan.FromMinutes(4),
                Crew = new Crew { Size = 3 },
                Run = (s, cancellationToken) => RunAsync(s, hc, cancellationToken),
            });
        }
    }

    // One class: how it is injected and the stop it must raise, or -- for a demoted tier -- the invalidation wake it must raise
    // instead while the run goes on.
    private sealed record HazardCase(string Name, string Class, long Bound, string Scope, bool Demoted, Func<ISession, CancellationToken, Task<(long Tick, JsonObject Reply)>> Inject);

    private static HazardCase[] Classes() =>
    [
        new("hazard/letter", "notification_batch", ProbeIntervalTicks,
            "A letter received mid-run at Ultrafast stops the clock as notification_batch within the 30-tick bound (the letter hook lands it next tick).",
            false, InjectLetterAsync),
        new("hazard/hostile", "hostile", ProbeIntervalTicks,
            "A hostile pawn spawned near a colonist mid-run at Ultrafast stops the clock as hostile within the 30-tick bound (the spawn hook lands it next tick).",
            false, InjectOp("hostile")),
        new("hazard/downed", "colonist_downed", HookedBoundTicks,
            "A colonist downed mid-run at Ultrafast stops the clock as colonist_downed within the hooked 1-tick bound.",
            false, InjectOp("downed")),
        new("hazard/injury", "colonist_injury", ProbeIntervalTicks,
            "A colonist wounded under the severity floor mid-run at Ultrafast keeps the clock running and invalidates the medical facts within the 30-tick probe bound (#584).",
            true, InjectOp("injury")),
        new("hazard/predator", "predator_hunt", ProbeIntervalTicks,
            "A predator hunting a colonist mid-run at Ultrafast stops the clock as predator_hunt within the 30-tick probe bound.",
            false, InjectOp("predator")),
    ];

    // Calls test/hazard_inject with the op and reports the tick the fixture acted on.
    private static Func<ISession, CancellationToken, Task<(long, JsonObject)>> InjectOp(string op) =>
        async (s, cancellationToken) =>
        {
            var reply = await s.Harness.CallAsync("inject-" + op, "test/hazard_inject", new JsonObject { ["op"] = op }, cancellationToken).ConfigureAwait(false);
            return reply["success"].AsBool() != true
                ? throw new AcceptanceException($"hazard_inject {op} refused: {reply.ToJson()}")
                : ((long)reply["tick"].AsNumber(), reply);
        };

    // Delivers a ThreatSmall letter now through LetterStack.ReceiveLetter, the hook the notification_batch bound rests on.
    private static async Task<(long, JsonObject)> InjectLetterAsync(ISession s, CancellationToken cancellationToken)
    {
        var reply = await s.Harness.CallAsync(
            "inject-letter",
            "test/deliver_letter",
            new JsonObject { ["definition"] = "ThreatSmall", ["label"] = "hazard letter", ["delayTicks"] = 0 },
            cancellationToken).ConfigureAwait(false);
        return reply["success"].AsBool() != true
            ? throw new AcceptanceException($"deliver_letter refused: {reply.ToJson()}")
            : ((long)reply["tick"].AsNumber(), reply);
    }

    private static async Task RunAsync(ISession s, HazardCase hc, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        foreach (var tool in RequiredTools)
        {
            if (!Dyn.Contains(s.Names, tool))
            {
                throw new AcceptanceException($"{tool} is not installed: build the mod with -Fixture HazardFixture,LetterFixture");
            }
        }

        // No letter pauses the clock on its own: the stop under test is the supervisor's, not LetterPauseHook's. Prefs are
        // process-wide; put the mode back for the next case.
        var initial = await h.CallAsync("pause-mode-initial", "test/letter_pause_mode", [], cancellationToken).ConfigureAwait(false);

        async Task SetPauseMode(string mode, CancellationToken token)
        {
            var reply = await h.CallAsync("pause-mode-" + mode, "test/letter_pause_mode", new JsonObject { ["mode"] = mode }, token).ConfigureAwait(false);
            if (reply["mode"].AsString() != mode)
            {
                throw new AcceptanceException($"letter_pause_mode did not take: {reply.ToJson()}");
            }
        }

        await SetPauseMode("Never", cancellationToken).ConfigureAwait(false);
        try
        {
            await RunHazardAsync(s, hc, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _ = await h.CallAsync("cleanup", "test/hazard_inject", new JsonObject { ["op"] = "cleanup" }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AcceptanceException tolerated)
            {
                // Best effort, as the deferred cleanup is.
                Tolerated.Note("hazard cleanup", tolerated);
            }

            try
            {
                await SetPauseMode(initial["mode"].AsString(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (AcceptanceException tolerated)
            {
                // Best effort, as the deferred restore is.
                Tolerated.Note("pause mode restore", tolerated);
            }
        }
    }

    private static async Task RunHazardAsync(ISession s, HazardCase hc, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var clock = new ScenarioClock(h, s.Identity, report);
        _ = await clock.AcquireAsync("acquire", cancellationToken).ConfigureAwait(false);
        JsonObject started;
        try
        {
            started = await clock.RunAsync("Ultrafast", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("ultrafast run: " + e.Message, e);
        }

        var startTick = started["startTick"].AsUInt64() ?? 0;

        // Let the run go on before injecting; a stop before then is a hazard the colony raised on its own and fails the case.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            await clock.BeatIfDueAsync(cancellationToken).ConfigureAwait(false);
            var status = await clock.StatusAsync(cancellationToken).ConfigureAwait(false);
            if (status["active"].AsBool() != true)
            {
                throw new AcceptanceException("clock held before the injection: " + status["stopReason"].AsString());
            }

            if ((status["lastTick"].AsUInt64() ?? 0) >= startTick + SettleTicks)
            {
                break;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"run did not reach {SettleTicks} ticks in 60 s: {status.ToJson()}"));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        var (injectTick, injected) = await hc.Inject(s, cancellationToken).ConfigureAwait(false);
        if (hc.Demoted)
        {
            await RunDemotedAsync(clock, hc, report, injectTick, injected, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Wait for the run's first clock_hold row; the 90 s deadline bounds the wait.
        var stopped = await clock.AwaitRowsAsync(
            () => clock.FirstRunRow(TelemetryKinds.ClockHold),
            TimeSpan.FromSeconds(90),
            string.Create(CultureInfo.InvariantCulture, $"a stop after injecting {hc.Class} at tick {injectTick}"),
            cancellationToken).ConfigureAwait(false);
        var kind = ClockRows.Reason(stopped);
        var stopTick = ClockRows.Tick(stopped);
        var detected = ClockRows.Whole(stopped, ColonyEventTelemetry.DetectedTickKey);
        var occurrence = ClockRows.Whole(stopped, ColonyEventTelemetry.OccurrenceTickKey);
        var shown = $"{kind} at tick {stopTick}: {ClockRows.Text(stopped, "msg")}";
        var summary = new JsonObject
        {
            ["class"] = hc.Class,
            ["bound_ticks"] = hc.Bound,
            ["stop_kind"] = kind,
            ["inject_tick"] = injectTick,
            ["stop_tick"] = stopTick,
            ["detected_tick"] = detected,
            ["occurrence_tick"] = occurrence,
            ["injected"] = injected.DeepClone(),
        };
        report["hazard"] = summary;
        var response = ResponseEvidence.Create(hc.Name, "Ultrafast", injectTick);
        response.DetectionSource = "native_supervisor";
        if (detected is { } detectedTick)
        {
            response.DetectionTick = detectedTick;
        }

        // This case exercises the native supervisor, not a served controller. Controller decision/dispatch and pawn
        // effects therefore remain absent.
        report["response"] = response.ToJson();
        if (kind != hc.Class)
        {
            throw new AcceptanceException($"stop kind {kind}, expected {hc.Class}: {shown}");
        }

        if (detected is not { } detectedAt)
        {
            throw new AcceptanceException($"{kind} stop carries no detectedTick: {shown}");
        }

        var injectGap = detectedAt - injectTick;
        summary["inject_gap_ticks"] = injectGap;
        if (injectGap < 0 || injectGap > hc.Bound)
        {
            throw new AcceptanceException($"{kind} detected {injectGap} ticks after the injection, bound {hc.Bound}");
        }

        if (occurrence is not { } occurredAt)
        {
            throw new AcceptanceException($"{kind} stop carries no occurrenceTick: {shown}");
        }

        var detectTicks = detectedAt - occurredAt;
        summary["detect_ticks"] = detectTicks;
        if (detectTicks < 0 || detectTicks > hc.Bound)
        {
            throw new AcceptanceException($"{kind} detect_ticks {detectTicks} (detected {detectedAt}, occurred {occurredAt}), bound {hc.Bound}");
        }

        // The native declaration in the clock accounting agrees with the bound the case asserts, and the class's own
        // recorded gap is within it.
        var accounting = await ClockAccounting.ReadAsync(s.Harness, "clock-accounting", cancellationToken).ConfigureAwait(false);
        var declared = accounting.HazardGaps.LastOrDefault(g => g.HazardClass == hc.Class)
            ?? throw new AcceptanceException($"clock accounting reports no hazard gap for {hc.Class}: {string.Join(", ", accounting.HazardGaps)}");
        summary["declared"] = new JsonObject
        {
            ["hazardClass"] = declared.HazardClass,
            ["boundTicks"] = declared.BoundTicks,
            ["maxTickGap"] = declared.MaxTickGap,
            ["hooked"] = declared.Hooked,
        };
        if (declared.BoundTicks != hc.Bound)
        {
            throw new AcceptanceException($"native declares a {declared.BoundTicks}-tick bound for {hc.Class}, the case asserts {hc.Bound}");
        }

        if (declared.MaxTickGap > hc.Bound)
        {
            throw new AcceptanceException($"native recorded a {declared.MaxTickGap}-tick gap for {hc.Class}, bound {hc.Bound}");
        }

        // A hooked class (the letter, spawn and downed hooks) lands at the next tick boundary whatever its declared bound:
        // the hook must be installed and the measured gap within the hooked bound.
        if (declared.Hooked)
        {
            if (injectGap > HookedBoundTicks)
            {
                throw new AcceptanceException($"{kind} is hooked but was detected {injectGap} ticks after the injection");
            }
        }
        else if (hc.Class is "notification_batch" or "hostile" or "colonist_downed")
        {
            throw new AcceptanceException($"{hc.Class} hook is not installed: {declared}");
        }
    }

    // Proves a tier the supervisor no longer stops for: the hazard is detected within the class's bound and published as an
    // observation_invalidated wake naming the pawn families, and the clock keeps running -- the review the stop used to
    // buy, without the stop-to-readmit pause.
    private static async Task RunDemotedAsync(ScenarioClock clock, HazardCase hc, Report report, long injectTick, JsonObject injected, CancellationToken cancellationToken)
    {
        var summary = new JsonObject
        {
            ["class"] = hc.Class,
            ["bound_ticks"] = hc.Bound,
            ["inject_tick"] = injectTick,
            ["injected"] = injected.DeepClone(),
            ["demoted"] = true,
        };
        report["hazard"] = summary;

        // The wound is only demotable below the severity floor; the fixture reports the game's own bleed-out estimate so
        // the case fails loudly if the injection ever grew into a danger.
        if (Int64Of(injected["bleedOutTicks"]) is { } ticks && ticks > 0 && ticks <= InjurySeverityFloorTicks)
        {
            throw new AcceptanceException(
                $"the injected wound bleeds out in {ticks} ticks, inside the {InjurySeverityFloorTicks}-tick severity floor: it is a danger, not a demoted tier: {injected.ToJson()}");
        }

        var wake = await clock.AwaitRowsAsync(
            () =>
            {
                if (clock.FirstRunRow(TelemetryKinds.ClockHold) is { } stopped)
                {
                    throw new AcceptanceException($"the clock was held ({ClockRows.Reason(stopped)}) for a wound under the severity floor: {ClockRows.Text(stopped, "msg")}");
                }

                return clock.RunRows(TelemetryKinds.ClockInvalidation).FirstOrDefault(row => HasFamily(row, Clock.FactFamily.Pawns));
            },
            TimeSpan.FromSeconds(90),
            string.Create(CultureInfo.InvariantCulture, $"a medical wake after injecting {hc.Class} at tick {injectTick}"),
            cancellationToken).ConfigureAwait(false);
        var wakeTick = ClockRows.Tick(wake);
        var families = string.Join(",", ClockRows.Strings(wake, "families"));
        summary["wake"] = new JsonObject { ["families"] = families, ["reason"] = ClockRows.Text(wake, ColonyEventTelemetry.ReasonKey) };
        summary["wake_tick"] = wakeTick;
        var gap = wakeTick - injectTick;
        summary["inject_gap_ticks"] = gap;
        if (gap < 0 || gap > hc.Bound)
        {
            throw new AcceptanceException($"the medical wake landed {gap} ticks after the injection, bound {hc.Bound}: families {families}");
        }

        if (!HasFamily(wake, Clock.FactFamily.Emergency))
        {
            throw new AcceptanceException($"the medical wake does not invalidate the emergency family: families {families}");
        }

        // The run survives the wake: the readmit the hold used to cost is exactly what this tier no longer spends.
        await clock.BeatIfDueAsync(cancellationToken).ConfigureAwait(false);
        var status = await clock.StatusAsync(cancellationToken).ConfigureAwait(false);
        if (status["active"].AsBool() != true)
        {
            throw new AcceptanceException("the clock is no longer running after the wake: " + status["stopReason"].AsString());
        }
    }

    // Reports whether a clock_invalidation row names a fact family; the row carries the enum's C# name.
    private static bool HasFamily(LogRow row, Clock.FactFamily family) =>
        ClockRows.Strings(row, "families").Contains(family.ToString(), StringComparer.Ordinal);

    // Reads a JSON integer the wire carries as a number.
    private static long? Int64Of(JsonNode? value) => value switch
    {
        null => null,
        _ => (long)value.AsNumber(),
    };
}
