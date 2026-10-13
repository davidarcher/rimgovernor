namespace RimGovernor.Brain.Policy;

/// <summary>
/// The personal-share gate on in-place bedroom upgrades. RoomTarget.Min is the tier ceiling a room climbs to;
/// the gate decides whether an owner's remaining share pays for the next step. The default (no Shares) is
/// ungated, so callers that carry no share keep working. A step is charged its market-value delta. A delta of
/// zero or less (a necessity), and any step in a room with no owners (a common or throne room, which stay on
/// the baseline), is never charged. A charged step needs the Reserves stage (a Foothold colony spends nothing
/// on comfort) and the owners' combined remaining share: a shared room spends its owners' remaining shares
/// together, and an owner with an unknown remaining refuses it.
/// </summary>
public sealed record RoomGate
{
    public ColonyStage Stage { get; init; }

    /// <summary>The colonist's share, null for an ungated gate (observation.ColonyProjection.PersonalShareOf).</summary>
    public Func<PawnId, PersonalShare>? Shares { get; init; }

    /// <summary>Items prices a floor's materials.</summary>
    public ItemFacts Items { get; init; } = new();

    /// <summary>Prices a stuffed bed.</summary>
    public BedPrice? BedPrice { get; init; }

    /// <summary>Prices a template piece in the stuff it would be built in (ItemFacts carries no row for a stuff-made def).</summary>
    public Func<string, (double Price, bool Ok)>? PiecePrice { get; init; }

    /// <summary>
    /// The bed a new suite is furnished with and SuitePieces the template pieces it takes; a gated gate with
    /// no SuiteBed prices no suite and refuses its claims.
    /// </summary>
    public Resource SuiteBed { get; init; }

    public Resource SuiteBedStuff { get; init; }

    public EquatableArray<string> SuitePieces { get; init; }

    /// <summary>
    /// What furnishing a suite costs its claimant: the new bed's delta over the bed they own plus the
    /// template pieces (walls, doors and floor are not charged), false when any part is unpriced.
    /// </summary>
    internal (double Price, bool Ok) FurnishPrice(SleepingBed owned)
    {
        var (total, ok) = BedDelta(SuiteBed, SuiteBedStuff, owned);
        if (!ok || SuiteBed.Value.Length == 0)
        {
            return (0, false);
        }
        foreach (var def in SuitePieces)
        {
            if (PiecePrice is null)
            {
                return (0, false);
            }
            var (price, pok) = PiecePrice(def);
            if (!pok)
            {
                return (0, false);
            }
            total += price;
        }
        return (total, true);
    }

    /// <summary>Whether the owners may spend delta now.</summary>
    public bool Allows(IReadOnlyList<PawnId> owners, double delta)
    {
        if (Shares is null || owners.Count == 0 || delta <= 0)
        {
            return true;
        }
        if (Stage < ColonyStage.Reserves)
        {
            return false;
        }
        var total = 0.0;
        foreach (var o in owners)
        {
            var s = Shares(o);
            if (!s.Gated)
            {
                return true;
            }
            if (!s.Remaining.TryGetValue(out var remaining))
            {
                return false;
            }
            total += remaining;
        }
        return delta <= total;
    }

    /// <summary>Whether the owners may place one def: its market value is the delta. A def the catalog cannot price is refused while gated.</summary>
    public bool PieceAllowed(IReadOnlyList<PawnId> owners, string def)
    {
        if (Shares is null || owners.Count == 0)
        {
            return true;
        }
        if (PiecePrice is null)
        {
            return false;
        }
        var (price, ok) = PiecePrice(def);
        return ok && Allows(owners, price);
    }

    /// <summary>The material price of one cell of floor d, false when the costs or a material's market value are unread.</summary>
    public (double Price, bool Ok) FloorCellPrice(FloorDefinition d)
    {
        if (!d.Costs.TryGetValue(out var costs))
        {
            return (0, false);
        }
        var total = 0.0;
        foreach (var c in costs)
        {
            if (!Items.TryMarketValue(c.Resource, out var price))
            {
                return (0, false);
            }
            total += price * c.Count;
        }
        return (total, true);
    }

    /// <summary>Whether the owners may rebuild owned as def in stuff: the price delta must be known and fit.</summary>
    internal bool BedAllowed(IReadOnlyList<PawnId> owners, Resource def, Resource stuff, SleepingBed owned)
    {
        if (Shares is null)
        {
            return true;
        }
        var (delta, ok) = BedDelta(def, stuff, owned);
        return ok && Allows(owners, delta);
    }

    /// <summary>
    /// The market-value delta of a new bed of def in stuff (Normal quality; empty stuff reads as the owned
    /// bed's) over the owned bed, false when either is unpriced.
    /// </summary>
    internal (double Delta, bool Ok) BedDelta(Resource def, Resource stuff, SleepingBed owned)
    {
        if (BedPrice is null)
        {
            return (0, false);
        }
        owned.Stuff.TryGetValue(out var ownedStuff);
        if (stuff.Value.Length == 0)
        {
            stuff = new Resource(ownedStuff ?? "");
        }
        owned.Quality.TryGetValue(out var quality);
        var (q, known) = PolicyFunctions.QualityByName(quality ?? "");
        var (baseValue, ok) = BedPrice(def, stuff);
        var (ownedBase, ownedOk) = BedPrice(owned.Definition, new Resource(ownedStuff ?? ""));
        if (!known || !ok || !ownedOk)
        {
            return (0, false);
        }
        var nk = PolicyFunctions.ItemMarketValue(baseValue, 2, 1).TryGetValue(out var next);
        var hk = PolicyFunctions.ItemMarketValue(ownedBase, q, 1).TryGetValue(out var have);
        return (next - have, nk && hk);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Drops the claims whose owner's remaining share does not pay the furnishing left to do, keeping the
    /// order. The claims take the vacant suites in order, so a claim is priced against the suite it would
    /// take: one with a bed standing has only the move left (free), any other, including a suite still to be
    /// built, is charged its furnishing. A gated claim whose price is unknown is dropped, so MaintainHousing
    /// can recover.
    /// </summary>
    internal static IReadOnlyList<SuiteClaim> GateSuiteClaims(
        IReadOnlyList<SuiteClaim> claims, LayoutPlan plan, RoomObservation rooms, SleepingObservation sleeping, RoomGate gate)
    {
        if (gate.Shares is null)
        {
            return claims;
        }
        var beds = new Dictionary<string, SleepingBed>(StringComparer.Ordinal);
        foreach (var b in sleeping.Beds)
        {
            beds[b.Id] = b;
        }
        var vacant = VacantSuites(plan, rooms, sleeping);
        var output = new List<SuiteClaim>();
        foreach (var c in claims)
        {
            var i = output.Count;
            if (i < vacant.Count)
            {
                // Census: the suite's beds are read off its room.
                var (room, ok) = CensusRoomIn(vacant[i], rooms);
                if (ok && room.Beds.Count > 0)
                {
                    output.Add(c);
                    continue;
                }
            }
            var known = beds.TryGetValue(c.Bed, out var owned);
            var (price, priced) = gate.FurnishPrice(owned ?? new SleepingBed());
            if (known && priced && gate.Allows([c.Pawn], price))
            {
                output.Add(c);
            }
        }
        return output;
    }
}
