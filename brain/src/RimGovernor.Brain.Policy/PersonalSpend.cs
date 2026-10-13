using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    // A hediff cannot tell a restore from an upgrade, so an installed part counts only a fraction of its
    // item's market price. PartBionicTier is the PartTier from which a part is bionic or better: prosthetics,
    // pegs and unrecognised parts (tier <= 1) pay PartRestoreDiscount, bionic and archotech parts (tier > 1)
    // pay PartUpgradeDiscount. An installed part also moves wealth from Items to Pawns, so every share shrinks
    // slightly; accepted.
    public const double PartBionicTier = 1.0;
    public const double PartRestoreDiscount = 0.25;
    public const double PartUpgradeDiscount = 0.75;
}

/// <summary>
/// One thing a colonist carries on themselves: a worn garment or the equipped weapon. Base is its MarketValue
/// at Normal quality and full hit points (the catalog row for its def and stuff); Condition is its hit-point
/// fraction.
/// </summary>
public sealed record PersonalItem
{
    public Resource Definition { get; init; }

    public Resource Stuff { get; init; }

    public int Quality { get; init; }

    public double Condition { get; init; }

    public Fact<double> Base { get; init; }
}

/// <summary>
/// A bed definition's MarketValue at Normal quality and full hit points for the given stuff (the catalog's
/// (def, stuff) row); Ok is false when the catalog has none. ItemFacts.Market is per def only and so cannot
/// stand in for a stuffed bed.
/// </summary>
public delegate (double Price, bool Ok) BedPrice(Resource def, Resource stuff);

/// <summary>
/// One colonist's carried gear. Gear is unknown until the loadout and equipment are read. Free is a free
/// colonist; slaves and prisoners are never charged. Parts are the installed added parts (empty is a pawn
/// with none); PartsUnread marks a failed native read, which leaves the pawn's spent Unknown.
/// </summary>
public sealed record PersonalSpendPawn
{
    public PawnId Id { get; init; }

    public bool Free { get; init; }

    public Fact<EquatableArray<PersonalItem>> Gear { get; init; }

    public IReadOnlyList<InstalledPart> Parts { get; init; } = [];

    public bool PartsUnread { get; init; }
}

/// <summary>
/// Everything the derived spent reads. PartPrice is an installed part item's market value
/// (ItemFacts.MarketValue); null prices nothing, so any installed part leaves its pawn Unknown.
/// </summary>
public sealed record PersonalSpendInput
{
    public SleepingObservation Sleeping { get; init; } = new();

    public BedPrice? BedPrice { get; init; }

    public Func<Resource, (double Price, bool Ok)>? PartPrice { get; init; }

    public IReadOnlyList<PersonalSpendPawn> Pawns { get; init; } = [];

    internal Fact<double> BedValue(SleepingBed bed)
    {
        var ok = bed.Quality.TryGetValue(out var quality);
        var (q, known) = QualityByName(quality ?? "");
        bed.Stuff.TryGetValue(out var stuff);
        if (!ok || !known || BedPrice is null)
        {
            return Unknown<double>();
        }
        var (price, priced) = BedPrice(bed.Definition, new Resource(stuff ?? ""));
        if (!priced)
        {
            return Unknown<double>();
        }
        return ItemMarketValue(price, q, 1);
    }

    /// <summary>An installed part's discounted price; Unknown when the part spawns no item or the item has no price.</summary>
    internal Fact<double> PartValue(InstalledPart p)
    {
        if (!p.Item.TryGetValue(out var item) || PartPrice is null)
        {
            return Unknown<double>();
        }
        var (price, priced) = PartPrice(item);
        if (!priced || !Finite(price) || price < 0)
        {
            return Unknown<double>();
        }
        return Known(price * PartDiscount(p.Tier));
    }
}

/// <summary>Accumulates a spent; one unknown part makes the whole unknown.</summary>
internal sealed class SpendSum
{
    public double Total { get; set; }

    public bool IsUnknown { get; set; }

    public void Add(Fact<double> f, double share)
    {
        if (f.TryGetValue(out var v))
        {
            Total += v * share;
        }
        else
        {
            IsUnknown = true;
        }
    }

    public SpendSum Copy() => new() { Total = Total, IsUnknown = IsUnknown };

    public Fact<double> ToFact() => IsUnknown ? Unknown<double>() : Known(Total);
}

public static partial class PolicyFunctions
{
    // Quality price factors of the MarketValue stat (StatPart_Quality on MarketValueBase in Core
    // Stats_Basics_General.xml, decompiled StatPart_Quality.TransformValue): a thing's value becomes
    // val + min(val x factor - val, maxGain), that is min(val x factor, val + maxGain), indexed Awful=0
    // through Legendary=6. Awful and Poor lose value, Good and above gain up to the cap.
    private static readonly double[] MarketQualityFactor = [.5, .75, 1, 1.25, 1.5, 2.5, 5];
    private static readonly double[] MarketQualityMaxGain = [1e7, 1e7, 1e7, 500, 1000, 2000, 3000];

    // StatPart_Health on MarketValue (same file): the value factor by hit-point fraction, linear between the
    // points and flat outside them. It applies to every def that uses hit points and lets health affect
    // price, which covers apparel, weapons and beds.
    private static readonly (double X, double Y)[] MarketHealthCurve = [(0, 0), (.5, .1), (.6, .5), (.9, 1)];

    /// <summary>
    /// An item's MarketValue from its value at Normal quality and full hit points (GearOption.Cost, the
    /// catalog's (def, stuff) row), its quality (Awful=0 through Legendary=6) and its hit-point fraction.
    /// Unknown for a quality outside the range, a condition outside 0..1 or a base that is not finite and
    /// non-negative.
    /// </summary>
    public static Fact<double> ItemMarketValue(double baseValue, int quality, double condition)
    {
        if (quality < 0 || quality >= MarketQualityFactor.Length || !FiniteUnit(condition) || !Finite(baseValue) || baseValue < 0)
        {
            return Unknown<double>();
        }
        var value = Math.Min(baseValue * MarketQualityFactor[quality], baseValue + MarketQualityMaxGain[quality]);
        return Known(value * MarketHealth(condition));
    }

    private static double MarketHealth(double x)
    {
        var p = MarketHealthCurve;
        if (x >= p[^1].X)
        {
            return p[^1].Y;
        }
        for (var i = 1; i < p.Length; i++)
        {
            if (x <= p[i].X)
            {
                return p[i - 1].Y + ((p[i].Y - p[i - 1].Y) * (x - p[i - 1].X) / (p[i].X - p[i - 1].X));
            }
        }
        return p[^1].Y;
    }

    /// <summary>Maps the native QualityCategory name to its index.</summary>
    internal static (int Index, bool Ok) QualityByName(string name)
    {
        var i = Array.IndexOf(["Awful", "Poor", "Normal", "Good", "Excellent", "Masterwork", "Legendary"], name);
        return (i, i >= 0);
    }

    /// <summary>A loadout option as a carried item, priced from the option's Cost.</summary>
    public static PersonalItem PersonalItemOf(GearOption o) => new()
    {
        Definition = o.Definition,
        Stuff = o.Stuff,
        Quality = o.Quality,
        Condition = o.Condition,
        Base = Known(o.Cost),
    };

    /// <summary>The share of an installed part's price that counts as spent.</summary>
    public static double PartDiscount(double tier) =>
        tier > PolicyConstants.PartBionicTier ? PolicyConstants.PartUpgradeDiscount : PolicyConstants.PartRestoreDiscount;

    /// <summary>
    /// Derives what each free colonist holds now: their share of the owned bed, of the contents of the
    /// bedroom they own, and their worn apparel and equipped weapon, each at its quality and condition price.
    /// Nothing is persisted, so a lost item frees budget. Installed parts add their market price at a tier
    /// discount (PartDiscount). Keyed by free colonists only; a slave or prisoner has no entry, and a
    /// colonist whose bed, room or gear is unread is Unknown (never zero) so a gate on it refuses upgrades
    /// instead of guessing.
    ///
    /// A bedroom is one RoomQualityTargets sees: a room holding a humanlike, non-medical, non-prisoner bed
    /// with owners, and slave beds never charge a free colonist. The room's RoomStatWorker_Wealth sums every
    /// building, plant and floor in it, the beds included, so contents are the room Wealth less its beds,
    /// split evenly among the room's owners, and each bed's value is split among that bed's own owners: a
    /// couple pays half each, a barracks splits the room but each pays only the bed they own. The proxy needs
    /// no per-piece contract. A bed's hit points are not read, so it is priced at full health, which the
    /// health curve leaves at 1 down to 90%.
    /// </summary>
    public static IReadOnlyDictionary<PawnId, Fact<double>> PersonalSpent(PersonalSpendInput input)
    {
        var roomsKnown = input.Sleeping.Rooms.TryGetValue(out var rooms);
        var wealth = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var r in rooms)
        {
            if (r.Quality.TryGetValue(out var q) && Finite(q.Wealth) && q.Wealth >= 0)
            {
                wealth[r.Id] = q.Wealth;
            }
        }
        var beds = new Dictionary<string, SpendSum>(StringComparer.Ordinal);
        var owners = new Dictionary<string, List<PawnId>>(StringComparer.Ordinal);
        var spent = new Dictionary<PawnId, SpendSum>();
        SpendSum Of(PawnId p)
        {
            if (!spent.TryGetValue(p, out var s))
            {
                s = new SpendSum();
                spent[p] = s;
            }
            return s;
        }
        foreach (var bed in input.Sleeping.Beds)
        {
            var ok = bed.Room.TryGetValue(out var room);
            bed.Humanlike.TryGetValue(out var humanlike);
            bed.Medical.TryGetValue(out var medical);
            bed.Prisoners.TryGetValue(out var prisoners);
            if (!ok || string.IsNullOrEmpty(room) || !humanlike || medical || prisoners || bed.Slaves || bed.Owners.Count == 0)
            {
                continue;
            }
            var value = input.BedValue(bed);
            if (!beds.TryGetValue(room, out var roomBeds))
            {
                roomBeds = new SpendSum();
                beds[room] = roomBeds;
            }
            roomBeds.Add(value, 1);
            if (!owners.TryGetValue(room, out var roomOwners))
            {
                roomOwners = [];
                owners[room] = roomOwners;
            }
            foreach (var o in bed.Owners)
            {
                if (!roomOwners.Contains(o))
                {
                    roomOwners.Add(o);
                }
                Of(o).Add(value, 1 / (double)bed.Owners.Count);
            }
        }
        // Go ranged the map in random order; a fixed order keeps a pawn's float sum reproducible.
        foreach (var (room, pawns) in owners.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var contents = Unknown<double>();
            var wok = wealth.TryGetValue(room, out var w);
            if (beds[room].ToFact().TryGetValue(out var b) && roomsKnown && wok)
            {
                contents = Known(Math.Max(w - b, 0));
            }
            foreach (var o in pawns)
            {
                Of(o).Add(contents, 1 / (double)pawns.Count);
            }
        }
        var result = new Dictionary<PawnId, Fact<double>>();
        foreach (var p in input.Pawns)
        {
            if (!p.Free)
            {
                continue;
            }
            var s = spent.TryGetValue(p.Id, out var held) ? held.Copy() : new SpendSum();
            var gearKnown = p.Gear.TryGetValue(out var items);
            if (!gearKnown)
            {
                s.IsUnknown = true;
            }
            foreach (var it in items)
            {
                if (!it.Base.TryGetValue(out var baseValue))
                {
                    s.IsUnknown = true;
                    continue;
                }
                s.Add(ItemMarketValue(baseValue, it.Quality, it.Condition), 1);
            }
            if (p.PartsUnread)
            {
                s.IsUnknown = true;
            }
            foreach (var part in p.Parts)
            {
                s.Add(input.PartValue(part), 1);
            }
            result[p.Id] = s.ToFact();
        }
        return result;
    }
}
