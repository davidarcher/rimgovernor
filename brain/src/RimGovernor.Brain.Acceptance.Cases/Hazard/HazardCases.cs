using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>hazard/*</c> family proves the supervisor's declared detection bounds
/// (docs/developers/architecture/hazard-detection-bounds.md): each case injects one hazard class into a native clock run
/// at Ultrafast, the speed that carries the most ticks per frame, while the clock still runs, and asserts the hold's
/// detect_ticks (detected_tick less occurrence_tick) and the gap from the injection tick to the detection both lie within
/// the class's bound. The letter case delivers through the real LetterStack callback (test/deliver_letter); the others use
/// test/hazard_inject. The demoted tier (injury) instead proves an invalidation wake while the clock keeps running.
/// </summary>
public static class HazardCases
{
    // Mirror SupervisedPlayHazards.cs; the clock accounting read reports the native declaration and the case checks the two agree.
    private const int ProbeIntervalTicks = 30;
    private const int HookedBoundTicks = 1;

    // How far the run goes before the injection, so the detection is measured under a moving clock, never at the start baseline.
    private const long SettleTicks = 300;

    // The settle (one status round at Ultrafast is about 2,250 ticks), the bound, and the log sink's flush for the demoted
    // wake, with room to spare under the lab cap.
    private const int RunTicks = 30_000;

    private const string InjectTool = "test/hazard_inject";
    private const string DeliverTool = "test/deliver_letter";
    private const string PauseModeTool = "test/letter_pause_mode";

    private static readonly FixtureOp<string, Fx.HazardInjected> Inject = new(
        InjectTool,
        op => ServedOps.Args(("op", op)),
        ServedOps.Succeeded<Fx.HazardInjected>(InjectTool, r => r.Success));

    // A ThreatSmall letter delivered now through LetterStack.ReceiveLetter, the hook the notification_batch bound rests on.
    private static readonly FixtureOp<NoArgs, Fx.LetterDelivered> Deliver = new(
        DeliverTool,
        _ => ServedOps.Args(("definition", "ThreatSmall"), ("label", "hazard letter"), ("delayTicks", 0)),
        ServedOps.Succeeded<Fx.LetterDelivered>(DeliverTool, r => r.Success && r.LetterId.Length > 0));

    // An empty mode only reads the one in force.
    private static readonly FixtureOp<string, Fx.LetterPauseMode> PauseMode = new(
        PauseModeTool,
        mode => ServedOps.Args(("mode", mode)),
        ServedOps.Succeeded<Fx.LetterPauseMode>(PauseModeTool, r => r.Success && r.Mode.Length > 0));

    // The colony watch policy (no combat events armed) with the policy's own hazard thresholds, which native requires (#2587).
    private static readonly Clock.WatchPolicy Policy = new()
    {
        Mode = Clock.WatchMode.Colony,
        HealthDropFraction = 0.1f,
        MinHealthFraction = 0.5f,
        HostileWithin = 40,
        InjuryStopCooldownMs = 0,
        SeriousSingleHitDamage = (float)HazardThresholdsRules.SeriousSingleHitDamage,
        SeriousSummaryHealthFloor = (float)HazardThresholdsRules.SeriousSummaryHealthFloor,
        SeriousBleedRateFloor = (float)HazardThresholdsRules.SeriousBleedRateFloor,
        SeriousVitalPartFloor = (float)HazardThresholdsRules.SeriousVitalPartFloor,
        ExplosiveNearMarginCells = (float)HazardThresholdsRules.ExplosiveNearMarginCells,
        MeleeReachCells = (float)HazardThresholdsRules.MeleeReachCells,
        InjurySeverityFloorTicks = (uint)HazardThresholdsRules.InjurySeverityFloorTicks,
        PredatorMarginCells = (float)HazardThresholdsRules.HuntPredatorMarginCells,
        // Only starvation holds here: these cases inject other hazards and must not stop for an ordinary hungry pawn.
        HungerHoldCategory = RimGovernor.Contracts.Wire.Defs.HungerCategory.Starving,
    };

    /// <summary>Registers every <c>hazard/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var hc in Classes())
        {
            registry.Register(new TypedCase
            {
                Name = hc.Name,
                Scope = hc.Scope,
                Ticks = RunTicks,
                Tier = CaseTier.OnDemand,
                QuietWorld = true,
                Ops = [InjectTool, DeliverTool, PauseModeTool],
                Run = (s, cancellationToken) => RunAsync(s, hc, cancellationToken),
            });
        }
    }

    // One class: how it is injected and the hold it must raise (with whether a game hook lands it), or -- for a demoted
    // tier, Reason null -- the invalidation wake it must raise instead while the run goes on.
    private sealed record HazardCase(string Name, string Class, Clock.HoldReason? Reason, bool Hooked, long Bound, string Scope, Func<TypedSession, CancellationToken, Task<Injection>> Inject);

    // The tick the fixture acted on, the injury's bleed-out estimate (0: not bleeding out, or not an injury) and the reply as evidence.
    private sealed record Injection(long Tick, long BleedOutTicks, JsonNode? Evidence);

    private static HazardCase[] Classes() =>
    [
        new("hazard/letter", "notification_batch", Clock.HoldReason.NotificationBatch, true, ProbeIntervalTicks,
            "A letter received mid-run at Ultrafast holds the clock as notification_batch within the 30-tick bound (the letter hook lands it next tick).",
            InjectLetterAsync),
        new("hazard/hostile", "hostile", Clock.HoldReason.Hostile, true, ProbeIntervalTicks,
            "A hostile pawn spawned near a colonist mid-run at Ultrafast holds the clock as hostile within the 30-tick bound (the spawn hook lands it next tick).",
            InjectOp("hostile")),
        new("hazard/downed", "colonist_downed", Clock.HoldReason.ColonistDowned, true, HookedBoundTicks,
            "A colonist downed mid-run at Ultrafast holds the clock as colonist_downed within the hooked 1-tick bound.",
            InjectOp("downed")),
        new("hazard/injury", "colonist_injury", null, false, ProbeIntervalTicks,
            "A colonist wounded under the severity floor mid-run at Ultrafast keeps the clock running and invalidates the medical facts within the 30-tick probe bound (#584).",
            InjectOp("injury")),
        new("hazard/predator", "predator_hunt", Clock.HoldReason.PredatorHunt, false, ProbeIntervalTicks,
            "A predator hunting a colonist mid-run at Ultrafast holds the clock as predator_hunt within the 30-tick probe bound.",
            InjectOp("predator")),
    ];

    // Calls test/hazard_inject with the op.
    private static Func<TypedSession, CancellationToken, Task<Injection>> InjectOp(string op) =>
        async (s, cancellationToken) =>
        {
            var reply = await s.FixtureAsync("inject-" + op, Inject, op, cancellationToken).ConfigureAwait(false);
            return new Injection(reply.Tick, reply.BleedOutTicks, ServedOps.Json(reply));
        };

    private static async Task<Injection> InjectLetterAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var reply = await s.FixtureAsync("inject-letter", Deliver, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        return new Injection(reply.Tick, 0, ServedOps.Json(reply));
    }

    private static async Task SetPauseModeAsync(TypedSession s, string mode, CancellationToken cancellationToken)
    {
        var reply = await s.FixtureAsync("pause-mode-" + mode, PauseMode, mode, cancellationToken).ConfigureAwait(false);
        if (reply.Mode != mode)
        {
            throw new AcceptanceException($"letter_pause_mode did not take: {reply.Mode}, expected {mode}");
        }
    }

    private static async Task RunAsync(TypedSession s, HazardCase hc, CancellationToken cancellationToken)
    {
        // No letter pauses the clock on its own: the hold under test is the supervisor's, not LetterPauseHook's. Prefs are
        // process-wide; put the mode back for the next case.
        var initial = await s.FixtureAsync("pause-mode-initial", PauseMode, "", cancellationToken).ConfigureAwait(false);
        await SetPauseModeAsync(s, "Never", cancellationToken).ConfigureAwait(false);
        try
        {
            await RunHazardAsync(s, hc, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _ = await s.FixtureAsync("cleanup", Inject, "cleanup", CancellationToken.None).ConfigureAwait(false);
            }
            catch (AcceptanceException tolerated)
            {
                // Best effort, as the deferred cleanup is.
                Tolerated.Note("hazard cleanup", tolerated);
            }

            try
            {
                await SetPauseModeAsync(s, initial.Mode, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AcceptanceException tolerated)
            {
                // Best effort, as the deferred restore is.
                Tolerated.Note("pause mode restore", tolerated);
            }
        }
    }

    private static async Task RunHazardAsync(TypedSession s, HazardCase hc, CancellationToken cancellationToken)
    {
        var generation = await TypedAuthority.GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);
        var request = new Clock.RunRequest { ExpectedGeneration = generation, Speed = Clock.Speed.Ultrafast, Policy = Policy };

        // The run goes on SettleTicks before the injection, which lands while the clock still runs; a hold before then is a
        // hazard the colony raised on its own and fails the case. The demoted tier ends on its wake instead of a hold.
        long? startTick = null;
        Injection? injected = null;
        long injectedSince = 0;
        LogRow? wake = null;
        var status = await s.RunClockUntilHeldAsync(
            "ultrafast",
            s.TicksLeft,
            request,
            async (running, token) =>
            {
                var tick = running.Context?.Tick ?? 0;
                startTick ??= tick;
                if (injected is null)
                {
                    if (tick < startTick + SettleTicks)
                    {
                        return false;
                    }

                    injectedSince = ClockRows.NowUnixMs();
                    injected = await hc.Inject(s, token).ConfigureAwait(false);
                }

                if (hc.Reason is not null)
                {
                    return false;
                }

                wake = s.Telemetry(TelemetryKinds.ClockInvalidation)
                    .FirstOrDefault(row => ClockRows.ObservedSince(row, injectedSince) && HasFamily(row, Clock.FactFamily.Pawns));
                return wake is not null;
            },
            cancellationToken).ConfigureAwait(false);
        if (injected is null)
        {
            throw new AcceptanceException($"the clock held before the injection ({status.Reason}): {status.Detail}");
        }

        if (hc.Reason is { } reason)
        {
            await AssertHoldAsync(s, hc, reason, injected, status, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            AssertDemoted(s, hc, injected, status, wake);
        }
    }

    private static async Task AssertHoldAsync(TypedSession s, HazardCase hc, Clock.HoldReason reason, Injection injected, Clock.Status held, CancellationToken cancellationToken)
    {
        // The hold row carries what the status does not: the event's detected and occurrence ticks.
        var row = await s.HoldRowAsync("hold", held, cancellationToken).ConfigureAwait(false);
        var holdTick = ClockRows.Tick(row);
        var detected = ClockRows.Whole(row, ColonyEventTelemetry.DetectedTickKey);
        var occurrence = ClockRows.Whole(row, ColonyEventTelemetry.OccurrenceTickKey);
        var injectGap = detected - injected.Tick;
        var detectTicks = detected - occurrence;

        // The native declaration in the clock accounting agrees with the bound the case asserts, and the class's own
        // recorded gap is within it.
        var accounting = await s.ClockAccountingAsync("clock-accounting", cancellationToken).ConfigureAwait(false);
        var declared = accounting.HazardGaps.LastOrDefault(g => g.HazardClass == hc.Class);
        s.Record("hazard", CombatLabKit.Obj(
            ("class", hc.Class),
            ("bound_ticks", hc.Bound),
            ("hold_reason", held.Reason?.ToString()),
            ("inject_tick", injected.Tick),
            ("hold_tick", holdTick),
            ("detected_tick", detected),
            ("occurrence_tick", occurrence),
            ("inject_gap_ticks", injectGap),
            ("detect_ticks", detectTicks),
            ("injected", injected.Evidence?.DeepClone()),
            ("declared", declared is null ? null : CombatLabKit.Obj(
                ("hazardClass", declared.HazardClass),
                ("boundTicks", declared.BoundTicks),
                ("maxTickGap", declared.MaxTickGap),
                ("hooked", declared.Hooked)))));

        // This case exercises the native supervisor, not a served controller: controller decision/dispatch and pawn effects
        // remain absent.
        var response = ResponseEvidence.Create(hc.Name, "Ultrafast", injected.Tick);
        response.DetectionSource = "native_supervisor";
        response.DetectionTick = detected;
        s.Record("response", response.ToJson());

        var shown = $"{held.Reason} at tick {holdTick}: {held.Detail}";
        if (held.Reason != reason)
        {
            throw new AcceptanceException($"hold reason {held.Reason}, expected {reason}: {shown}");
        }

        if (detected is null || occurrence is null)
        {
            throw new AcceptanceException($"the {hc.Class} hold row carries no detected or occurrence tick (detected {detected}, occurred {occurrence}): {shown}");
        }

        if (injectGap is < 0 || injectGap > hc.Bound)
        {
            throw new AcceptanceException($"{hc.Class} detected {injectGap} ticks after the injection, bound {hc.Bound}: {shown}");
        }

        if (detectTicks is < 0 || detectTicks > hc.Bound)
        {
            throw new AcceptanceException($"{hc.Class} detect_ticks {detectTicks} (detected {detected}, occurred {occurrence}), bound {hc.Bound}");
        }

        if (declared is null)
        {
            throw new AcceptanceException($"clock accounting reports no hazard gap for {hc.Class}: {string.Join(", ", accounting.HazardGaps)}");
        }

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
        if (hc.Hooked && !declared.Hooked)
        {
            throw new AcceptanceException($"{hc.Class} hook is not installed: {declared}");
        }

        if (declared.Hooked && injectGap > HookedBoundTicks)
        {
            throw new AcceptanceException($"{hc.Class} is hooked but was detected {injectGap} ticks after the injection");
        }
    }

    // Proves a tier the supervisor no longer holds for: the hazard is detected within the class's bound and published as a
    // clock_invalidation wake naming the pawn and emergency families, and the clock keeps running -- the review the hold
    // used to buy, without the hold-to-readmit pause.
    private static void AssertDemoted(TypedSession s, HazardCase hc, Injection injected, Clock.Status ended, LogRow? wake)
    {
        var wakeTick = wake is null ? (long?)null : ClockRows.Tick(wake);
        var families = wake is null ? "" : string.Join(",", ClockRows.Strings(wake, "families"));
        var gap = wakeTick - injected.Tick;
        s.Record("hazard", CombatLabKit.Obj(
            ("class", hc.Class),
            ("bound_ticks", hc.Bound),
            ("inject_tick", injected.Tick),
            ("injected", injected.Evidence?.DeepClone()),
            ("demoted", true),
            ("ended", ended.State.ToString()),
            ("wake_tick", wakeTick),
            ("wake_families", families),
            ("wake_reason", wake is null ? null : ClockRows.Text(wake, ColonyEventTelemetry.ReasonKey)),
            ("inject_gap_ticks", gap)));

        // The wound is only demotable below the severity floor; the fixture reports the game's own bleed-out estimate so
        // the case fails loudly if the injection ever grew into a danger.
        if (injected.BleedOutTicks > 0 && injected.BleedOutTicks <= HazardThresholdsRules.InjurySeverityFloorTicks)
        {
            throw new AcceptanceException(
                $"the injected wound bleeds out in {injected.BleedOutTicks} ticks, inside the {HazardThresholdsRules.InjurySeverityFloorTicks}-tick severity floor: it is a danger, not a demoted tier");
        }

        // The run survives the wake: the readmit the hold used to cost is exactly what this tier no longer spends.
        if (ended.State == Clock.ClockState.Held || wake is null)
        {
            throw new AcceptanceException($"the clock was held ({ended.Reason}) for a wound under the severity floor: {ended.Detail}");
        }

        if (gap is < 0 || gap > hc.Bound)
        {
            throw new AcceptanceException($"the medical wake landed {gap} ticks after the injection, bound {hc.Bound}: families {families}");
        }

        if (!HasFamily(wake, Clock.FactFamily.Emergency))
        {
            throw new AcceptanceException($"the medical wake does not invalidate the emergency family: families {families}");
        }
    }

    // Reports whether a clock_invalidation row names a fact family; the row carries the enum's C# name.
    private static bool HasFamily(LogRow row, Clock.FactFamily family) =>
        ClockRows.Strings(row, "families").Contains(family.ToString(), StringComparer.Ordinal);
}
