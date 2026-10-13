namespace RimGovernor.Brain.Domain;

/// <summary>One rectangle of cleared ground: Width x Height cells from Origin. Go <c>GroundRect</c>.</summary>
public readonly partial record struct GroundRect(Cell Origin, int Width, int Height);

/// <summary>
/// Designates one observed building (a DECONSTRUCT Designate). Native safety and identity are checked at
/// apply; applied means designated.
/// </summary>
public sealed record Deconstruction : IActionPayload
{
    /// <summary>Go <c>NewDeconstruction</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Deconstruction(string target, string definition, Cell cell)
    {
        DomainException.Require(
            Identity.IsValidText(target) && Identity.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0,
            "invalid deconstruction identity or cell"
        );
        Target = target;
        Definition = definition;
        Cell = cell;
    }

    public string Target { get; }

    public string Definition { get; }

    public Cell Cell { get; }

    /// <summary>Reports a door-to-wall swap (Go <c>ReplacesWithWall</c>).</summary>
    public bool ReplacesWithWall { get; private init; }

    /// <summary>
    /// The deconstruction swapping its door for a wall of the door's stuff: native places the wall blueprint
    /// and orders a builder through both jobs.
    /// </summary>
    public Deconstruction WithWallReplacement() => this with { ReplacesWithWall = true };
}

public sealed partial record DomainAction
{
    public static DomainAction NewDeconstructionAction(ActionId id, Deconstruction deconstruction) =>
        Create(id, ActionKind.Deconstruction, deconstruction);

    /// <summary>Go <c>Action.Deconstruction() (Deconstruction, bool)</c>: the payload when this is a deconstruction action, otherwise null.</summary>
    public Deconstruction? Deconstruction => PayloadOf<Deconstruction>(ActionKind.Deconstruction);
}
