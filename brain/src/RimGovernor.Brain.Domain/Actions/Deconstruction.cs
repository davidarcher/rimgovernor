namespace RimGovernor.Brain.Domain;

/// <summary>One rectangle of cleared ground: Width x Height cells from Origin.</summary>
public readonly partial record struct GroundRect(Cell Origin, int Width, int Height);

/// <summary>
/// Designates one observed building (a DECONSTRUCT Designate). Native safety and identity are checked at
/// apply; applied means designated.
/// </summary>
public sealed record Deconstruction : IActionPayload
{
    /// <summary>Throws <see cref="DomainException"/> when invalid.</summary>
    public Deconstruction(string target, string definition, Cell cell)
    {
        DomainException.Require(
            IdText.IsValidText(target) && IdText.IsValidText(definition) && cell.X >= 0 && cell.Z >= 0,
            "invalid deconstruction identity or cell"
        );
        Target = target;
        Definition = definition;
        Cell = cell;
    }

    public string Target { get; }

    public string Definition { get; }

    public Cell Cell { get; }

    /// <summary>Reports a door-to-wall swap.</summary>
    public bool ReplacesWithWall { get; private init; }

    /// <summary>
    /// The deconstruction swapping its door for a wall of the door's stuff: native places the wall blueprint
    /// and orders a builder through both jobs.
    /// </summary>
    public Deconstruction WithWallReplacement() => this with { ReplacesWithWall = true };

    /// <summary>Reports the removal of a blueprint: cancelled (THING_DESIGNATION_CANCEL), since nothing is built to take down.</summary>
    public bool CancelsBlueprint { get; private init; }

    /// <summary>
    /// The removal of the player blueprint <see cref="Target"/> (#3742): native cancels it through the game's cancel
    /// designator. A frame is deconstructed instead, so its delivered materials come back.
    /// </summary>
    public Deconstruction WithBlueprintCancel() => this with { CancelsBlueprint = true };
}

public sealed partial record DomainAction
{
    public static DomainAction NewDeconstructionAction(ActionId id, Deconstruction deconstruction) =>
        Create(id, ActionKind.Deconstruction, deconstruction);

    /// <summary>The payload when this is a deconstruction action, otherwise null.</summary>
    public Deconstruction? Deconstruction => PayloadOf<Deconstruction>(ActionKind.Deconstruction);
}
