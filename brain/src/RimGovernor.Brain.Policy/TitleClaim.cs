namespace RimGovernor.Brain.Policy;

// The title claim gate. A higher title raises the holder's expectations, so the colony claims the next one only
// once the holder's bedroom and the next title's throne room already meet their requirements, never before. The
// gate is a pure decision over recorded facts; no runtime acts on a TitleClaimNow yet (see TitleClaim).

/// <summary>Names why the gate holds, or None when it claims.</summary>
public enum TitleClaimReason
{
    [WireName("")]
    None,

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

    public required RoomObservation Rooms { get; init; }

    public IReadOnlyList<CurrentBuilding> Built { get; init; } = [];
}

public static partial class TitleClaimRules
{
    /// <summary>The rung a holding would claim next: the first rung above the held title (the first rung for none), null at the top of the ladder or for a title off it.</summary>
    internal static RoyalRung? NextClaim(EquatableArray<RoyalRung> ladder, string? held)
    {
        var next = 0;
        if (held is not null)
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
                return null;
            }
        }
        return next < ladder.Count ? ladder[next] : null;
    }

    /// <summary>
    /// Whether holder's solo bedroom meets rung's bedroom requirements (its impressiveness and furniture); a rung
    /// that asks for none is met. Missing furniture census rows raise RequiredFactException.
    /// </summary>
    private static bool BedroomMet(TitleClaimFacts f, PawnId holder, RoyalRung rung)
    {
        var minImpressiveness = rung.BedroomMinImpressiveness;
        if (minImpressiveness <= 0 && rung.BedroomThings.Count == 0)
        {
            return true;
        }
        var furniture = new Dictionary<string, FurnitureRoom>(StringComparer.Ordinal);
        foreach (var r in f.BedroomPieces)
        {
            furniture[r.Id] = r;
        }
        foreach (var s in RoomQualityRules.SoloBedrooms(f.Sleeping))
        {
            if (s.Owner != holder)
            {
                continue;
            }
            if (s.Impressiveness < minImpressiveness)
            {
                return false;
            }
            var ok = furniture.TryGetValue(s.Room, out var room);
            if (!ok && rung.BedroomThings.Count > 0)
            {
                throw new RequiredFactException("royal bedroom furniture");
            }
            foreach (var thing in rung.BedroomThings)
            {
                if (thing.AnyOf.Any(d => f.Rooms.Shapes.Furniture.IsBed(d.Value)))
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
                    return false;
                }
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the throne room of need stands in the plan with a throne in it at the title's minimum
    /// impressiveness; a rung that asks for no throne is met. Missing room impressiveness raises RequiredFactException.
    /// </summary>
    private static bool ThroneMet(TitleClaimFacts f, RoyalRung rung)
    {
        var (need, asks) = ThroneRules.RungRequirement(rung);
        if (!asks)
        {
            return true;
        }
        var (room, ok) = f.Plan.ThroneRoomFor(need.MinArea);
        if (!ok)
        {
            return false;
        }
        // Census: impressiveness is read by the room's id.
        var (standing, censused) = PlannedRoom.CensusRoomIn(room, f.Rooms);
        var (_, stands) = ThroneRules.StandingThroneIn(room, need, f.Built);
        if (!censused || !stands)
        {
            return false;
        }
        if (need.MinImpressiveness <= 0)
        {
            return true;
        }
        var have = standing!.Stats.FirstOrDefault(stat => stat.DefName == nameof(RoomQuality.Impressiveness))?.Value
            ?? throw new RequiredFactException("throne room impressiveness");
        return have >= need.MinImpressiveness;
    }

    /// <summary>
    /// The gate's verdict for the colonist closest to the next title: the first holder, by ID, whose favor
    /// reaches the next rung's FavorNeeded. With no such colonist the verdict is NoFavor (including an empty ladder). A claimable colonist whose bedroom or the rung's throne room falls short is
    /// held, so a title is never claimed ahead of the upkeep it brings.
    /// </summary>
    public static TitleClaim NextTitleClaim(TitleClaimFacts f)
    {
        var ladder = f.Royalty.Ladder;
        if (ladder.Count == 0)
        {
            return new TitleClaim { Reason = TitleClaimReason.NoFavor };
        }
        var holders = SortedHolders(f.Royalty.Holders);
        var verdict = new TitleClaim { Reason = TitleClaimReason.NoFavor };
        foreach (var id in holders)
        {
            foreach (var h in f.Royalty.Holders[id])
            {
                if (NextClaim(ladder, h.Title) is not { } rung || h.Favor < rung.FavorNeeded)
                {
                    continue;
                }
                var claim = new TitleClaim { Holder = id, Title = rung.Title };
                var bedroom = BedroomMet(f, id, rung);
                var throne = ThroneMet(f, rung);
                if (!bedroom)
                {
                    claim = claim with { Reason = TitleClaimReason.BedroomUnmet };
                }
                else if (!throne)
                {
                    claim = claim with { Reason = TitleClaimReason.ThroneUnmet };
                }
                else
                {
                    return claim with { Claim = true };
                }
                if (verdict.Reason is TitleClaimReason.NoFavor)
                {
                    verdict = claim;
                }
            }
        }
        return verdict;
    }

    internal static List<PawnId> SortedHolders(IReadOnlyDictionary<PawnId, EquatableArray<PawnRoyalHolding>> m) => [.. m.Keys.OrderBy(id => id)];
}
