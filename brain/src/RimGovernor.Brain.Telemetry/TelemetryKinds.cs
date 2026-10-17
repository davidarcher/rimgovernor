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
    /// <summary>One pawn activity interval (<see cref="PawnActivityTelemetry.IntervalKind"/>).</summary>
    public static readonly TelemetryKind PawnActivityInterval = new(PawnActivityTelemetry.IntervalKind);

    /// <summary>One observed production-site output (<see cref="PawnActivityTelemetry.OutputKind"/>).</summary>
    public static readonly TelemetryKind PawnActivityOutput = new(PawnActivityTelemetry.OutputKind);

    /// <summary>The kind the stockpile, field and building layout decisions are emitted under.</summary>
    public const string LayoutEditKind = "layout_edit";

    /// <summary>One stockpile, field or building layout decision (<see cref="LayoutEditKind"/>).</summary>
    public static readonly TelemetryKind LayoutEdit = new(LayoutEditKind);

    /// <summary>The clock entered a hold (<see cref="ClockEventTelemetry.HoldKind"/>): a hazard, a game pause, a combat event or a brain hold.</summary>
    public static readonly TelemetryKind ClockHold = new(ClockEventTelemetry.HoldKind);

    /// <summary>A Run resumed a held clock (<see cref="ClockEventTelemetry.ResumeKind"/>).</summary>
    public static readonly TelemetryKind ClockResume = new(ClockEventTelemetry.ResumeKind);

    /// <summary>The native authority generation moved (<see cref="ClockEventTelemetry.AuthorityKind"/>).</summary>
    public static readonly TelemetryKind ClockAuthority = new(ClockEventTelemetry.AuthorityKind);

    /// <summary>A new game alert (<see cref="ClockEventTelemetry.AlertKind"/>).</summary>
    public static readonly TelemetryKind ClockAlert = new(ClockEventTelemetry.AlertKind);

    /// <summary>A new letter or transient message (<see cref="ClockEventTelemetry.NotificationKind"/>).</summary>
    public static readonly TelemetryKind ClockNotification = new(ClockEventTelemetry.NotificationKind);

    /// <summary>A colonist injury observed without a hold (<see cref="ClockEventTelemetry.InjuryKind"/>).</summary>
    public static readonly TelemetryKind ClockInjury = new(ClockEventTelemetry.InjuryKind);

    /// <summary>No conscious hostiles remain (<see cref="ClockEventTelemetry.HostilesClearedKind"/>).</summary>
    public static readonly TelemetryKind ClockHostilesCleared = new(ClockEventTelemetry.HostilesClearedKind);

    /// <summary>Observed fact families changed under a running clock (<see cref="ClockEventTelemetry.InvalidationKind"/>).</summary>
    public static readonly TelemetryKind ClockInvalidation = new(ClockEventTelemetry.InvalidationKind);

    /// <summary>A long event or transient force pause began or cleared (<see cref="ClockEventTelemetry.LongEventKind"/>).</summary>
    public static readonly TelemetryKind ClockLongEvent = new(ClockEventTelemetry.LongEventKind);

    /// <summary>A pending hold's pause did not take; the hold stays armed (<see cref="ClockEventTelemetry.PauseFailedKind"/>).</summary>
    public static readonly TelemetryKind ClockPauseFailed = new(ClockEventTelemetry.PauseFailedKind);
}
