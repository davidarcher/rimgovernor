namespace RimGovernor.Brain.Policy;

/// <summary>
/// One ancient cryptosleep casket. HasContents is the native fact; who is inside stays unknown until the
/// casket opens. A casket under 20% hit points explodes, so HitPoints is a safety reading, not trivia.
/// InteractionCell is where a pawn stands to open it and where the ejected occupant lands.
/// </summary>
public sealed record ShrineCasket
{
    public string EntityId { get; init; } = "";

    public Cell Cell { get; init; }

    public Cell InteractionCell { get; init; }

    public uint HitPoints { get; init; }

    public bool HasContents { get; init; }

    public bool PlayerClaimed { get; init; }
}

public sealed record ShrineGuard
{
    public string EntityId { get; init; } = "";

    /// <summary>What a breached shrine released (a ShrineGuard* constant).</summary>
    public string Kind { get; init; } = "";

    public bool Downed { get; init; }

    public bool Dead { get; init; }
}

/// <summary>
/// A perimeter wall the player may deconstruct without a roof-support blocker; Outside is the adjacent cell
/// beyond the room and DefName the wall's definition (empty from a native before it was read).
/// </summary>
public sealed record ShrineBreachWall
{
    public string EntityId { get; init; } = "";

    public string DefName { get; init; } = "";

    public Cell Cell { get; init; }

    public Cell Outside { get; init; }
}

/// <summary>
/// One ancient-danger room as observed. Guards is complete only when GuardsKnown; a sealed shrine never knows
/// its guards. Nothing here admits a breach: readiness and the breach goal decide.
/// </summary>
public sealed record AncientShrine
{
    public string Id { get; init; } = "";

    public Cell Minimum { get; init; }

    public Cell Maximum { get; init; }

    public bool Sealed { get; init; }

    public bool InHome { get; init; }

    public bool GuardsKnown { get; init; }

    public EquatableArray<ShrineCasket> Caskets { get; init; }

    public EquatableArray<ShrineGuard> Guards { get; init; }

    public EquatableArray<ShrineBreachWall> BreachWalls { get; init; }

    /// <summary>The humanlikes the caskets released, hostile or not, and their corpses; empty while the caskets are sealed.</summary>
    public EquatableArray<ShrineOccupant> Occupants { get; init; }

    /// <summary>
    /// Whether any observed guard still stands or lies downed. Unknown guards (a sealed shrine) count as
    /// alive: the room has not been cleared until it has been seen.
    /// </summary>
    public bool GuardsAlive()
    {
        if (!GuardsKnown)
        {
            return true;
        }
        foreach (var guard in Guards)
        {
            if (!guard.Dead)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Counts the caskets that still hold something.</summary>
    public int FilledCaskets() => Caskets.Count(c => c.HasContents);
}

public static partial class PolicyFunctions
{
    // ShrineGuardKind classifies what a breached shrine released.
    public const string ShrineGuardMechanoid = "mechanoid";
    public const string ShrineGuardInsectoid = "insectoid";
    public const string ShrineGuardFleshbeast = "fleshbeast";
    public const string ShrineGuardHuman = "human";
    public const string ShrineGuardHive = "hive";
    public const string ShrineGuardOther = "other";
}
