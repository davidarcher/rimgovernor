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
    /// <summary>A planner's or the rounds review's step verdict (<see cref="Decision.PlannerStepKind"/>).</summary>
    public static readonly TelemetryKind PlannerStep = new(Decision.PlannerStepKind);

    /// <summary>One pawn activity interval (<see cref="PawnActivityTelemetry.IntervalKind"/>).</summary>
    public static readonly TelemetryKind PawnActivityInterval = new(PawnActivityTelemetry.IntervalKind);

    /// <summary>One observed production-site output (<see cref="PawnActivityTelemetry.OutputKind"/>).</summary>
    public static readonly TelemetryKind PawnActivityOutput = new(PawnActivityTelemetry.OutputKind);

    /// <summary>The kind the stockpile, field and building layout decisions are emitted under.</summary>
    public const string LayoutEditKind = "layout_edit";

    /// <summary>One stockpile, field or building layout decision (<see cref="LayoutEditKind"/>).</summary>
    public static readonly TelemetryKind LayoutEdit = new(LayoutEditKind);

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
}
