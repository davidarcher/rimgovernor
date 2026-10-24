namespace RimGovernor.Brain.Policy;

/// <summary>
/// The role an interior is planned and furnished for: a game room role (RoomRoleDef), or a role the game has no
/// RoomRoleDef for. A suite and an isolation room are plan roles only: the game scores a furnished one a Bedroom.
/// A piece with no room family (no work table role, not a humanlike bed) carries <see cref="NoFamily"/>.
/// </summary>
public readonly record struct PlanRole
{
    private enum Kind
    {
        Game,
        Suite,
        IsolationRoom,
        NoFamily,
    }

    private readonly Kind _kind;
    private readonly RoomRole _game;

    private PlanRole(Kind kind, RoomRole game)
    {
        _kind = kind;
        _game = game;
    }

    /// <summary>A suite: RimWorld has no Suite RoomRoleDef, so the native census reports a furnished suite as a Bedroom.</summary>
    public static PlanRole Suite { get; } = new(Kind.Suite, RoomRole.None);

    /// <summary>An isolation room: the game scores the furnished room a bedroom, so it has no facility row.</summary>
    public static PlanRole IsolationRoom { get; } = new(Kind.IsolationRoom, RoomRole.None);

    /// <summary>The family of a piece the game scores into no room role.</summary>
    public static PlanRole NoFamily { get; } = new(Kind.NoFamily, RoomRole.None);

    public static implicit operator PlanRole(RoomRole role) => new(Kind.Game, role);

    /// <summary>The game room role this plan role is, false for a plan-only role.</summary>
    public bool IsGame(out RoomRole role)
    {
        role = _game;
        return _kind == Kind.Game;
    }

    /// <summary>The wire name: the RoomRoleDef defName of a game role, else the plan role's own name.</summary>
    public string Name => _kind == Kind.Game ? Wire.Name(_game) : _kind == Kind.NoFamily ? "" : _kind.ToString();

    public override string ToString() => Name;
}
