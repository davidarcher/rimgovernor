namespace RimGovernor.Brain.Domain;

/// <summary>A grid cell. Go <c>Cell</c>.</summary>
public readonly record struct Cell(int X, int Z);

public enum Rotation
{
    North,
    East,
    South,
    West,
}

/// <summary>
/// Marks the per-family payload records (<c>Haul</c>, <c>Tend</c>, ...) that a <see cref="DomainAction"/> carries.
/// Payloads are immutable records with value equality; a slice field is an <see cref="EquatableArray{T}"/>.
/// </summary>
public interface IActionPayload;

/// <summary>
/// One resolved placement. Native discovery owns definition existence, footprint, map bounds, costs and
/// placement legality; these are not inferred here. (Go <c>NewBuilding</c> is the constructor.)
/// </summary>
public sealed record Building
{
    public Building(string definition, Cell cell, Rotation rotation, string stuff)
    {
        DomainException.Require(Identity.IsNativeText(definition, allowEmpty: false) && Identity.IsNativeText(stuff, allowEmpty: true), "invalid native definition or stuff text");
        DomainException.Require(cell.X >= 0 && cell.Z >= 0, "building anchor must be nonnegative");
        DomainException.Require(Wire.IsDefined(rotation), "invalid building rotation");
        Definition = definition;
        Cell = cell;
        Rotation = rotation;
        Stuff = stuff;
    }

    public string Definition { get; }

    public Cell Cell { get; }

    public Rotation Rotation { get; }

    /// <summary>Empty Stuff requests the native default material selection.</summary>
    public string Stuff { get; }
}

/// <summary>
/// The rung of the construction ladder a blueprint or frame is built for (#2522). Absent means ungated
/// vanilla construction.
/// </summary>
public enum ConstructionTier
{
    Survive,
    Sustain,
    Comfort,
    Produce,
    Expand,
    Secure,
}

/// <summary>
/// The payload of a <see cref="ActionKind.Building"/> action: the placement plus the construction settings
/// Go kept as extra fields on <c>Action</c> (finishing skill, tier, replace-wall, construction target).
/// </summary>
public sealed record BuildingOrder(
    Building Building,
    Fact<int> FinishingSkill,
    string ConstructionTarget,
    bool ReplaceWall,
    Fact<ConstructionTier> Tier
) : IActionPayload;

/// <summary>
/// An action is a closed variant: a kind plus the payload record of that kind's family. It can only be built
/// through the validated <c>New...Action</c> factories (one per family, declared beside the payload), so it is
/// always canonical; there is no zero value. (Go <c>Action</c>; renamed to avoid <see cref="System.Action"/>.)
/// </summary>
public sealed partial record DomainAction
{
    private DomainAction(ActionId id, ActionKind kind, IActionPayload payload)
    {
        Id = id;
        Kind = kind;
        Payload = payload;
    }

    public ActionId Id { get; }

    public ActionKind Kind { get; }

    internal IActionPayload Payload { get; }

    /// <summary>Builds an action of <paramref name="kind"/>; the family factories call this after validating the payload.</summary>
    internal static DomainAction Create(ActionId id, ActionKind kind, IActionPayload payload)
    {
        DomainException.Require(id.IsValid, "invalid action identity");
        return new DomainAction(id, kind, payload);
    }

    /// <summary>The payload when this action is of <paramref name="kind"/> and carries a <typeparamref name="T"/>; otherwise null.</summary>
    internal T? PayloadOf<T>(ActionKind kind)
        where T : class, IActionPayload => Kind == kind ? Payload as T : null;

    private DomainAction WithPayload(IActionPayload payload) => new(Id, Kind, payload);

    /// <summary>
    /// A build order at a construction tier (#2525). The tier is a required argument: every planner states where
    /// its build stands on the ladder, and Expand is passed explicitly by the ones the ladder does not list.
    /// </summary>
    public static DomainAction NewBuildingAction(ActionId id, Building building, ConstructionTier tier)
    {
        DomainException.Require(id.IsValid, "invalid action identity");
        DomainException.Require(Wire.IsDefined(tier), "invalid construction tier");
        return Create(id, ActionKind.Building, new BuildingOrder(building, default, "", false, Known(tier)));
    }

    public Building? Building => PayloadOf<BuildingOrder>(ActionKind.Building)?.Building;

    private BuildingOrder BuildingOrderOrThrow(string message) =>
        PayloadOf<BuildingOrder>(ActionKind.Building) ?? throw new DomainException(message);

    /// <summary>
    /// Attaches a target's once-chosen construction setting. An existing target is an exact-ID adoption, never
    /// permission to replace it.
    /// </summary>
    public DomainAction WithFinishingSkill(int minimum, string target)
    {
        const string Message = "invalid construction finishing skill";
        var order = BuildingOrderOrThrow(Message);
        DomainException.Require(minimum is >= 0 and <= 1000 && Identity.IsNativeText(target, allowEmpty: true), Message);
        return WithPayload(order with { FinishingSkill = Known(minimum), ConstructionTarget = target });
    }

    public Fact<int> FinishingSkill => PayloadOf<BuildingOrder>(ActionKind.Building)?.FinishingSkill ?? default;

    public string ConstructionTarget => PayloadOf<BuildingOrder>(ActionKind.Building)?.ConstructionTarget ?? "";

    /// <summary>
    /// Attaches the construction tier. A non-empty target re-tiers that exact blueprint or frame (an exact-ID
    /// adoption, the set-tier operation); it must agree with any target the finishing skill already names.
    /// </summary>
    public DomainAction WithTier(ConstructionTier tier, string target)
    {
        const string Message = "invalid construction tier";
        var order = BuildingOrderOrThrow(Message);
        DomainException.Require(
            Wire.IsDefined(tier)
                && Identity.IsNativeText(target, allowEmpty: true)
                && !(order.ConstructionTarget.Length != 0 && target.Length != 0 && target != order.ConstructionTarget),
            Message
        );
        return WithPayload(order with { Tier = Known(tier), ConstructionTarget = target.Length != 0 ? target : order.ConstructionTarget });
    }

    public Fact<ConstructionTier> Tier => PayloadOf<BuildingOrder>(ActionKind.Building)?.Tier ?? default;

    /// <summary>
    /// Marks a building action as an in-place swap of the built wall at the cell (#2529): a vanilla replacement
    /// blueprint, the cell impassable at every tick of the swap. Native refuses when no built wall stands there,
    /// placement is refused, or the swap would drop a roof's last holder. It never names an existing blueprint
    /// or frame to adopt.
    /// </summary>
    public DomainAction WithWallReplacement()
    {
        const string Message = "invalid wall replacement";
        var order = BuildingOrderOrThrow(Message);
        DomainException.Require(order.ConstructionTarget.Length == 0, Message);
        return WithPayload(order with { ReplaceWall = true });
    }

    public bool ReplacesWall => PayloadOf<BuildingOrder>(ActionKind.Building)?.ReplaceWall ?? false;
}
