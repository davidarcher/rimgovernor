namespace RimGovernor.Brain.Domain;

/// <summary>
/// A one-shot patch of the crop one exact plant grower sows (Building_PlantGrower.SetPlantDefToGrow on the
/// native side; a BuildingPatchIntent); there is no pawn or job involved.
/// </summary>
public sealed record GrowerCrop : IActionPayload
{
    /// <summary>Go <c>NewGrowerCrop</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public GrowerCrop(string thing, string crop)
    {
        DomainException.Require(Identity.IsValidText(thing) && Identity.IsValidText(crop), "invalid grower crop identity");
        Thing = thing;
        Crop = crop;
    }

    public string Thing { get; }

    public string Crop { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewGrowerCropAction(ActionId id, GrowerCrop crop) => Create(id, ActionKind.GrowerCrop, crop);

    public GrowerCrop? GrowerCrop => PayloadOf<GrowerCrop>(ActionKind.GrowerCrop);
}
