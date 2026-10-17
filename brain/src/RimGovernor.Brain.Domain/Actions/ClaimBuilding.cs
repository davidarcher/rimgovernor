namespace RimGovernor.Brain.Domain;

/// <summary>
/// A one-shot claim of one exact claimable building for the player (Building.ClaimableBy(player) then
/// SetFaction(player) on the native side; a BuildingPatchIntent); no pawn or Job is involved.
/// </summary>
public sealed record ClaimBuilding : IActionPayload
{
    /// <summary>Throws <see cref="DomainException"/> when invalid.</summary>
    public ClaimBuilding(string thing)
    {
        DomainException.Require(IdText.IsValidText(thing), "invalid claim building identity");
        Thing = thing;
    }

    public string Thing { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewClaimBuildingAction(ActionId id, ClaimBuilding claim) => Create(id, ActionKind.ClaimBuilding, claim);

    /// <summary>The payload when this is a claim building action, otherwise null.</summary>
    public ClaimBuilding? ClaimBuilding => PayloadOf<ClaimBuilding>(ActionKind.ClaimBuilding);
}
