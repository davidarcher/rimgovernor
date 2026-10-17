namespace RimGovernor.Brain.Policy;

// The Royalty tribute collector pays royal favor (honor) instead of silver: a caravan of
// TraderKind TributeCollectorKind opens a favor-currency session (the trade sheet's FavorCurrency), where every
// row's sell price is the item's favor value and the trader's count is a placeholder, never a silver budget. The
// bot sells it surplus gold and surplus prisoners.

public static partial class TradePolicyRules
{
    /// <summary>The trader kind of the Empire's tribute collector.</summary>
    public const string TributeCollectorKind = "Empire_Caravan_TributeCollector";

    /// <summary>The one resource sold for favor.</summary>
    public static readonly Resource FavorSaleResource = new("Gold");

    /// <summary>The history event selling a prisoner raises; the precept rule answers it.</summary>
    public const string SoldPrisonerEvent = "SoldPrisoner";

    /// <summary>Whether a trader is the tribute collector.</summary>
    public static bool IsTributeCollector(TraderFacts t) => t.Kind == TributeCollectorKind;

    /// <summary>
    /// The gold a favor sale never sells below: the largest of the resource target, the economic floor
    /// (construction commitments) and the retained stock the wealth-surplus rule already keeps (TradeRetained).
    /// While the retained stock is unknown nothing is surplus: the keep is the largest stock, so a sale takes none.
    /// </summary>
    public static long FavorGoldKeep(IReadOnlyDictionary<Resource, long> targets, IReadOnlyDictionary<string, long> floors, Fact<IReadOnlyDictionary<Resource, long>> retainedFact)
    {
        if (!retainedFact.TryGetValue(out var retained))
        {
            return long.MaxValue;
        }
        return Math.Max(targets.GetValueOrDefault(FavorSaleResource), Math.Max(floors.GetValueOrDefault(FavorSaleResource.Value), retained.GetValueOrDefault(FavorSaleResource)));
    }

    /// <summary>
    /// Adds the favor-sale reason to the need: a tribute collector is present or arriving and the known gold
    /// stock exceeds FavorGoldKeep. Unknown traders, stock or need add nothing.
    /// </summary>
    public static Fact<TradeNeed> FavorGoldNeed(
        Fact<TradeNeed> need,
        Fact<EquatableArray<TraderFacts>> traders,
        Fact<EquatableArray<Amount>> resources,
        IReadOnlyDictionary<Resource, long> targets,
        IReadOnlyDictionary<string, long> floors,
        Fact<IReadOnlyDictionary<Resource, long>> retained
    )
    {
        var nk = need.TryGetValue(out var n);
        var tk = traders.TryGetValue(out var rows);
        var sk = resources.TryGetValue(out var stock);
        if (!nk || !tk || !sk)
        {
            return need;
        }
        var present = false;
        foreach (var row in rows!)
        {
            present = present || (IsTributeCollector(row) && (row.CanTrade || row.Travelling));
        }
        if (!present)
        {
            return need;
        }
        var gold = 0L;
        foreach (var row in stock!)
        {
            if (row.Resource == FavorSaleResource)
            {
                gold += row.Count;
            }
        }
        var surplus = gold - FavorGoldKeep(targets, floors, retained);
        if (surplus > 0)
        {
            return Known(n! with { FavorGold = surplus });
        }
        return need;
    }

    /// <summary>
    /// Reports that the ideoligion does not plainly allow selling a prisoner: disapproved (a mood cost or
    /// refusal), or unread with Ideology installed. Without the expansion, or when precepts only approve, the sale
    /// proceeds.
    /// </summary>
    public static bool FavorPrisonersHeld(IdeologyRead ideology) =>
        ideology.ActionStance(new PreceptAction(SoldPrisonerEvent, false), new PreceptSubject()).Stance is not (PreceptStance.Allowed or PreceptStance.Approved);

    /// <summary>
    /// The prisoners (by pawn id) the colony can sell to the tribute collector: living, not recruitable-wanted
    /// (HarvestEligible), no creepjoiner, no queued surgery or execution, not one the prisoners family would enslave
    /// and not one the organ harvest or part recovery would cut (judged row by row against the surgery planner's
    /// needs). Empty while any needed fact is unknown or the precepts do not plainly allow the sale.
    /// </summary>
    public static IReadOnlySet<PawnId> SurplusPrisoners(this RoundsFacts f, bool silverShort)
    {
        var rk = f.Prisoners.TryGetValue(out var rows);
        var ck = f.PrisonerColony.TryGetValue(out var colony);
        var mk = f.MedicalPawns.TryGetValue(out _);
        if (!rk || !ck || !mk || FavorPrisonersHeld(f.IdeologyRead()))
        {
            return new HashSet<PawnId>();
        }
        var stock = new Dictionary<Resource, long>();
        f.Resources.TryGetValue(out var resources);
        foreach (var row in resources)
        {
            stock[row.Resource] = stock.GetValueOrDefault(row.Resource) + row.Count;
        }
        var wants = SurgeryRules.SelectSurgery(f.MedicalPawns, null, f.SurgeryContextOf()).Wants;
        var needs = OrganHarvestRules.OrganNeeds(f.MedicalPawns, wants, silverShort, stock, f.Recipes);
        var parts = OrganHarvestRules.PartRecoveryNeeds(f.MedicalPawns, wants, f.Recipes);
        var output = new HashSet<PawnId>();
        foreach (var row in rows!)
        {
            if (!SurplusPrisoner(row, colony!))
            {
                continue;
            }
            Fact<EquatableArray<PrisonerFacts>> one = Known<EquatableArray<PrisonerFacts>>([row]);
            var col = Known(colony!);
            var harvest = OrganHarvestRules.SelectOrganHarvest(one, col, needs, null) is not null;
            var recovery = OrganHarvestRules.SelectPartRecovery(one, col, parts, null, f.Recipes) is not null;
            if (!harvest && !recovery)
            {
                output.Add(row.Pawn);
            }
        }
        return output;
    }

    private static bool SurplusPrisoner(PrisonerFacts row, PrisonerColony colony)
    {
        var dk = row.Dead.TryGetValue(out var dead);
        var pk = row.Prisoner.TryGetValue(out var prisoner);
        var ek = row.HarvestEligible(colony).TryGetValue(out var eligible);
        var jk = row.CreepJoiner.TryGetValue(out var joiner);
        var qk = row.QueuedSurgeries.TryGetValue(out var queued);
        row.Prospect.TryGetValue(out var prospect);
        if (!dk || dead || !pk || !prisoner || !ek || !eligible || !jk || joiner || !qk || queued > 0 || row.Executing)
        {
            return false;
        }
        return !(colony.SlaveryAllowed() && !row.WildMan && PopulationRules.CanLabor(prospect!));
    }

    /// <summary>Adds the prisoner-sale reason to the need: a tribute collector is present or arriving and some prisoner is surplus.</summary>
    public static Fact<TradeNeed> FavorPrisonerNeed(Fact<TradeNeed> need, Fact<EquatableArray<TraderFacts>> traders, IReadOnlySet<PawnId> surplus)
    {
        var nk = need.TryGetValue(out var n);
        var tk = traders.TryGetValue(out var rows);
        if (!nk || !tk || surplus.Count == 0)
        {
            return need;
        }
        foreach (var row in rows!)
        {
            if (IsTributeCollector(row) && (row.CanTrade || row.Travelling))
            {
                return Known(n! with { FavorPrisoners = surplus.Count });
            }
        }
        return need;
    }

    /// <summary>
    /// A prisoner row the favor session may sell: a surplus prisoner, secure and not downed, holding no extra home
    /// or host faction (a sale would anger it), and a known positive favor price. An unknown flag holds.
    /// </summary>
    private static bool FavorPrisonerRow(TradeSelectionFacts facts, TradeSheetRowFact row) =>
        row.Pawn is { } pawn
        && facts.FavorPrisoners.Contains(new PawnId(pawn.Id))
        && pawn.GuestStatus == RimGovernor.Contracts.Wire.Defs.GuestStatus.Prisoner
        && pawn.PrisonerSecure
        && !pawn.Downed
        && pawn.ExtraHomeFaction.Length == 0
        && pawn.ExtraHostFaction.Length == 0
        && row.TraderWillTradeKnown
        && row.TraderWillTrade
        && row.CurrencyKnown
        && !row.Currency
        && row.ColonyCount >= 1
        && row.SellPriceKnown
        && Finite(row.SellPrice)
        && row.SellPrice > 0;

    /// <summary>
    /// SelectTrade for a favor session: it sells the gold row's stock above keep and each surplus prisoner
    /// (facts.FavorPrisoners), and buys nothing. There is no silver budget and no trader-cash cap; the favor price
    /// must be known and positive, the row tradeable and the stock known. Anything else selects nothing (not a
    /// refusal) so the session is cancelled.
    /// </summary>
    public static TradeSelection SelectFavorSale(TradeSelectionFacts facts, long keep)
    {
        if (!facts.Complete)
        {
            return new TradeSelection { Refused = true };
        }
        var selected = new List<TradeSelectionLine>();
        foreach (var row in facts.Rows)
        {
            if (FavorPrisonerRow(facts, row))
            {
                selected.Add(new TradeSelectionLine(row.LineID, -1));
                continue;
            }
            if (row.DefName != FavorSaleResource.Value)
            {
                continue;
            }
            if (!row.TraderWillTradeKnown || !row.TraderWillTrade || row.Pawn is not null || !row.CurrencyKnown || row.Currency)
            {
                continue;
            }
            if (row.ColonyCount < 0 || !row.SellPriceKnown || !Finite(row.SellPrice) || row.SellPrice <= 0)
            {
                continue;
            }
            var sell = row.ColonyCount - keep;
            if (sell > 0)
            {
                selected.Add(new TradeSelectionLine(row.LineID, -sell));
            }
        }
        return new TradeSelection { Selected = selected };
    }
}
