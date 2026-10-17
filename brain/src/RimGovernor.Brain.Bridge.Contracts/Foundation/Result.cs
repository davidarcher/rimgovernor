namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// The complete receipt at the transport boundary. <see cref="Structured"/> is untrusted wire
/// JSON text until a consumer decodes its generated contract. <see cref="Empty"/> is the empty receipt.
/// </summary>
public sealed record Result
{
    public static readonly Result Empty = new();

    /// <summary>The raw envelope as valid JSON text; empty when none.</summary>
    public string Envelope { get; init; } = "";

    /// <summary>The structured content as valid JSON text; empty when none.</summary>
    public string Structured { get; init; } = "";

    public EquatableArray<string> Text { get; init; }
}
