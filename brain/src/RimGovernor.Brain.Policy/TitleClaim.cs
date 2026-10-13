namespace RimGovernor.Brain.Policy;

// The title claim gate. A higher title raises the holder's expectations, so the colony claims the next one only
// once the holder's bedroom and the next title's throne room already meet their requirements, never before. The
// gate is a pure decision over recorded facts; no runtime acts on a TitleClaimNow yet (see TitleClaim).

/// <summary>Names why the gate holds, or None when it claims.</summary>
public enum TitleClaimReason
{
    [WireName("")]
    None,

    /// <summary>A fact the gate needs is unread; it holds rather than guess.</summary>
    [WireName("unknown")]
    Unknown,

    /// <summary>No colonist has the favor for the next rung.</summary>
    [WireName("favor_short")]
    NoFavor,

    /// <summary>The holder's solo bedroom falls short of its title's requirements.</summary>
    [WireName("bedroom_unmet")]
    BedroomUnmet,

    /// <summary>The next title's throne room is not standing with its throne at the required impressiveness.</summary>
    [WireName("throne_unmet")]
    ThroneUnmet,
}

/// <summary>The gate's verdict for one colonist: Claim is set when the colonist may claim Title now, otherwise Reason says what holds it.</summary>
public sealed record TitleClaim
{
    public PawnId Holder { get; init; }

    public string Title { get; init; } = "";

    public bool Claim { get; init; }

    public TitleClaimReason Reason { get; init; }
}

/// <summary>
/// The recorded facts the gate reads: the royalty read, the sleeping census (bedrooms and their pieces), the
/// throne plan with the room census and buildings, and each census room's impressiveness.
/// </summary>
public sealed record TitleClaimFacts
{
    public RoyaltyFacts Royalty { get; init; } = new();

    public SleepingObservation Sleeping { get; init; } = new();

    public IReadOnlyList<FurnitureRoom> BedroomPieces { get; init; } = [];

    public LayoutPlan Plan { get; init; } = new();

    public RoomObservation Rooms { get; init; } = new();

    public IReadOnlyList<CurrentBuilding> Built { get; init; } = [];

    public IReadOnlyDictionary<string, double> Impressiveness { get; init; } = new Dictionary<string, double>();
}

public static partial class PolicyFunctions
{
    /// <summary>The rung a holding would claim next: the first rung above the held title (the first rung for none), false at the top of the ladder.</summary>
    private static (RoyalRung Rung, bool Ok) NextClaim(IReadOnlyList<RoyalRung> ladder, string held)
    {
        var next = 0;
        if (held.Length != 0)
        {
            next = -1;
            for (var i = 0; i < ladder.Count; i++)
            {
                if (ladder[i].Title == held)
                {
                    next = i + 1;
                }
            }
            if (next < 0)
            {
                return (new RoyalRung(), false);
            }
        }
        if (next >= ladder.Count)
        {
            return (new RoyalRung(), false);
        }
        return (ladder[next], true);
    }

    /// <summary>
    /// Whether holder's solo bedroom meets rung's bedroom requirements (its impressiveness and furniture); a rung
    /// that asks for none is met. Unknown when the holder's bedroom furniture is unread.
    /// </summary>
    private static Fact<bool> BedroomMet(TitleClaimFacts f, PawnId holder, RoyalRung rung)
    {
        rung.BedroomMinImpressiveness.TryGetValue(out var minImpressiveness);
        if (minImpressiveness <= 0 && rung.BedroomThings.Count == 0)
        {
            return Known(true);
        }
        var furniture = new Dictionary<string, FurnitureRoom>(StringComparer.Ordinal);
        foreach (var r in f.BedroomPieces)
        {
            furniture[r.Id] = r;
        }
        foreach (var s in SoloBedrooms(f.Sleeping))
        {
            if (s.Owner != holder)
            {
                continue;
            }
            if (s.Impressiveness < minImpressiveness)
            {
                return Known(false);
            }
            var ok = furniture.TryGetValue(s.Room, out var room);
            if (!ok && rung.BedroomThings.Count > 0)
            {
                return Unknown<bool>();
            }
            foreach (var thing in rung.BedroomThings)
            {
                if (IsReplacementBed(thing.AnyOf))
                {
                    continue;
                }
                var have = 0;
                foreach (var p in room!.Pieces)
                {
                    foreach (var d in thing.AnyOf)
                    {
                        if (p.Def == d.Value)
                        {
                            have++;
                        }
                    }
                }
                if (have < thing.Count)
                {
                    return Known(false);
                }
            }
            return Known(true);
        }
        return Known(false);
    }

    /// <summary>
    /// Whether the throne room of need stands in the plan with a throne in it at the title's minimum
    /// impressiveness; a rung that asks for no throne is met. Unknown when the room's impressiveness is unread.
    /// </summary>
    private static Fact<bool> ThroneMet(TitleClaimFacts f, RoyalRung rung)
    {
        var (need, asks) = RungRequirement(rung);
        if (!asks)
        {
            return Known(true);
        }
        var (room, ok) = f.Plan.ThroneRoomFor(need.MinArea);
        if (!ok)
        {
            return Known(false);
        }
        // Census: impressiveness is read by the room's id.
        var (standing, censused) = CensusRoomIn(room, f.Rooms);
        var (_, stands) = StandingThroneIn(room, need, f.Built);
        if (!censused || !stands)
        {
            return Known(false);
        }
        if (need.MinImpressiveness <= 0)
        {
            return Known(true);
        }
        if (!f.Impressiveness.TryGetValue(standing.Id, out var have))
        {
            return Unknown<bool>();
        }
        return Known(have >= need.MinImpressiveness);
    }

    /// <summary>
    /// The gate's verdict for the colonist closest to the next title: the first holder, by ID, whose favor
    /// reaches the next rung's FavorNeeded. With no such colonist the verdict is NoFavor (or Unknown when the
    /// ladder or a favor is unread). A claimable colonist whose bedroom or the rung's throne room falls short is
    /// held, so a title is never claimed ahead of the upkeep it brings.
    /// </summary>
    public static TitleClaim NextTitleClaim(TitleClaimFacts f)
    {
        var ladder = f.Royalty.Ladder;
        if (ladder.Count == 0)
        {
            return new TitleClaim { Reason = TitleClaimReason.Unknown };
        }
        var holders = SortedHolders(f.Royalty.Holders);
        var verdict = new TitleClaim { Reason = TitleClaimReason.NoFavor };
        foreach (var id in holders)
        {
            foreach (var h in f.Royalty.Holders[id])
            {
                var (rung, ok) = NextClaim(ladder, h.Title);
                if (!ok)
                {
                    continue;
                }
                var fk = h.Favor.TryGetValue(out var favor);
                var nk = rung.FavorNeeded.TryGetValue(out var needed);
                if (!fk || !nk)
                {
                    if (verdict.Reason == TitleClaimReason.NoFavor)
                    {
                        verdict = new TitleClaim { Holder = id, Title = rung.Title, Reason = TitleClaimReason.Unknown };
                    }
                    continue;
                }
                if (favor < needed)
                {
                    continue;
                }
                var claim = new TitleClaim { Holder = id, Title = rung.Title };
                var bk = BedroomMet(f, id, rung).TryGetValue(out var bedroom);
                var tk = ThroneMet(f, rung).TryGetValue(out var throne);
                if (bk && !bedroom)
                {
                    claim = claim with { Reason = TitleClaimReason.BedroomUnmet };
                }
                else if (tk && !throne)
                {
                    claim = claim with { Reason = TitleClaimReason.ThroneUnmet };
                }
                else if (!bk || !tk)
                {
                    claim = claim with { Reason = TitleClaimReason.Unknown };
                }
                else
                {
                    return claim with { Claim = true };
                }
                if (verdict.Reason is TitleClaimReason.NoFavor or TitleClaimReason.Unknown)
                {
                    verdict = claim;
                }
            }
        }
        return verdict;
    }

    private static List<PawnId> SortedHolders(IReadOnlyDictionary<PawnId, EquatableArray<RoyalHolding>> m) => [.. m.Keys.OrderBy(id => id)];
}
