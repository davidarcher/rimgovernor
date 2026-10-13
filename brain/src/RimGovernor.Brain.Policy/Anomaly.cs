namespace RimGovernor.Brain.Policy;

// Go: anomaly.go

/// <summary>The order given for a held entity (EntityContainmentMode on the wire): keep it, study it, release it or execute it.</summary>
public enum ContainmentMode
{
    [WireName("")]
    None,
    MaintainOnly,
    Study,
    Release,
    Execute,
}

/// <summary>
/// One pawn's Anomaly facts as its row carries them; every field is unknown when native did not read it.
/// Hostility is the pawn row's own. A null inside a Known fact is a known absence (a pawn that is no
/// holding-platform target, nothing studiable). The static facts (what an entity needs, what a study yields)
/// are the catalog's (bridge.AnomalyCatalog).
/// </summary>
public sealed record PawnAnomaly
{
    /// <summary>Pawn.IsEntity.</summary>
    public Fact<bool> Entity { get; init; }

    /// <summary>Pawn.IsMutant (a shambler is one).</summary>
    public Fact<bool> Mutant { get; init; }

    /// <summary>Pawn.IsShambler.</summary>
    public Fact<bool> Shambler { get; init; }

    /// <summary>The containment strength an entity needs to stay held; unknown for a pawn that is no entity.</summary>
    public Fact<double> MinContainmentStrength { get; init; }

    public Fact<EntityHeld?> Held { get; init; }

    public Fact<StudyState?> Study { get; init; }

    /// <summary>A pawn the player cannot see or target (an unrevealed sightstealer).</summary>
    public Fact<bool> HiddenFromPlayer { get; init; }

    /// <summary>The caster of its lord's psychic ritual.</summary>
    public Fact<bool> PsychicRitualInvoker { get; init; }

    /// <summary>A pawn whose attack is melee with no offensive ability.</summary>
    public Fact<bool> MeleeOnly { get; init; }

    /// <summary>The pawn's creepjoiner tracker facts; a null inside a Known fact is a pawn that is no creepjoiner.</summary>
    public Fact<CreepJoiner?> CreepJoiner { get; init; }
}

/// <summary>
/// A creepjoiner's visible facts: its form and benefit def names and whether the game has fired its downside.
/// The downside def is hidden information and is never read; what a player can see of it is the pawn's traits
/// and hediffs.
/// </summary>
public sealed record CreepJoiner
{
    public Fact<string> Form { get; init; }

    public Fact<string> Benefit { get; init; }

    public Fact<bool> DownsideTriggered { get; init; }
}

/// <summary>A holding-platform target's state: whether it is held now, on which platform (known "" when on none) and under which order.</summary>
public sealed record EntityHeld
{
    public Fact<bool> Held { get; init; }

    public Fact<bool> Escaping { get; init; }

    public Fact<bool> ExtractBioferrite { get; init; }

    public Fact<bool> CanBeCaptured { get; init; }

    /// <summary>HasHediffsNeedingTend.</summary>
    public Fact<bool> NeedsTend { get; init; }

    /// <summary>A positive bleed rate.</summary>
    public Fact<bool> Bleeding { get; init; }

    /// <summary>The platform's HasAttachedBioferriteHarvester.</summary>
    public Fact<bool> HarvesterAttached { get; init; }

    /// <summary>CompProducesBioferrite.BioferritePerDay, 0 while the BioferriteExtracted hediff stands.</summary>
    public Fact<double> BioferritePerDay { get; init; }

    public Fact<string> Platform { get; init; }

    public Fact<ContainmentMode> Mode { get; init; }
}

/// <summary>
/// A studiable thing's CompStudiable state. ProgressPercent is 0..1; KnowledgeCategory and AnomalyKnowledge are
/// the values the game resolves for this thing; KnowledgeGained is what its study has yielded.
/// </summary>
public sealed record StudyState
{
    public Fact<bool> StudyEnabled { get; init; }

    public Fact<bool> Completed { get; init; }

    public Fact<bool> EverStudiable { get; init; }

    public Fact<bool> CurrentlyStudiable { get; init; }

    public Fact<double> ProgressPercent { get; init; }

    public Fact<double> StudyPoints { get; init; }

    public Fact<double> KnowledgeGained { get; init; }

    public Fact<double> AnomalyKnowledge { get; init; }

    public Fact<int> Interactions { get; init; }

    public Fact<string> KnowledgeCategory { get; init; }
}

/// <summary>
/// One building's Anomaly facts: a holding platform's containment and a studiable building's study state.
/// Unknown when the row carries none; a null inside a Known fact is a known absence.
/// </summary>
public sealed record BuildingAnomaly
{
    public Fact<EntityHolder?> Holder { get; init; }

    public Fact<StudyState?> Study { get; init; }
}

/// <summary>A CompEntityHolder: the containment strength it provides now, whether it can take a pawn and the pawn it holds (known "" when none).</summary>
public sealed record EntityHolder
{
    public Fact<double> ContainmentStrength { get; init; }

    public Fact<bool> Available { get; init; }

    public string HeldPawn { get; init; } = "";

    /// <summary>The doors the game counts for the holder's room; unknown when the door read failed, empty for a room with none.</summary>
    public Fact<EquatableArray<ContainmentDoor>> Doors { get; init; }
}

/// <summary>
/// One door of a holder's room: Open and HoldOpen are the Building_Door's, Breached is ContainmentBreached
/// (open 600 ticks or more, which zeroes the room's door strength) and BlockedOpen is BlockedOpenMomentary (a
/// thing or pawn in the doorway keeps it from closing).
/// </summary>
public sealed record ContainmentDoor
{
    public Cell Cell { get; init; }

    public Fact<bool> Open { get; init; }

    public Fact<bool> HoldOpen { get; init; }

    public Fact<bool> Breached { get; init; }

    public Fact<bool> BlockedOpen { get; init; }
}
