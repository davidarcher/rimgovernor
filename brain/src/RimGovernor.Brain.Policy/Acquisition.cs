namespace RimGovernor.Brain.Policy;

// Go: acquisition.go

/// <summary>
/// An observed native-approved source, not inventory. Definition is the source's own native definition name
/// (the plant or the animal, not the harvested resource); a hunt row of a pest race (Pest, the race row's) is a
/// pest hunt: food false, no nutrition.
/// </summary>
public sealed record AcquisitionSource
{
    public string ID { get; init; } = "";

    public string Resource { get; init; } = "";

    public string Token { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public bool Tree { get; init; }

    public bool Food { get; init; }

    public bool Designated { get; init; }

    public bool Hunt { get; init; }

    public double Yield { get; init; }

    public double NutritionYield { get; init; }

    public double RevengeChance { get; init; }

    public double WeaponRange { get; init; }

    public int HerdSize { get; init; }

    public bool MeleeOnly { get; init; }

    public bool Downed { get; init; }

    /// <summary>Sleeping and Predator describe a hunt row for squad planning.</summary>
    public bool Sleeping { get; init; }

    public bool Predator { get; init; }

    /// <summary>What butchering the animal yields besides meat (leather), from its race row.</summary>
    public EquatableArray<SourceProduct> Products { get; init; }

    /// <summary>The source's race row flag (AnimalRace.Pest).</summary>
    public bool Pest { get; init; }

    /// <summary>The tick native first saw the designation (reset on load); set only when Designated.</summary>
    public Tick DesignatedTick { get; init; }

    /// <summary>A pawn's reservation or a colonist's current job targets the source.</summary>
    public bool Taken { get; init; }

    /// <summary>
    /// A plant row's growth fraction (0..1). Plantation marks a sown tree of a harvest-destroys crop in a
    /// growing zone: the only zone plant chop acquisition may offer, and only at or above the chop gate
    /// (BelowChopGate). Wild trees ignore growth.
    /// </summary>
    public double Growth { get; init; }

    public bool Plantation { get; init; }

    /// <summary>
    /// Whether the row is a plantation tree still under the minimum growth fraction the chop selection requires
    /// (RoundsPolicy.ChopMinGrowth). Wild trees and every other row are never below it.
    /// </summary>
    public bool BelowChopGate(double minGrowth) => Plantation && Tree && Growth < minGrowth;

    /// <summary>
    /// A hunt a lone hunter must not designate: a predator, or a standing animal whose revenge chance exceeds
    /// MaxHuntRevengeChance. Selection never designates it; a squad may (SquadPrey).
    /// </summary>
    public bool Retaliates() => Hunt && (Predator || (!Downed && RevengeChance > MaxHuntRevengeChance));

    /// <summary>An open hunt row a squad may target, retaliating or not.</summary>
    public bool SquadPrey() => Hunt && !Designated && !Taken;

    /// <summary>Expected retaliation exposure, before the channel risk cap.</summary>
    public double HuntRevengeCost() => Downed ? 0 : RevengeChance * Math.Max(1, HerdSize);
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The highest manhunter-on-harm chance a standing animal may carry and still be hunted. Above it (moose,
    /// boar, elk...) one wounding shot risks a revenge charge the colony's one or two hunters cannot absorb.
    /// Downed prey cannot retaliate.
    /// </summary>
    public const double MaxHuntRevengeChance = 0.2;

    /// <summary>
    /// Prefers forage, then downed prey and lower herd revenge cost. Equal-cost hunts go meatiest first;
    /// otherwise native ordering holds. Pending yield and unresolved sources prevent duplicate work but never
    /// count as recovered stock.
    /// </summary>
    public static IReadOnlyList<AcquisitionSource> SelectAcquisition(
        Fact<EquatableArray<AcquisitionSource>> sources,
        Fact<double> deficit,
        Fact<double> pending,
        bool food,
        IReadOnlyDictionary<string, bool> held,
        params Fact<int>[] huntSlots
    )
    {
        (double, bool) Accept(AcquisitionSource row) =>
            food ? (row.NutritionYield, row.Food && row.NutritionYield > 0) : (row.Yield, row.Tree && row.Resource == "WoodLog");
        return SelectAcquisitionCore(sources, deficit, pending, Accept, held, huntSlots);
    }

    /// <summary>
    /// SelectAcquisition for one harvested definition (herbal medicine from wild healroot): every non-hunt
    /// source yielding exactly that resource counts by its unit yield.
    /// </summary>
    public static IReadOnlyList<AcquisitionSource> SelectResourceAcquisition(
        Fact<EquatableArray<AcquisitionSource>> sources,
        Fact<double> deficit,
        Fact<double> pending,
        Resource resource,
        IReadOnlyDictionary<string, bool> held
    )
    {
        DomainException.Require(ValidResource(resource), "invalid acquisition resource");
        (double, bool) Accept(AcquisitionSource row) => (row.Yield, !row.Hunt && new Resource(row.Resource) == resource);
        return SelectAcquisitionCore(sources, deficit, pending, Accept, held);
    }

    internal static List<AcquisitionSource> SelectAcquisitionCore(
        Fact<EquatableArray<AcquisitionSource>> sources,
        Fact<double> deficit,
        Fact<double> pending,
        Func<AcquisitionSource, (double Amount, bool Ok)> accept,
        IReadOnlyDictionary<string, bool> held,
        params Fact<int>[] huntSlots
    )
    {
        var known = sources.TryGetValue(out var rowsFact);
        var nk = deficit.TryGetValue(out var need);
        var pk = pending.TryGetValue(out var outstanding);
        DomainException.Require(known && nk && pk && FoodNumber(need) && FoodNumber(outstanding), "acquisition facts unavailable");
        var slots = 0;
        DomainException.Require(huntSlots.Length <= 1, "multiple hunting budgets");
        if (huntSlots.Length == 1 && huntSlots[0].TryGetValue(out var n))
        {
            DomainException.Require(n >= 0 && n <= MaxHuntRows, "invalid hunting budget");
            slots = n;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rowsFact)
        {
            DomainException.Require(
                !(
                    !FoodNumber(row.RevengeChance)
                    || row.RevengeChance > 1
                    || row.HerdSize < 0
                    || row.HerdSize > 65536
                    || !FoodNumber(row.WeaponRange)
                    || !FoodId(row.ID)
                    || !FoodId(row.Resource)
                    || !FoodId(row.Token)
                    || seen.Contains(row.ID)
                    || row.Cell.X < 0
                    || row.Cell.Z < 0
                    || !FoodNumber(row.Yield)
                    || row.Yield <= 0
                    || !FoodNumber(row.NutritionYield)
                    || (!row.Food && row.NutritionYield != 0)
                    || (row.Hunt && (row.Tree || row.Yield != 1 || (!row.Food && !row.Pest)))
                ),
                "invalid acquisition source"
            );
            seen.Add(row.ID);
        }

        var rows = PolicySort.ByLess(
            rowsFact,
            (a, b) =>
            {
                if (a.Hunt != b.Hunt)
                {
                    return !a.Hunt;
                }

                if (!a.Hunt)
                {
                    return false;
                }

                if (a.Downed != b.Downed)
                {
                    return a.Downed;
                }

                if (a.HuntRevengeCost() != b.HuntRevengeCost())
                {
                    return a.HuntRevengeCost() < b.HuntRevengeCost();
                }

                // Hunt slots are few: of equally safe prey the meatier goes first, so a squirrel does not take
                // the slot a deer could.
                return a.NutritionYield > b.NutritionYield;
            }
        );
        var remaining = Math.Max(0, need - outstanding);
        var selected = new List<AcquisitionSource>();
        var others = 0;
        foreach (var row in rows)
        {
            if (remaining <= 0)
            {
                break;
            }

            if (row.Designated || (held.TryGetValue(row.ID, out var isHeld) && isHeld))
            {
                continue;
            }

            if (row.Hunt && (slots == 0 || row.Retaliates()))
            {
                continue;
            }

            // The row cap bounds the non-hunt rows; hunts are bounded by the hunters' budget, so their count
            // follows the nutrition gap.
            if (!row.Hunt && others == MaxCatalogSelection)
            {
                continue;
            }

            var (amount, ok) = accept(row);
            if (!ok)
            {
                continue;
            }

            if (row.Hunt)
            {
                slots--;
            }
            else
            {
                others++;
            }

            selected.Add(row);
            remaining -= amount;
        }

        return selected;
    }
}
