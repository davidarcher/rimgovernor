namespace RimGovernor.Brain.Policy;

// The game's own capacity penalty tables for the four conditions the capability-days ledger prices (#3519, #3520, #3521 of
// #3469): malnutrition, food poisoning, hypothermia and heatstroke. The bridge decodes each hediff's stages from its def
// (Catalog/CapacityPenalties.cs), so the ledger prices a pawn's lost capacities without a hand-chosen number. The brain
// derives no penalty itself; it reads these tables. Hypothermia and heatstroke are priced from the thermal spans (#3521).

/// <summary>Whether a capacity modifier caps its capacity: Capped at a ceiling, or Uncapped when the stage sets none.</summary>
public abstract record CapacityCeiling
{
    private CapacityCeiling() { }

    public sealed record Capped(double Max) : CapacityCeiling;

    public sealed record Uncapped : CapacityCeiling;
}

/// <summary>Whether a hediff kills at a severity: Lethal at it (held there), or Harmless when the def has no lethal severity.</summary>
public abstract record Lethality
{
    private Lethality() { }

    public sealed record Lethal(double Severity) : Lethality;

    public sealed record Harmless : Lethality;
}

/// <summary>Whether a hediff's severity moves on its own: Drifting by a rate a day (the SeverityPerDay comp), or Steady without the comp.</summary>
public abstract record SeverityDrift
{
    private SeverityDrift() { }

    public sealed record Drifting(double PerDay) : SeverityDrift;

    public sealed record Steady : SeverityDrift;
}

/// <summary>Whether a hediff ends on its own: EndsAfter a range of ticks (the Disappears comp), or Lasting without the comp.</summary>
public abstract record Lifespan
{
    private Lifespan() { }

    public sealed record EndsAfter(double MinTicks, double MaxTicks) : Lifespan;

    public sealed record Lasting : Lifespan;
}

/// <summary>Whether tending slows a hediff: Tendable at a severity a day (the TendDuration comp's <c>severityPerDayTended</c>, scaled by the tend quality), or Untendable without the comp.</summary>
public abstract record TendEffect
{
    private TendEffect() { }

    public sealed record Tendable(double SeverityPerDayTended) : TendEffect;

    public sealed record Untendable : TendEffect;
}

/// <summary>One capacity modifier of a hediff stage: a PawnCapacityDef by name with its offset, its ceiling and its post factor (1 is none).</summary>
public sealed record CapacityModifier(string Capacity, double Offset, CapacityCeiling Ceiling, double PostFactor);

/// <summary>One stage of a hediff: the severity it starts at, the pain it adds, its capacity modifiers and the partEfficiencyOffset it adds to its part (#3830).</summary>
public sealed record PenaltyStage(double MinSeverity, double PainOffset, IReadOnlyList<CapacityModifier> Modifiers)
{
    public double PartEfficiencyOffset { get; init; }
}

/// <summary>The <c>HediffCompProperties_Immunizable</c> numbers a course needs: immunity gained a day while sick (before the pawn's <c>ImmunityGainSpeed</c>) and the severity a day without and with full immunity.</summary>
public sealed record ImmunizableProps(double ImmunityPerDaySick, double SeverityPerDayNotImmune, double SeverityPerDayImmune);

/// <summary>
/// One hediff's stages ordered by severity, with its def-level severity data: whether it kills at a severity, the severity it
/// is given at, whether the SeverityPerDay comp moves it, whether the Disappears comp ends it and whether the TendDuration
/// comp lets tending slow it. The default is unknown: the def is absent from the catalog, which is not an empty table.
/// </summary>
public sealed record HediffPenaltyTable
{
    public bool Known { get; init; }

    public string Hediff { get; init; } = string.Empty;

    public Lethality Lethality { get; init; } = new Lethality.Harmless();

    public double InitialSeverity { get; init; }

    public SeverityDrift Drift { get; init; } = new SeverityDrift.Steady();

    public Lifespan Lifespan { get; init; } = new Lifespan.Lasting();

    /// <summary>The Immunizable comp's props (null without the comp): a course that waits for immunity (wound infection).</summary>
    public ImmunizableProps? Immunizable { get; init; }

    /// <summary>Whether tending slows the hediff, by the TendDuration comp's rate scaled by the tend quality while tended.</summary>
    public TendEffect Tending { get; init; } = new TendEffect.Untendable();

    public IReadOnlyList<PenaltyStage> Stages { get; init; } = [];

    /// <summary>Reports a known table with finite numbers, at least one stage, and stages in strictly rising severity.</summary>
    public bool Valid()
    {
        if (!Known || Hediff.Length == 0 || Stages.Count == 0 || !double.IsFinite(InitialSeverity)
            || Lethality is Lethality.Lethal { Severity: var lethal } && !double.IsFinite(lethal)
            || Drift is SeverityDrift.Drifting { PerDay: var rate } && !double.IsFinite(rate)
            || Lifespan is Lifespan.EndsAfter { MinTicks: var min, MaxTicks: var max } && !(double.IsFinite(min) && double.IsFinite(max) && min <= max)
            || Immunizable is { } imm && !(double.IsFinite(imm.ImmunityPerDaySick) && double.IsFinite(imm.SeverityPerDayNotImmune) && double.IsFinite(imm.SeverityPerDayImmune))
            || Tending is TendEffect.Tendable { SeverityPerDayTended: var tended } && !double.IsFinite(tended))
        {
            return false;
        }

        var previous = double.NegativeInfinity;
        foreach (var stage in Stages)
        {
            if (!double.IsFinite(stage.MinSeverity) || stage.MinSeverity <= previous || !double.IsFinite(stage.PainOffset)
                || stage.Modifiers.Any(m => m.Capacity.Length == 0 || !double.IsFinite(m.Offset) || !double.IsFinite(m.PostFactor)
                    || m.Ceiling is CapacityCeiling.Capped { Max: var cap } && !double.IsFinite(cap)))
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

    /// <summary>PawnCapacityDefOf.Moving's minForCapable, read with the tables (the catalog throws without the def); meaningful only beside known tables.</summary>
    public double MovingMinForCapable { get; init; }

    /// <summary>Reports all four tables known and valid, and the Moving minimum finite.</summary>
    public bool Valid() => Malnutrition.Valid() && FoodPoisoning.Valid() && Hypothermia.Valid() && Heatstroke.Valid() && double.IsFinite(MovingMinForCapable);
}
