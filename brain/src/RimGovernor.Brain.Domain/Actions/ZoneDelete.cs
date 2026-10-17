namespace RimGovernor.Brain.Domain;

/// <summary>
/// An immutable, comparable value: a one-shot deletion of one exact zone (native DeleteZone); native checks the
/// zone when the intent applies.
/// </summary>
public sealed record ZoneDelete : IActionPayload
{
    public ZoneDelete(string zone)
    {
        DomainException.Require(IdText.IsValidText(zone), "invalid zone delete identity");
        Zone = zone;
    }

    public string Zone { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewZoneDeleteAction(ActionId id, ZoneDelete d) => Create(id, ActionKind.ZoneDelete, d);

    /// <summary>The payload when this is a zone_delete action, otherwise null.</summary>
    public ZoneDelete? ZoneDelete => PayloadOf<ZoneDelete>(ActionKind.ZoneDelete);
}
