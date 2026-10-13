namespace RimGovernor.Brain.Domain;

/// <summary>
/// An immutable one-shot patch of a bed's medical, prisoner or slave use. BuildingPatchIntent revalidates
/// legality at apply; no pawn job is involved.
/// </summary>
public sealed record BedUse : IActionPayload
{
    private BedUse(string thing, bool medical, bool prisoners, bool slaves)
    {
        Thing = thing;
        Medical = medical;
        Prisoners = prisoners;
        Slaves = slaves;
    }

    public string Thing { get; }

    public bool Medical { get; }

    public bool Prisoners { get; }

    public bool Slaves { get; }

    public static BedUse NewBedMedical(string thing, bool medical)
    {
        DomainException.Require(Identity.IsValidText(thing), "invalid bed medical identity");
        return new BedUse(thing, medical, false, false);
    }

    /// <summary>Sets one exact bed for prisoners; there is no patch back to colonists.</summary>
    public static BedUse NewBedPrisoners(string thing)
    {
        DomainException.Require(Identity.IsValidText(thing), "invalid bed medical identity");
        return new BedUse(thing, false, true, false);
    }

    /// <summary>Sets one exact bed for slaves (Ideology); like the prisoners patch, there is no patch back to colonists.</summary>
    public static BedUse NewBedSlaves(string thing)
    {
        DomainException.Require(Identity.IsValidText(thing), "invalid bed medical identity");
        return new BedUse(thing, false, false, true);
    }
}

public sealed partial record DomainAction
{
    public static DomainAction NewBedUseAction(ActionId id, BedUse b) => Create(id, ActionKind.BedUse, b);

    public BedUse? BedUse => PayloadOf<BedUse>(ActionKind.BedUse);
}
