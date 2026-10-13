namespace RimGovernor.Brain.Domain;

/// <summary>
/// Switches one exact refuelable building's auto-refuel (CompRefuelable.allowAutoRefuel): a BuildingPatchIntent
/// arm; no pawn or job is involved. The temperature family lets a heat campfire burn out once its sleeping room
/// is warm and refuels it when the room is cold. Holds the building and the setting it should hold.
/// </summary>
public sealed record AutoRefuel : IActionPayload
{
    /// <summary>Go <c>NewAutoRefuel</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public AutoRefuel(string thing, bool allow)
    {
        DomainException.Require(Identity.IsValidText(thing), "invalid auto refuel identity");
        Thing = thing;
        Allow = allow;
    }

    public string Thing { get; }

    public bool Allow { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewAutoRefuelAction(ActionId id, AutoRefuel refuel) => Create(id, ActionKind.AutoRefuel, refuel);

    public AutoRefuel? AutoRefuel => PayloadOf<AutoRefuel>(ActionKind.AutoRefuel);
}
