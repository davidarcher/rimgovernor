namespace RimGovernor.Brain.Domain;

/// <summary>
/// Orders one exact flickable building's switch to <see cref="On"/>: a BuildingPatchIntent arm that sets the switch's
/// wanted state exactly as the power gizmo does (CompFlickable's toggle), so a pawn then flicks it natively. The power
/// family switches idle consumers off to serve a spoiling one and back on once the network can serve them.
/// </summary>
public sealed record PowerSwitch : IActionPayload
{
    /// <summary>Throws <see cref="DomainException"/> when invalid.</summary>
    public PowerSwitch(string thing, bool on)
    {
        DomainException.Require(IdText.IsValidText(thing), "invalid power switch identity");
        Thing = thing;
        On = on;
    }

    public string Thing { get; }

    public bool On { get; }
}

/// <summary>
/// Unforbids one exact building (the CompForbiddable gizmo): a BuildingPatchIntent arm. The power family allows a
/// forbidden producer before its restoration, because pawns never refuel or repair a forbidden building.
/// </summary>
public sealed record BuildingAllow : IActionPayload
{
    /// <summary>Throws <see cref="DomainException"/> when invalid.</summary>
    public BuildingAllow(string thing)
    {
        DomainException.Require(IdText.IsValidText(thing), "invalid building allow identity");
        Thing = thing;
    }

    public string Thing { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewPowerSwitchAction(ActionId id, PowerSwitch value) => Create(id, ActionKind.PowerSwitch, value);

    public PowerSwitch? PowerSwitch => PayloadOf<PowerSwitch>(ActionKind.PowerSwitch);

    public static DomainAction NewBuildingAllowAction(ActionId id, BuildingAllow value) => Create(id, ActionKind.BuildingAllow, value);

    public BuildingAllow? BuildingAllow => PayloadOf<BuildingAllow>(ActionKind.BuildingAllow);
}
