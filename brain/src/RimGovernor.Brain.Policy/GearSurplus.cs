namespace RimGovernor.Brain.Policy;

/// <summary>
/// The one definition of the gear the colony holds beyond its demand, the gear planners' own figures subtracted
/// from what lies in storage: the garments no loadout, replacement need or tattered-garment runway claims and the
/// loose weapons no colonist is or will be assigned. It is what a sale may take while the colony is short of
/// silver; the best of a kind stays for the loadouts, the cheapest surplus is sold first (SaleGearSurplus).
/// </summary>
public sealed record GearSurplus
{
    /// <summary>The serviceable stored garment kinds above demand, each row a (definition, stuff, quality, hit-point band) with its surplus count.</summary>
    public IReadOnlyList<GearStock> Apparel { get; init; } = [];

    /// <summary>The loose weapon counts above the armory's demand, per definition (SpareWeaponCounts).</summary>
    public IReadOnlyDictionary<Resource, int> Weapons { get; init; } = new Dictionary<Resource, int>();

    public bool Empty() => Apparel.Count == 0 && Weapons.Count == 0;
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The serviceable stored apparel above demand. Demand is the gear planners' own: the stored garments the
    /// loadout model assigns to a gap, the replacements the census still asks for (best quality first) and the
    /// spare the clothing runway keeps for the worn garments about to wear out. Known is false while the census,
    /// the storage rows or any replacement is unread, or the loadout model refuses a pawn: nothing is then surplus.
    /// </summary>
    public static (IReadOnlyList<GearStock> Rows, bool Known) ApparelSurplus(ClothingDemandInput input)
    {
        if (!input.Gear.TryGetValue(out var gear) || !gear.Stored.TryGetValue(out var stored))
        {
            return ([], false);
        }
        GearReview review;
        try
        {
            review = ReviewGear(input.Gear);
        }
        catch (DomainException)
        {
            return ([], false);
        }
        var left = UnassignedGearStock(stored, review.Loadouts).Where(s => s.Serviceable() && s.Count > 0).ToList();
        // The best of a kind is claimed first, so the cheapest is what remains.
        left = left
            .OrderBy(s => s.Definition.Value, StringComparer.Ordinal)
            .ThenBy(s => s.Stuff.Value, StringComparer.Ordinal)
            .ThenByDescending(s => s.Quality)
            .ThenByDescending(s => s.HPBand)
            .ToList();
        bool Claim(Resource definition, Resource stuff)
        {
            for (var i = 0; i < left.Count; i++)
            {
                var row = left[i];
                if (row.Count > 0 && row.Definition == definition && (string.IsNullOrEmpty(stuff.Value) || row.Stuff == stuff))
                {
                    left[i] = row with { Count = row.Count - 1 };
                    return true;
                }
            }
            return false;
        }
        foreach (var pawn in gear.Pawns)
        {
            if (!pawn.Replacements.TryGetValue(out var replacements))
            {
                return ([], false);
            }
            foreach (var n in replacements)
            {
                Claim(n.Definition, n.Stuff);
            }
        }
        var reserve = ClothingSpareReserve(input);
        foreach (var definition in reserve.Keys.OrderBy(d => d.Value, StringComparer.Ordinal))
        {
            for (var i = 0; i < reserve[definition]; i++)
            {
                Claim(definition, new Resource(""));
            }
        }
        return (left.Where(r => r.Count > 0).ToList(), true);
    }

    /// <summary>
    /// The loose weapons above the armory's demand, per definition: those the colony holds less the weapons
    /// AssignEquip hands a colonist and the ones an armed fighter's upgrade will take. A makeshift or biocoded
    /// thing is never counted.
    /// </summary>
    public static IReadOnlyDictionary<Resource, int> SpareWeaponCounts(
        ArmoryTier tier,
        IReadOnlyList<EquipCandidatePawn> pawns,
        IReadOnlyDictionary<Domain.PawnId, ArmoryPrimary> primaries,
        IReadOnlyList<EquipCandidateWeapon> weapons,
        IReadOnlyList<GearRecipe> recipes,
        IReadOnlyDictionary<Resource, WeaponDef> products
    )
    {
        var spare = new Dictionary<Resource, int>(ArmoryWeaponAccounting(tier, pawns, primaries, weapons, recipes, products).Spare);
        var @out = new Dictionary<Resource, int>();
        foreach (var w in weapons)
        {
            if (w.Class == WeaponClass.Makeshift || w.Biocoded)
            {
                spare.Remove(new Resource(w.Definition));
            }
        }
        foreach (var (definition, n) in spare)
        {
            if (n > 0)
            {
                @out[definition] = n;
            }
        }
        return @out;
    }

    /// <summary>
    /// The surplus gear sale: the sheet rows to sell, by thing id, cheapest first, until their prices cover the
    /// silver gap. Apparel rows must lie in a warehouse zone and match a surplus kind; a weapon row matches a
    /// surplus definition. The caller offers it only while the gap is open.
    /// </summary>
    public static IReadOnlySet<string> SaleGearSurplus(IReadOnlyList<TradeSheetRowFact> rows, IReadOnlySet<string> warehouses, GearSurplus surplus, double gap)
    {
        var @out = new HashSet<string>(StringComparer.Ordinal);
        if (surplus.Empty() || !(gap > 0))
        {
            return @out;
        }
        var apparel = new Dictionary<GearStock, int>();
        foreach (var s in surplus.Apparel)
        {
            var key = s with { Count = 0 };
            apparel[key] = apparel.GetValueOrDefault(key) + s.Count;
        }
        var weapons = new Dictionary<Resource, int>(surplus.Weapons);
        var candidates = rows
            .Where(r => r.ThingID.Length != 0 && !r.Pawn && r.SellPriceKnown && Finite(r.SellPrice) && r.SellPrice > 0)
            .OrderBy(r => r.SellPrice)
            .ThenBy(r => r.ThingID, StringComparer.Ordinal)
            .ToList();
        var covered = 0.0;
        foreach (var row in candidates)
        {
            if (covered >= gap)
            {
                break;
            }
            var def = new Resource(row.DefName);
            if (weapons.GetValueOrDefault(def) > 0)
            {
                weapons[def]--;
            }
            else if (ApparelKey(row, warehouses) is { } key && apparel.GetValueOrDefault(key) > 0)
            {
                apparel[key]--;
            }
            else
            {
                continue;
            }
            @out.Add(row.ThingID);
            covered += row.SellPrice;
        }
        return @out;
    }

    /// <summary>The surplus kind of a warehouse gear row.</summary>
    private static GearStock? ApparelKey(TradeSheetRowFact row, IReadOnlySet<string> warehouses)
    {
        if (!warehouses.Contains(row.ZoneID) || !row.HitPointsKnown || !row.QualityKnown)
        {
            return null;
        }
        return new GearStock
        {
            Definition = new Resource(row.DefName),
            Stuff = new Resource(row.Stuff),
            Quality = (int)row.Quality,
            HPBand = Math.Min(9, (int)Math.Floor(row.HitPoints * 10)),
        };
    }
}
