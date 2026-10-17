using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the per-pawn reading, medicine carry and ideoligion planners compose from. Traits, mood and break thresholds and
/// animal bonds are read elsewhere on the pawn row. Empty strings are "none".
/// </summary>
public sealed record PawnPolicyInputs
{
    public string ReadingPolicy { get; init; } = "";

    /// <summary>The pawn's carry settings (medicine).</summary>
    public IReadOnlyList<InventoryStock> InventoryStock { get; init; } = [];

    /// <summary>The ideoligion load id.</summary>
    public string Ideo { get; init; } = "";

    /// <summary>Every precept defName of the ideoligion.</summary>
    public IReadOnlyList<string> Precepts { get; init; } = [];

    /// <summary>The pawn's role precept defName.</summary>
    public string IdeoRole { get; init; } = "";

    /// <summary>The pawn's certainty (0..1) in Ideo; unknown without an ideoligion.</summary>
    public Fact<double> IdeoCertainty { get; init; }

    /// <summary>Guest, Prisoner or Slave; null for a free pawn.</summary>
    public RimGovernor.Contracts.Wire.Defs.GuestStatus? GuestStatus { get; init; }
}

public sealed record InventoryStock(string Group, int Count);
