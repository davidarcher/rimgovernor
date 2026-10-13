using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The decoded colony facts an end-state audit judges beyond what the raw snapshot carries (Go
/// <c>campaign.ColonyFacts</c>'s projection: the sleeping temperature band and the power coverage). A null member is a
/// fact the snapshot did not carry (Go <c>Fact.Value()</c> unknown).
/// </summary>
/// <param name="SleepingMin">The coldest sleeping-room temperature, Celsius.</param>
/// <param name="SleepingMax">The hottest sleeping-room temperature, Celsius.</param>
/// <param name="PowerRequired">Whether any power consumer exists.</param>
/// <param name="PowerHeadroom">The weakest network's headroom, watts.</param>
/// <param name="DisabledConsumers">Whether a consumer is enabled but unpowered.</param>
public sealed record ColonyFactsReading(double? SleepingMin, double? SleepingMax, bool? PowerRequired, double? PowerHeadroom, bool? DisabledConsumers);

/// <summary>
/// The seam to the decoded colony projection. The Go harness decoded <c>observations_read_colony_facts</c> over its
/// own typed bridge client (definition catalog and frame tables); the C# harness has none, so the host installs the
/// reader once (the in-process brain's observation layer owns the decode) and <see cref="ReadAsync"/> fails until it does.
/// </summary>
public static class ColonyFactsSource
{
    /// <summary>Reads the loaded game's decoded colony facts: the harness, the session's identity, the label and the token.</summary>
    public static Func<Harness, JsonObject, string, CancellationToken, Task<ColonyFactsReading>>? Reader { get; set; }

    /// <summary>Reads the decoded facts through the installed <see cref="Reader"/>.</summary>
    public static Task<ColonyFactsReading> ReadAsync(Harness harness, JsonObject identity, string label, CancellationToken cancellationToken) =>
        Reader is { } read
            ? read(harness, identity, label, cancellationToken)
            : throw new AcceptanceException("no colony-facts reader is installed (ColonyFactsSource.Reader)");
}
