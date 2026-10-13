namespace RimGovernor.Brain.Policy;

// Go: trade_select.go.

/// <summary>
/// One live trade sheet row as SelectTrade reads it. Each optional native field carries its own Known flag:
/// Python's select_trade tests <c>is not True</c> / <c>is not False</c> precisely so an absent field is never
/// silently read as a usable value, and this mirrors that. It deliberately mirrors the bridge's trade sheet row
/// without this package depending on the bridge package.
/// </summary>
public sealed record TradeSheetRowFact
{
    public Fact<TradeFoodGood> Food { get; init; }

    public string LineID { get; init; } = "";

    public string DefName { get; init; } = "";

    public long ColonyCount { get; init; }

    public long TraderCount { get; init; }

    public double BuyPrice { get; init; }

    public bool BuyPriceKnown { get; init; }

    public double SellPrice { get; init; }

    public bool SellPriceKnown { get; init; }

    public bool TraderWillTrade { get; init; }

    public bool TraderWillTradeKnown { get; init; }

    public bool Currency { get; init; }

    public bool CurrencyKnown { get; init; }

    public bool Pawn { get; init; }

    public bool PawnKnown { get; init; }

    // Pawn rows only: what SelectPawnPurchase ranks.
    public IReadOnlyList<ProfileSkill> Skills { get; init; } = [];

    public bool ViolenceCapable { get; init; }

    public bool ViolenceCapableKnown { get; init; }

    /// <summary>A non-pawn row's first colony thing: a packed sculpture's row names its packed item.</summary>
    public string ThingID { get; init; } = "";

    /// <summary>A gear row's hit-point fraction and ZoneID the stockpile its thing lies in; what SaleGear matches.</summary>
    public double HitPoints { get; init; }

    public bool HitPointsKnown { get; init; }

    /// <summary>A gear row's native QualityCategory index (Awful 0 .. Legendary 6).</summary>
    public int Quality { get; init; }

    public bool QualityKnown { get; init; }

    public string ZoneID { get; init; } = "";

    /// <summary>A stuffed gear row's material (empty otherwise): what SaleGearSurplus matches beside the definition and condition.</summary>
    public string Stuff { get; init; } = "";

    /// <summary>A pawn row's load id (the animal census id for a colony animal): the key a live-animal sale matches and a purchase can name.</summary>
    public string PawnID { get; init; } = "";

    /// <summary>A pawn row's gender ("Male"/"Female"), as an animal's census gender: what a purchase of a founder's missing sex matches.</summary>
    public string PawnGender { get; init; } = "";

    // A prisoner row's sale facts: guest status, the secure and downed tests, and the extra home / host faction ids
    // a sale angers.
    public string GuestStatus { get; init; } = "";

    public bool PrisonerSecure { get; init; }

    public bool PrisonerSecureKnown { get; init; }

    public bool PawnDowned { get; init; }

    public bool PawnDownedKnown { get; init; }

    public string ExtraHomeFaction { get; init; } = "";

    public string ExtraHostFaction { get; init; } = "";
}

/// <summary>
/// Everything SelectTrade reads: the complete unfiltered sheet, both sides' current silver, the economic floors
/// and production-halted definitions EconomicReserves computed, and this request's own net-spend ceiling.
/// Complete is the caller's assertion that the sheet it passed was read whole and unfiltered; SelectTrade refuses
/// outright when it is false, matching Python's refusal on a nonzero omittedByRowCap/omittedByFilter.
/// </summary>
public sealed record TradeSelectionFacts
{
    public IReadOnlyDictionary<string, long> CropSurplusFloors { get; init; } = new Dictionary<string, long>();

    public bool Complete { get; init; }

    public IReadOnlyList<TradeSheetRowFact> Rows { get; init; } = [];

    public long ColonySilver { get; init; }

    public long TraderSilver { get; init; }

    public bool SilverKnown { get; init; }

    public IReadOnlyDictionary<string, long> Floors { get; init; } = new Dictionary<string, long>();

    public IReadOnlyList<string> Stopped { get; init; } = [];

    public long MaxSilverSpend { get; init; }

    /// <summary>The packed art the colony may sell (SaleSculptures, by packed item id); ArtFirst sells it before the targets, when the wealth headroom is negative.</summary>
    public IReadOnlySet<string> SaleArt { get; init; } = new HashSet<string>();

    public bool ArtFirst { get; init; }

    /// <summary>The colony animals the herd plan lets go (HerdSaleAnimals), set only while the colony wants silver. Each sells through its own pawn row.</summary>
    public IReadOnlySet<PawnId> SaleAnimals { get; init; } = new HashSet<PawnId>();

    /// <summary>The worn-dump gear the colony sells (SaleGear, by thing id): protected natively, so the accept names these ids.</summary>
    public IReadOnlySet<string> SaleGear { get; init; } = new HashSet<string>();

    /// <summary>The animals the herd plan lacks (HerdWants), best first: the pawn-purchase line buys the first affordable one.</summary>
    public IReadOnlyList<HerdWant> HerdWants { get; init; } = [];

    /// <summary>Marks a favor-currency session (the tribute collector): SelectFavorSale decides it, selling gold above FavorKeep.</summary>
    public bool Favor { get; init; }

    public long FavorKeep { get; init; }

    /// <summary>The surplus prisoners (RoundsFacts.SurplusPrisoners) a favor session sells.</summary>
    public IReadOnlySet<PawnId> FavorPrisoners { get; init; } = new HashSet<PawnId>();
}

/// <summary>
/// One selected row adjustment: the native line id SetTradeLines addresses, the definition it came from, and the
/// signed count (positive buys, negative sells) the trade line carries as its AbsoluteCount.
/// </summary>
public sealed record TradeSelectionLine(string LineID, string DefName, long Count);

/// <summary>
/// One target's decision row, mirroring Python's evidence dictionaries one for one: either a blocker naming why
/// the target was skipped, or the matched quantities that produced its count.
/// </summary>
public sealed record TradeSelectionEvidence
{
    public string Item { get; init; } = "";

    public string Blocker { get; init; } = "";

    public bool Matched { get; init; }

    public long EligibleStock { get; init; }

    public long RetainedTarget { get; init; }

    public long Count { get; init; }

    public long ExportCapacity { get; init; }
}

/// <summary>
/// One whole economic decision: the lines to stage, the per-target evidence behind them, and the silver reserve
/// the AcceptTrade floors must carry. Refused is set when the facts themselves were unusable, in which case
/// Selected and Evidence are empty and Reason explains why, distinct from an admissible decision that simply
/// selected nothing.
/// </summary>
public sealed record TradeSelection
{
    public bool Refused { get; init; }

    public string Reason { get; init; } = "";

    public IReadOnlyList<TradeSelectionLine> Selected { get; init; } = [];

    public IReadOnlyList<TradeSelectionEvidence> Evidence { get; init; } = [];

    public long SilverReserve { get; init; }

    public long Budget { get; init; }
}

/// <summary>
/// What EconomicReserves folds into economic floors: the player's own per-resource reserves and spending
/// restrictions (the governor's configured production policy) and native's outstanding construction commitments.
/// </summary>
public sealed record TradeReserveFacts
{
    public IReadOnlyDictionary<string, long> Reserves { get; init; } = new Dictionary<string, long>();

    public IReadOnlyList<string> Restricted { get; init; } = [];

    public IReadOnlyDictionary<string, long> Construction { get; init; } = new Dictionary<string, long>();
}

public static partial class PolicyFunctions
{
    internal const string TradeBlockerAmbiguous = "Unavailable or ambiguous native definition";

    internal const string TradeBlockerRow = "Ineligible trade row";

    internal const string TradeBlockerProtected = "Food outside the authorized crop surplus";

    internal const string TradeBlockerUnknown = "Economic stock or price evidence is unknown";

    /// <summary>The surplus sale's MinSellPrice (RoundsTradeTargets): any positive price.</summary>
    internal const double ArtMinSellPrice = double.Epsilon;

    internal static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>The definition of the sheet's currency row: the coin both sides' cash and every price are in. A sheet with no known currency row has none.</summary>
    public static (string Currency, bool Ok) TradeCurrency(IReadOnlyList<TradeSheetRowFact> rows)
    {
        foreach (var row in rows)
        {
            if (row.CurrencyKnown && row.Currency)
            {
                return (row.DefName, true);
            }
        }
        return ("", false);
    }

    /// <summary>
    /// Ports trade_policy.py's select_trade: a pure decision over one complete trade sheet and the
    /// already-computed economic floors. Explicit target order sets purchase priority. Sales use only observed
    /// eligible surplus, capped by the buyer's current cash; purchases never rely on anticipated sale proceeds or
    /// future production, and spend a budget that is fixed before the first line is chosen. It decides nothing
    /// about which trader to open with, whether a deal is worth taking, or whether the colony may afford it:
    /// native's own preview remains the authority on all three.
    /// </summary>
    public static TradeSelection SelectTrade(TradeEconomicPolicy p, TradeSelectionFacts facts)
    {
        static TradeSelection Refuse(string reason) => new() { Refused = true, Reason = reason };

        // Art or animals alone sell without a catalog target.
        if (p.Targets.Count > 0 || facts.SaleArt.Count + facts.SaleAnimals.Count + facts.SaleGear.Count == 0)
        {
            try
            {
                p.Validate();
            }
            catch (DomainException e)
            {
                return Refuse(e.Message);
            }
        }
        else if (p.SilverReserve < 0)
        {
            return Refuse("economic silver reserve out of range");
        }
        if (!facts.Complete)
        {
            return Refuse("Economic selection requires an unfiltered complete trade sheet");
        }
        var (currency, hasCurrency) = TradeCurrency(facts.Rows);
        if (!hasCurrency || !facts.SilverKnown || facts.ColonySilver < 0 || facts.TraderSilver < 0 || facts.MaxSilverSpend < 0)
        {
            return Refuse(TradeBlockerUnknown);
        }
        var stopped = new HashSet<string>(facts.Stopped, StringComparer.Ordinal);
        var reserve = Math.Max(p.SilverReserve, facts.Floors.GetValueOrDefault(currency));
        var budgetCap = Math.Min(facts.MaxSilverSpend, Math.Max(0, facts.ColonySilver - reserve));
        if (stopped.Contains(currency))
        {
            budgetCap = 0;
        }
        // Budget and the trader's cash are carried as exact running floats, the way Python carries them, because
        // prices are fractional: rounding either to whole silver between lines would let a later line spend money
        // an earlier one had already committed.
        double budget = budgetCap;
        double traderCash = facts.TraderSilver;
        var selected = new List<TradeSelectionLine>();
        var evidence = new List<TradeSelectionEvidence>();
        if (facts.ArtFirst)
        {
            SellArt(selected, evidence, facts, stopped, ref traderCash);
        }
        foreach (var target in p.Targets)
        {
            TradeSheetRowFact? row = null;
            var ambiguous = false;
            foreach (var candidate in facts.Rows)
            {
                if (candidate.DefName != target.Item)
                {
                    continue;
                }
                if (row is not null)
                {
                    ambiguous = true;
                    break;
                }
                row = candidate;
            }
            if (row is null || ambiguous)
            {
                evidence.Add(new TradeSelectionEvidence { Item = target.Item, Blocker = TradeBlockerAmbiguous });
                continue;
            }
            // Classification comes from ThingDef, never localized names or model guesses; an unknown flag is
            // never read as a permissive default.
            if (!row.TraderWillTradeKnown || !row.TraderWillTrade || !row.PawnKnown || row.Pawn || !row.CurrencyKnown || row.Currency)
            {
                evidence.Add(new TradeSelectionEvidence { Item = target.Item, Blocker = TradeBlockerRow });
                continue;
            }
            if (row.ColonyCount < 0 || row.TraderCount < 0)
            {
                evidence.Add(new TradeSelectionEvidence { Item = target.Item, Blocker = TradeBlockerUnknown });
                continue;
            }
            var stock = row.ColonyCount;
            var supply = row.TraderCount;
            var floor = Math.Max(target.Stock, facts.Floors.GetValueOrDefault(target.Item));
            var cropAuthorized = facts.CropSurplusFloors.TryGetValue(target.Item, out var cropFloor);
            var foodKnown = row.Food.TryGetValue(out var food);
            cropAuthorized = cropAuthorized && cropFloor > 0 && foodKnown && ValidTradeFood(food!) && food!.Crop;
            if (cropAuthorized)
            {
                floor = Math.Max(floor, cropFloor);
            }
            var count = 0L;
            if (stock < floor && target.MaxBuy > 0)
            {
                if (!row.BuyPriceKnown || !Finite(row.BuyPrice) || row.BuyPrice < 0)
                {
                    evidence.Add(new TradeSelectionEvidence { Item = target.Item, Blocker = TradeBlockerUnknown });
                    continue;
                }
                var price = row.BuyPrice;
                if (price > 0)
                {
                    count = Math.Min(Math.Min(floor - stock, supply), Math.Min(target.MaxBuy, (long)Math.Floor(budget / price)));
                    if (count < 0)
                    {
                        count = 0;
                    }
                    budget -= count * price;
                }
            }
            else if (stock > floor && target.MaxSell > 0 && !stopped.Contains(target.Item))
            {
                if (cropAuthorized)
                {
                    cropAuthorized = SelectedProteinPurchase(selected, facts.Rows);
                }
                // Food sells only as the authorized crop surplus; every other row sells as the target says
                // (native keeps no export protection).
                var listed = facts.CropSurplusFloors.ContainsKey(target.Item);
                if ((foodKnown || listed) && !cropAuthorized)
                {
                    evidence.Add(new TradeSelectionEvidence { Item = target.Item, Blocker = TradeBlockerProtected });
                    continue;
                }
                if (!row.SellPriceKnown || !Finite(row.SellPrice) || row.SellPrice < 0)
                {
                    evidence.Add(new TradeSelectionEvidence { Item = target.Item, Blocker = TradeBlockerUnknown });
                    continue;
                }
                var price = row.SellPrice;
                if (price > 0 && price >= target.MinSellPrice)
                {
                    count = -Math.Min(Math.Min(stock - floor, target.MaxSell), (long)Math.Floor(traderCash / price));
                    if (count > 0)
                    {
                        count = 0;
                    }
                    traderCash += count * price;
                }
            }
            evidence.Add(
                new TradeSelectionEvidence
                {
                    Item = target.Item,
                    Matched = true,
                    EligibleStock = stock,
                    RetainedTarget = floor,
                    Count = count,
                    ExportCapacity = Math.Max(0, stock - floor),
                }
            );
            if (count != 0)
            {
                selected.Add(new TradeSelectionLine(row.LineID, row.DefName, count));
            }
        }
        if (!facts.ArtFirst)
        {
            SellArt(selected, evidence, facts, stopped, ref traderCash);
        }
        SellAnimals(selected, evidence, facts, stopped, ref traderCash);
        SellGear(selected, evidence, facts, stopped, ref traderCash);
        return new TradeSelection
        {
            Selected = selected,
            Evidence = evidence,
            SilverReserve = reserve,
            Budget = budgetCap,
        };
    }

    // The art-sale step: each row whose thing is sale art sells that one piece, line by line, under the same rules
    // as a surplus sale: a tradeable unprotected row, a known positive price (surplus MinSellPrice) and the
    // trader's remaining cash. A line already selected is left alone.
    private static void SellArt(List<TradeSelectionLine> selected, List<TradeSelectionEvidence> evidence, TradeSelectionFacts facts, HashSet<string> stopped, ref double traderCash)
    {
        if (facts.SaleArt.Count == 0)
        {
            return;
        }
        SellRows(selected, evidence, facts.Rows, stopped, ref traderCash, false, row => row.ThingID.Length != 0 && facts.SaleArt.Contains(row.ThingID));
    }

    // The live-animal sale step: each pawn row whose pawn id is a sale animal sells that one animal under the same
    // rules. The herd plan that chose the animal is the authorization.
    private static void SellAnimals(List<TradeSelectionLine> selected, List<TradeSelectionEvidence> evidence, TradeSelectionFacts facts, HashSet<string> stopped, ref double traderCash)
    {
        if (facts.SaleAnimals.Count == 0)
        {
            return;
        }
        SellRows(selected, evidence, facts.Rows, stopped, ref traderCash, true, row => row.PawnID.Length != 0 && facts.SaleAnimals.Contains(new PawnId(row.PawnID)));
    }

    // The gear-sale step: each row whose thing is sale gear sells that one piece under the same rules.
    private static void SellGear(List<TradeSelectionLine> selected, List<TradeSelectionEvidence> evidence, TradeSelectionFacts facts, HashSet<string> stopped, ref double traderCash)
    {
        if (facts.SaleGear.Count == 0)
        {
            return;
        }
        SellRows(selected, evidence, facts.Rows, stopped, ref traderCash, false, row => row.ThingID.Length != 0 && facts.SaleGear.Contains(row.ThingID));
    }

    /// <summary>
    /// The gear the colony sells: apparel or weapons lying in a warehouse zone (warehouses, by zone id) whose hit
    /// points are below the gear hit-point floor or whose quality is below the gear quality floor. The warehouse
    /// disallows burnable gear, so what lies there below the keep floors is worth selling. Unknown hit points or
    /// quality is not sale gear. The trader's willingness to trade a row (biocoded gear is refused natively) is
    /// checked at selection.
    /// </summary>
    public static HashSet<string> SaleGear(IReadOnlyList<TradeSheetRowFact> rows, IReadOnlySet<string> warehouses)
    {
        var floor = DomainConstants.GearQualityFloor.Rank();
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.ThingID.Length == 0 || row.Pawn || !warehouses.Contains(row.ZoneID) || !row.HitPointsKnown || !row.QualityKnown)
            {
                continue;
            }
            if (row.HitPoints < DomainConstants.GearHitPointFloor || row.Quality < floor)
            {
                output.Add(row.ThingID);
            }
        }
        return output;
    }

    // Sells each wanted single-item row; pawns says the rows are pawn rows.
    private static void SellRows(
        List<TradeSelectionLine> selectedLines,
        List<TradeSelectionEvidence> evidenceRows,
        IReadOnlyList<TradeSheetRowFact> rows,
        HashSet<string> stopped,
        ref double traderCash,
        bool pawns,
        Func<TradeSheetRowFact, bool> wanted
    )
    {
        var selected = new HashSet<string>(selectedLines.Select(l => l.LineID), StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!wanted(row) || selected.Contains(row.LineID))
            {
                continue;
            }
            var evidence = new TradeSelectionEvidence { Item = row.DefName };
            if (!row.TraderWillTradeKnown || !row.TraderWillTrade || !row.PawnKnown || row.Pawn != pawns || !row.CurrencyKnown || row.Currency || stopped.Contains(row.DefName))
            {
                evidence = evidence with { Blocker = TradeBlockerRow };
            }
            else if (row.ColonyCount < 1 || !row.SellPriceKnown || !Finite(row.SellPrice) || row.SellPrice < 0)
            {
                evidence = evidence with { Blocker = TradeBlockerUnknown };
            }
            if (evidence.Blocker.Length != 0)
            {
                evidenceRows.Add(evidence);
                continue;
            }
            evidence = evidence with { Matched = true, EligibleStock = 1, ExportCapacity = 1 };
            var price = row.SellPrice;
            if (price >= ArtMinSellPrice && price <= traderCash)
            {
                evidence = evidence with { Count = -1 };
                traderCash -= price;
                selectedLines.Add(new TradeSelectionLine(row.LineID, row.DefName, -1));
            }
            evidenceRows.Add(evidence);
        }
    }

    /// <summary>
    /// Ports trade_policy.py's economic_reserves: the per-def stock a trade must never sell below, and the
    /// production-halted defs a trade must never sell at all. The resource policy's own reserves, each economic
    /// target's own stock level raised over them, and the construction-commitment addition are ported; Python's
    /// fourth source, an active colony goal's target quantity, is deliberately omitted because a standard carries
    /// no target quantity.
    /// </summary>
    public static (IReadOnlyDictionary<string, long> Floors, IReadOnlyList<string> Stopped) EconomicReserves(TradeEconomicPolicy p, TradeReserveFacts facts)
    {
        var bases = new Dictionary<string, long>(StringComparer.Ordinal);
        var floors = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (resource, reserve) in facts.Reserves)
        {
            if (reserve <= 0)
            {
                continue;
            }
            bases[resource] = reserve;
            floors[resource] = reserve;
        }
        var targets = new Dictionary<string, long>(bases, StringComparer.Ordinal);
        foreach (var target in p.Targets)
        {
            targets[target.Item] = Math.Max(targets.GetValueOrDefault(target.Item), target.Stock);
        }
        foreach (var (resource, target) in targets)
        {
            floors[resource] = floors.GetValueOrDefault(resource) + target - bases.GetValueOrDefault(resource);
        }
        foreach (var (resource, needed) in facts.Construction)
        {
            if (needed <= 0)
            {
                continue;
            }
            floors[resource] = floors.GetValueOrDefault(resource) + needed;
        }
        foreach (var resource in floors.Where(kv => kv.Value <= 0).Select(kv => kv.Key).ToList())
        {
            floors.Remove(resource);
        }
        var stopped = new List<string>(facts.Restricted);
        stopped.Sort(StringComparer.Ordinal);
        return (floors, stopped);
    }
}
