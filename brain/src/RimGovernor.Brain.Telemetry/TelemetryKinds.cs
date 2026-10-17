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
}
