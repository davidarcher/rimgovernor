namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// A registered OTLP log kind (docs/developers/contracts/otel-signals.md#kinds). Only <see cref="TelemetryKinds"/>
/// constructs one, so a reader that takes a kind cannot be handed a raw kind string.
/// </summary>
public sealed class TelemetryKind
{
    internal TelemetryKind(string name) => Name = name;

    /// <summary>The row's <c>kind</c> (the OTLP record's <c>eventName</c>).</summary>
    public string Name { get; }

    public override string ToString() => Name;
}

/// <summary>
/// The kinds a reader may select rows by. A kind is added here with its row in the kinds table of otel-signals.md and
/// the emitter's constant, when the first reader needs it.
/// </summary>
public static class TelemetryKinds
{
    public static readonly TelemetryKind AcceptanceCase = new("acceptance_case");
    public static readonly TelemetryKind ForecastPrediction = new(Otlp.ForecastEvidenceReader.PredictionKind);
    public static readonly TelemetryKind ForecastObservation = new(Otlp.ForecastEvidenceReader.ObservationKind);
    /// <summary>A planner's or the rounds review's step verdict (<see cref="Decision.PlannerStepKind"/>).</summary>
    public static readonly TelemetryKind PlannerStep = new(Decision.PlannerStepKind);

    /// <summary>One pawn activity interval (<see cref="PawnActivityTelemetry.IntervalKind"/>).</summary>
    public static readonly TelemetryKind PawnActivityInterval = new(PawnActivityTelemetry.IntervalKind);

    /// <summary>One change of an installed order's execution (<see cref="PawnActivityTelemetry.ExecutionKind"/>).</summary>
    public static readonly TelemetryKind PawnActivityExecution = new(PawnActivityTelemetry.ExecutionKind);

    /// <summary>One observed production-site output (<see cref="PawnActivityTelemetry.OutputKind"/>).</summary>
    public static readonly TelemetryKind PawnActivityOutput = new(PawnActivityTelemetry.OutputKind);

    /// <summary>The kind the goals family's per-goal review rows are emitted under (#3494).</summary>
    public const string GoalReviewKind = "goal_review";

    /// <summary>One goal's standing at a review (<see cref="GoalReviewKind"/>).</summary>
    public static readonly TelemetryKind GoalReview = new(GoalReviewKind);

    /// <summary>The kind the shared portfolio comparison's selection decisions are emitted under.</summary>
    public const string CoupledSelectionKind = "coupled_selection";

    /// <summary>One review's coupled selection: the chosen tasks and the value each outcome was ranked by (<see cref="CoupledSelectionKind"/>).</summary>
    public static readonly TelemetryKind CoupledSelection = new(CoupledSelectionKind);

    /// <summary>The kind the stockpile, field and building layout decisions are emitted under.</summary>
    public const string LayoutEditKind = "layout_edit";

    /// <summary>One stockpile, field or building layout decision (<see cref="LayoutEditKind"/>).</summary>
    public static readonly TelemetryKind LayoutEdit = new(LayoutEditKind);

    /// <summary>The kind the layout plan's derive, replan and cut decisions are emitted under.</summary>
    public const string LayoutPlanKind = "layout_plan";

    /// <summary>One layout plan decision (<see cref="LayoutPlanKind"/>).</summary>
    public static readonly TelemetryKind LayoutPlan = new(LayoutPlanKind);

    /// <summary>The kind the station chain's advice row is emitted under.</summary>
    public const string LayoutStationsKind = "layout_stations";

    /// <summary>One run of the station chain (<see cref="LayoutStationsKind"/>).</summary>
    public static readonly TelemetryKind LayoutStations = new(LayoutStationsKind);

    /// <summary>One rounds review's summary row (<see cref="RoundsReviewTelemetry.Kind"/>): the food plan and the hunt comparison's note.</summary>
    public static readonly TelemetryKind RoundsReview = new(RoundsReviewTelemetry.Kind);

    /// <summary>One review's tier-2 colony model run (<see cref="ColonyModelTelemetry.Kind"/>): horizon, events, runway, per-project dV/dt, truncation and findings.</summary>
    public static readonly TelemetryKind ColonyModel = new(ColonyModelTelemetry.Kind);

    /// <summary>The clock entered a hold (<see cref="ColonyEventTelemetry.HoldKind"/>): a hazard, a game pause, a combat event or a brain hold.</summary>
    public static readonly TelemetryKind ClockHold = new(ColonyEventTelemetry.HoldKind);

    /// <summary>A Run resumed a held clock (<see cref="ColonyEventTelemetry.ResumeKind"/>).</summary>
    public static readonly TelemetryKind ClockResume = new(ColonyEventTelemetry.ResumeKind);

    /// <summary>The native authority generation moved (<see cref="ColonyEventTelemetry.AuthorityKind"/>).</summary>
    public static readonly TelemetryKind ClockAuthority = new(ColonyEventTelemetry.AuthorityKind);

    /// <summary>A new game alert (<see cref="ColonyEventTelemetry.AlertKind"/>).</summary>
    public static readonly TelemetryKind ClockAlert = new(ColonyEventTelemetry.AlertKind);

    /// <summary>A new letter or transient message (<see cref="ColonyEventTelemetry.NotificationKind"/>).</summary>
    public static readonly TelemetryKind ClockNotification = new(ColonyEventTelemetry.NotificationKind);

    /// <summary>A colonist injury observed without a hold (<see cref="ColonyEventTelemetry.InjuryKind"/>).</summary>
    public static readonly TelemetryKind ClockInjury = new(ColonyEventTelemetry.InjuryKind);

    /// <summary>No conscious hostiles remain (<see cref="ColonyEventTelemetry.HostilesClearedKind"/>).</summary>
    public static readonly TelemetryKind ClockHostilesCleared = new(ColonyEventTelemetry.HostilesClearedKind);

    /// <summary>Observed fact families changed under a running clock (<see cref="ColonyEventTelemetry.InvalidationKind"/>).</summary>
    public static readonly TelemetryKind ClockInvalidation = new(ColonyEventTelemetry.InvalidationKind);

    /// <summary>A long event or transient force pause began or cleared (<see cref="ColonyEventTelemetry.LongEventKind"/>).</summary>
    public static readonly TelemetryKind ClockLongEvent = new(ColonyEventTelemetry.LongEventKind);

    /// <summary>A pending hold's pause did not take; the hold stays armed (<see cref="ColonyEventTelemetry.PauseFailedKind"/>).</summary>
    public static readonly TelemetryKind ClockPauseFailed = new(ColonyEventTelemetry.PauseFailedKind);

    /// <summary>The player asked for a speed in game (<see cref="ColonyEventTelemetry.SpeedRequestKind"/>).</summary>
    public static readonly TelemetryKind ClockSpeedRequest = new(ColonyEventTelemetry.SpeedRequestKind);

    /// <summary>The clock scheduler's step decision row's kind.</summary>
    public const string ClockStepKind = "clock_step";

    /// <summary>One clock scheduler step (<see cref="ClockStepKind"/>).</summary>
    public static readonly TelemetryKind ClockStep = new(ClockStepKind);

    /// <summary>The clock run driver's decision row's kind.</summary>
    public const string ClockRunKind = "clock_run";

    /// <summary>A scheduler step that changed or refused the clock (<see cref="ClockRunKind"/>).</summary>
    public static readonly TelemetryKind ClockRun = new(ClockRunKind);

    /// <summary>The clock_run attr naming the station fact a first_plan refusal waits for (#3652).</summary>
    public const string ClockRunFirstPlanFactKey = "first_plan_fact";

    /// <summary>An action run's decision row's kind.</summary>
    public const string DispatchKind = "dispatch";

    /// <summary>One action run that acted (<see cref="DispatchKind"/>).</summary>
    public static readonly TelemetryKind Dispatch = new(DispatchKind);

    /// <summary>The clock scheduler's combat stop row's kind.</summary>
    public const string ClockStopKind = "clock_stop";

    /// <summary>A combat window stopped the clock, with its resume latency (<see cref="ClockStopKind"/>).</summary>
    public static readonly TelemetryKind ClockStop = new(ClockStopKind);

    /// <summary>A combat order result's decision row's kind.</summary>
    public const string CombatOrderKind = "combat_order";

    /// <summary>One combat order applied or refused (<see cref="CombatOrderKind"/>).</summary>
    public static readonly TelemetryKind CombatOrder = new(CombatOrderKind);

    /// <summary>The combat window's closing summary row's kind.</summary>
    public const string CombatSummaryKind = "combat_summary";

    /// <summary>A combat ended (<see cref="CombatSummaryKind"/>).</summary>
    public static readonly TelemetryKind CombatSummary = new(CombatSummaryKind);
}
