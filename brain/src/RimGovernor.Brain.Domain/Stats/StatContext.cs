namespace RimGovernor.Brain.Domain;

// The live state of a thing request that the defs do not hold (Go stateval.StatContext). Every field is a
// Fact: an unstated fact is Unknown and a stat that reads it fails with StatException naming it, never a
// default. Only the facts a caller states or a fake needs are modelled here; the game-thread service (#3080)
// reads the rest from the thing itself, and hypothetical pawns (combatsim) state the pawn facts below.

/// <summary>The live state of the thing a request is about; a null context on the subject is a definition request.</summary>
public sealed record StatContext
{
    /// <summary>The pawn's state; null when the thing is not a Pawn.</summary>
    public PawnStatState? Pawn { get; init; }

    /// <summary>The inner pawn's state when the thing is a Corpse (<see cref="Pawn"/> is then null).</summary>
    public PawnStatState? Corpse { get; init; }

    public Fact<bool> Spawned { get; init; }

    public Fact<bool> Roofed { get; init; }

    public Fact<float> GlowLevel { get; init; }

    public Fact<float> Temperature { get; init; }

    /// <summary>The QualityCategory (0 awful to 6 legendary) from <c>Thing.TryGetQuality</c>; -1 is a thing with no CompQuality (Normal).</summary>
    public Fact<int> Quality { get; init; }

    public Fact<int> HitPoints { get; init; }

    public Fact<int> MaxHitPoints { get; init; }

    /// <summary><c>Thing.GetStatValue</c> of this very thing by StatDef name, for stats a class derives from another stat of the thing.</summary>
    public IReadOnlyDictionary<string, float> Stats { get; init; } = new Dictionary<string, float>();
}

/// <summary>The pawn facts stat parts read.</summary>
public sealed record PawnStatState
{
    /// <summary>Biological age in ticks.</summary>
    public Fact<long> AgeBiologicalTicks { get; init; }

    public Fact<bool> Female { get; init; }

    /// <summary>The LifeStageDef name of the current life stage.</summary>
    public Fact<string> LifeStage { get; init; }

    public Fact<string> KindDef { get; init; }

    /// <summary><c>pawn.skills</c> levels by SkillDef name; Known(empty) is a pawn without skills.</summary>
    public Fact<IReadOnlyDictionary<string, int>> SkillLevels { get; init; }

    /// <summary><c>health.capacities.GetLevel</c> by PawnCapacityDef name.</summary>
    public Fact<IReadOnlyDictionary<string, float>> Capacities { get; init; }

    public Fact<EquatableArray<StatTrait>> Traits { get; init; }

    public Fact<EquatableArray<StatHediff>> Hediffs { get; init; }

    /// <summary>Active, non-overridden gene def names in order.</summary>
    public Fact<EquatableArray<string>> Genes { get; init; }

    /// <summary>PreceptDef names of the pawn's ideo; Known(empty) is no ideo.</summary>
    public Fact<EquatableArray<string>> Precepts { get; init; }

    public Fact<float> PainTotal { get; init; }

    public Fact<float> MoodLevel { get; init; }

    /// <summary>The mutant def name; Known("") is not a mutant.</summary>
    public Fact<string> MutantDef { get; init; }

    public Fact<bool> IsSlave { get; init; }
}

/// <summary>One trait of <c>pawn.story.traits</c>.</summary>
public sealed record StatTrait(string Def, int Degree, bool Suppressed = false);

/// <summary>One hediff of <c>pawn.health.hediffSet.hediffs</c>.</summary>
public sealed record StatHediff(string Def, float Severity, bool Permanent = false);
