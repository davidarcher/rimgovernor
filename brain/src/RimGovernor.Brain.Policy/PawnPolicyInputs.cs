using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the per-pawn outfit/drug/food/reading planners compose from. Traits, mood and break thresholds and
/// animal bonds are read elsewhere on the pawn row. Empty strings are "none".
/// </summary>
public sealed record PawnPolicyInputs
{
    public string OutfitPolicy { get; init; } = "";

    public string DrugPolicy { get; init; } = "";

    public string ReadingPolicy { get; init; } = "";

    /// <summary>The pawn's carry settings (medicine).</summary>
    public IReadOnlyList<InventoryStock> InventoryStock { get; init; } = [];

    /// <summary>Each chemical the pawn is addicted to or tolerant of.</summary>
    public IReadOnlyList<ChemicalState> Chemicals { get; init; } = [];

    /// <summary>The chemicals of chemical-dependency genes.</summary>
    public IReadOnlyList<string> DependencyChemicals { get; init; } = [];

    public string RoyalTitle { get; init; } = "";

    /// <summary>The ideoligion load id.</summary>
    public string Ideo { get; init; } = "";

    /// <summary>Every precept defName of the ideoligion.</summary>
    public IReadOnlyList<string> Precepts { get; init; } = [];

    /// <summary>The pawn's role precept defName.</summary>
    public string IdeoRole { get; init; } = "";

    /// <summary>The pawn's certainty (0..1) in Ideo; unknown without an ideoligion.</summary>
    public Fact<double> IdeoCertainty { get; init; }

    public IReadOnlyList<ApparelRequirement> RoleApparel { get; init; } = [];

    public IReadOnlyList<string> PreceptApparel { get; init; } = [];

    /// <summary>Guest, Prisoner or Slave; empty for a free pawn.</summary>
    public string GuestStatus { get; init; } = "";

    public string PrisonerInteraction { get; init; } = "";

    public string SlaveInteraction { get; init; } = "";

    /// <summary>The MedicalTendQuality stat; unknown for a pawn that cannot doctor.</summary>
    public Fact<double> TendQuality { get; init; }
}

public sealed record InventoryStock(string Group, string Thing, int Count);

public sealed record ChemicalState
{
    public string Chemical { get; init; } = "";

    public Fact<double> Addiction { get; init; }

    public bool Withdrawal { get; init; }

    public Fact<double> Tolerance { get; init; }
}

public sealed record ApparelRequirement
{
    public IReadOnlyList<string> BodyPartGroups { get; init; } = [];

    public IReadOnlyList<string> RequiredDefs { get; init; } = [];

    public IReadOnlyList<string> RequiredTags { get; init; } = [];

    public IReadOnlyList<string> AllowedTags { get; init; } = [];
}
