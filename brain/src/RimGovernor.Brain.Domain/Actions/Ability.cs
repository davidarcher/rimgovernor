namespace RimGovernor.Brain.Domain;

/// <summary>Names where an ability comes from (Go <c>AbilitySourceKind</c>).</summary>
public enum AbilitySourceKind
{
    /// <summary>An acting royal permit (royalAid) the pawn holds with a faction.</summary>
    Permit,

    /// <summary>A psycast (AbilityDef) the pawn knows.</summary>
    Psycast,
}

/// <summary>What an ability aims at (Go <c>AbilityTargetKind</c>).</summary>
public enum AbilityTargetKind
{
    None,
    Cell,
    Pawn,
    Thing,
}

/// <summary>
/// An immutable value. Def is the permit's RoyalTitlePermitDef defName; Faction is the faction def the permit is
/// held with.
/// </summary>
public sealed record AbilitySource
{
    private AbilitySource(AbilitySourceKind kind, string faction, string def)
    {
        Kind = kind;
        Faction = faction;
        Def = def;
    }

    public AbilitySourceKind Kind { get; }

    /// <summary>The faction def of a permit source, empty for other sources.</summary>
    public string Faction { get; }

    /// <summary>The source's ability def: the permit def of a permit source, the AbilityDef of a psycast source.</summary>
    public string Def { get; }

    /// <summary>The permit source for a faction def and a permit def.</summary>
    public static AbilitySource PermitSource(string faction, string permit)
    {
        DomainException.Require(
            Identity.IsValidText(faction) && Identity.IsValidText(permit) && !faction.Contains(':') && !permit.Contains(':'),
            "ability permit source requires a faction def and a permit def"
        );
        return new AbilitySource(AbilitySourceKind.Permit, faction, permit);
    }

    /// <summary>The psycast source for an AbilityDef defName.</summary>
    public static AbilitySource PsycastSource(string ability)
    {
        DomainException.Require(Identity.IsValidText(ability) && !ability.Contains(':'), "ability psycast source requires an ability def");
        return new AbilitySource(AbilitySourceKind.Psycast, "", ability);
    }

    /// <summary>The source's stable single-string form, "permit:&lt;faction&gt;:&lt;permit&gt;" or "psycast:&lt;abilityDef&gt;".</summary>
    public string Key =>
        Kind switch
        {
            AbilitySourceKind.Permit => Wire.Name(Kind) + ":" + Faction + ":" + Def,
            AbilitySourceKind.Psycast => Wire.Name(Kind) + ":" + Def,
            _ => "",
        };

    /// <summary><see cref="Key"/>'s inverse.</summary>
    public static AbilitySource ParseAbilitySource(string key)
    {
        var parts = key.Split(':');
        if (parts.Length == 3 && parts[0] == Wire.Name(AbilitySourceKind.Permit))
        {
            return PermitSource(parts[1], parts[2]);
        }
        if (parts.Length == 2 && parts[0] == Wire.Name(AbilitySourceKind.Psycast))
        {
            return PsycastSource(parts[1]);
        }
        throw new DomainException($"invalid ability source \"{key}\"");
    }

    /// <summary>
    /// Whether the source can take a target of the kind. A permit calls aid, a strike or a drop at a cell, or
    /// takes no target. A psycast takes whichever arm the ability needs (none for a self-cast); native checks that
    /// the arm is the one this ability takes.
    /// </summary>
    internal bool Accepts(AbilityTargetKind target) =>
        Kind switch
        {
            AbilitySourceKind.Permit => target is AbilityTargetKind.None or AbilityTargetKind.Cell,
            AbilitySourceKind.Psycast => true,
            _ => false,
        };
}

/// <summary>An immutable value: none, a cell, or the load id of a pawn or thing.</summary>
public sealed record AbilityTarget
{
    private AbilityTarget(AbilityTargetKind kind, Cell cell, string id)
    {
        Kind = kind;
        Cell = cell;
        Id = id;
    }

    public AbilityTargetKind Kind { get; }

    /// <summary>The target cell of a cell target.</summary>
    public Cell Cell { get; }

    /// <summary>The load id of a pawn or thing target.</summary>
    public string Id { get; }

    public static AbilityTarget NoAbilityTarget() => new(AbilityTargetKind.None, default, "");

    public static AbilityTarget AbilityCellTarget(Cell cell)
    {
        DomainException.Require(cell.X >= 0 && cell.Z >= 0, "invalid ability target cell");
        return new AbilityTarget(AbilityTargetKind.Cell, cell, "");
    }

    public static AbilityTarget AbilityPawnTarget(PawnId pawn)
    {
        DomainException.Require(pawn.IsValid, "invalid ability target pawn");
        return new AbilityTarget(AbilityTargetKind.Pawn, default, pawn.Value);
    }

    public static AbilityTarget AbilityThingTarget(string thing)
    {
        DomainException.Require(Identity.IsValidText(thing), "invalid ability target thing");
        return new AbilityTarget(AbilityTargetKind.Thing, default, thing);
    }
}

/// <summary>
/// Invokes one pawn ability through Actions/Apply. Native guards own eligibility, including favor, cooldown,
/// range and hostility. Applied proves use and cooldown start; arrivals and combat effects require separate
/// observations. One pawn using one ability source on one target.
/// </summary>
public sealed record Ability : IActionPayload
{
    /// <summary>Go <c>NewAbility</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Ability(PawnId pawn, AbilitySource source, AbilityTarget target)
    {
        DomainException.Require(pawn.IsValid, "ability requires a valid pawn identity");
        DomainException.Require(
            source.Accepts(target.Kind),
            $"a {Wire.Name(source.Kind)} ability does not take a {Wire.Name(target.Kind)} target"
        );
        Pawn = pawn;
        Source = source;
        Target = target;
    }

    public PawnId Pawn { get; }

    public AbilitySource Source { get; }

    public AbilityTarget Target { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewAbilityAction(ActionId id, Ability ability) => Create(id, ActionKind.Ability, ability);

    public Ability? Ability => PayloadOf<Ability>(ActionKind.Ability);
}
