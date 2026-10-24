namespace RimGovernor.Brain.Policy;

// The game's own capacity penalty tables for the four conditions the capability-days ledger prices (#3519, #3520, #3521 of
// #3469): malnutrition, food poisoning, hypothermia and heatstroke. The bridge decodes each hediff's stages from its def
// (Catalog/CapacityPenalties.cs), so the ledger prices a pawn's lost capacities without a hand-chosen number. The brain
// derives no penalty itself; it reads these tables. Hypothermia and heatstroke are priced from the thermal spans (#3521).

/// <summary>One capacity modifier of a hediff stage: a PawnCapacityDef by name with its offset, its ceiling (null when the stage sets none) and its post factor (1 is none).</summary>
public sealed record CapacityModifier(string Capacity, double Offset, double? SetMax, double PostFactor);

/// <summary>One stage of a hediff: the severity it starts at, the pain it adds, its capacity modifiers and the partEfficiencyOffset it adds to its part (#3830).</summary>
public sealed record PenaltyStage(double MinSeverity, double PainOffset, IReadOnlyList<CapacityModifier> Modifiers)
{
    public double PartEfficiencyOffset { get; init; }
}

/// <summary>The <c>HediffCompProperties_Immunizable</c> numbers a course needs: immunity gained a day while sick (before the pawn's <c>ImmunityGainSpeed</c>) and the severity a day without and with full immunity.</summary>
public sealed record ImmunizableProps(double ImmunityPerDaySick, double SeverityPerDayNotImmune, double SeverityPerDayImmune);

/// <summary>
/// One hediff's stages ordered by severity, with its def-level severity data: the severity that kills (null when the def has
/// none), the severity it is given at, the SeverityPerDay comp's rate (null without the comp) and the Disappears comp's
/// range of ticks (null without it). The default is unknown: the def is absent from the catalog, which is not an empty table.
/// </summary>
public sealed record HediffPenaltyTable
{
    public bool Known { get; init; }

    public string Hediff { get; init; } = string.Empty;

    public double? LethalSeverity { get; init; }

    public double InitialSeverity { get; init; }

    public double? SeverityPerDay { get; init; }

    public (double Min, double Max)? DisappearsAfterTicks { get; init; }

    /// <summary>The Immunizable comp's props (null without the comp): a course that waits for immunity (wound infection).</summary>
    public ImmunizableProps? Immunizable { get; init; }

    /// <summary>The TendDuration comp's <c>severityPerDayTended</c> (null without the comp), scaled by the tend quality while tended.</summary>
    public double? SeverityPerDayTended { get; init; }

    public IReadOnlyList<PenaltyStage> Stages { get; init; } = [];

    /// <summary>Reports a known table with finite numbers, at least one stage, and stages in strictly rising severity.</summary>
    public bool Valid()
    {
        if (!Known || Hediff.Length == 0 || Stages.Count == 0 || !double.IsFinite(InitialSeverity)
            || LethalSeverity is { } lethal && !double.IsFinite(lethal)
            || SeverityPerDay is { } rate && !double.IsFinite(rate)
            || DisappearsAfterTicks is { } gone && !(double.IsFinite(gone.Min) && double.IsFinite(gone.Max) && gone.Min <= gone.Max)
            || Immunizable is { } imm && !(double.IsFinite(imm.ImmunityPerDaySick) && double.IsFinite(imm.SeverityPerDayNotImmune) && double.IsFinite(imm.SeverityPerDayImmune))
            || SeverityPerDayTended is { } tended && !double.IsFinite(tended))
        {
            return false;
        }

        var previous = double.NegativeInfinity;
        foreach (var stage in Stages)
        {
            if (!double.IsFinite(stage.MinSeverity) || stage.MinSeverity <= previous || !double.IsFinite(stage.PainOffset)
                || stage.Modifiers.Any(m => m.Capacity.Length == 0 || !double.IsFinite(m.Offset) || !double.IsFinite(m.PostFactor)
                    || m.SetMax is { } max && !double.IsFinite(max)))
            {
                return false;
            }

            previous = stage.MinSeverity;
        }

        return true;
    }

    /// <summary>The stage a severity is in: the last whose MinSeverity it reaches; null at or below 0 (the hediff is absent).</summary>
    public PenaltyStage? StageAt(double severity)
    {
        if (severity <= 0)
        {
            return null;
        }

        PenaltyStage? found = null;
        var stages = Stages;
        for (var i = 0; i < stages.Count; i++)
        {
            if (stages[i].MinSeverity <= severity)
            {
                found = stages[i];
            }
        }

        return found;
    }
}

/// <summary>
/// The penalty tables of the hediffs the ledger prices, and the Moving capacity's minForCapable (the level at or below
/// which the game downs a pawn, <c>PawnCapacitiesHandler.CapableOf</c>). Each is unknown until the catalog supplies it.
/// </summary>
public sealed record CapacityPenaltyTables
{
    public HediffPenaltyTable Malnutrition { get; init; } = new();

    public HediffPenaltyTable FoodPoisoning { get; init; } = new();

    public HediffPenaltyTable Hypothermia { get; init; } = new();

    public HediffPenaltyTable Heatstroke { get; init; } = new();

    /// <summary>The blood loss a bleeding pawn builds (#3544, the harm model); not part of <see cref="Valid"/>, which the ledger's own four tables answer.</summary>
    public HediffPenaltyTable BloodLoss { get; init; } = new();

    /// <summary>The wound infection hediff (#3544).</summary>
    public HediffPenaltyTable WoundInfection { get; init; } = new();

    /// <summary>PawnCapacityDefOf.Moving's minForCapable; null when the catalog lacks the def.</summary>
    public double? MovingMinForCapable { get; init; }

    /// <summary>Reports all four tables known and valid, and the Moving minimum finite.</summary>
    public bool Valid() => Malnutrition.Valid() && FoodPoisoning.Valid() && Hypothermia.Valid() && Heatstroke.Valid() && MovingMinForCapable is { } min && double.IsFinite(min);
}
