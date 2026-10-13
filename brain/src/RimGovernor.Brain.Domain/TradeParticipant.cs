namespace RimGovernor.Brain.Domain;

public enum TradeParticipantKind
{
    [WireName("map_trader")]
    Map,
    Settlement,
    [WireName("orbital_ship")]
    Orbital,
}

/// <summary>
/// The closed identity of a seller, distinct from an economic <see cref="TradeTarget"/> (a desired stock
/// level). Settlement trades also carry the player caravan whose inventory and negotiator own the session.
/// </summary>
public sealed record TradeParticipant(TradeParticipantKind Kind, string Id, string Caravan = "")
{
    /// <summary>Go <c>Validate</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public void Validate()
    {
        var problem = Problem();
        if (problem is not null)
        {
            throw new DomainException(problem);
        }
    }

    /// <summary>Go's <c>Validate() == nil</c>.</summary>
    public bool IsValid => Problem() is null;

    private string? Problem()
    {
        if (!Identity.IsValidText(Id))
        {
            return "invalid trade participant ID";
        }
        switch (Kind)
        {
            case TradeParticipantKind.Map or TradeParticipantKind.Orbital:
                return Caravan.Length != 0 ? "caravan on non-settlement trade participant" : null;
            case TradeParticipantKind.Settlement:
                return !Identity.IsValidText(Caravan) || Caravan == Id ? "invalid settlement trade caravan" : null;
            default:
                return "invalid trade participant kind";
        }
    }

    /// <summary>The identity of a priced seller and the inventory owning the deal.</summary>
    public string Key() =>
        Kind switch
        {
            TradeParticipantKind.Map => Id,
            TradeParticipantKind.Orbital => "orbital/" + Id,
            TradeParticipantKind.Settlement => "settlement/" + Id + "/" + Caravan,
            _ => "",
        };
}
