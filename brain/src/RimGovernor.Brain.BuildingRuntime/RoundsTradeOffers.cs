namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The priced offers of the caravans the negotiator has looked at, in memory only (derived state):
/// RoundsTradePlanner records one at each sheet read, the Round's supply plan reads the fresh ones as trade
/// candidates, and a record is dropped when its trader leaves the census or the world changes. The food plan
/// reads the same records to present traders as trade candidates.
/// </summary>
internal sealed class TradeOfferBook
{
    private readonly object _gate = new();
    private GenerationSnapshot _snapshot;
    private Dictionary<string, Pol.TradeOffers>? _records;

    // Counts the records and drops, so a plan built before one is rebuilt after it.
    private ulong _revision;

    /// <summary>Whether two snapshots are one loaded world: a caravan's offers outlive plan revisions and native generations, not a load or map.</summary>
    internal static bool SameOfferWorld(GenerationSnapshot a, GenerationSnapshot b) =>
        a.Colony == b.Colony && a.Load == b.Load && a.Map == b.Map;

    /// <summary>Replaces the trader's offers.</summary>
    public void Record(GenerationSnapshot snapshot, Pol.TradeOffers offers)
    {
        lock (_gate)
        {
            if (!SameOfferWorld(_snapshot, snapshot) || _records is null)
            {
                _snapshot = snapshot;
                _records = [];
            }
            _records[offers.Trader] = offers;
            _revision++;
        }
    }

    /// <summary>
    /// The records of the traders on the census that can trade and whose record still describes them
    /// (TradeOffersFresh), in trader id order. A trader that left the census loses its record.
    /// </summary>
    public List<Pol.TradeOffers> Fresh(GenerationSnapshot snapshot, Tick now, long silver, IReadOnlyList<Pol.TraderFacts> traders)
    {
        lock (_gate)
        {
            if (!SameOfferWorld(_snapshot, snapshot) || _records is null)
            {
                return [];
            }
            var present = new Dictionary<string, Pol.TraderFacts>();
            foreach (var t in traders)
            {
                present[t.Id] = t;
            }
            var output = new List<Pol.TradeOffers>();
            foreach (var (id, record) in _records.ToList())
            {
                if (!present.TryGetValue(id, out var t))
                {
                    _records.Remove(id);
                    _revision++;
                    continue;
                }
                if (t.CanTrade && Pol.PolicyFunctions.TradeOffersFresh(record, now, silver, t.GoodsStacks))
                {
                    output.Add(record);
                }
            }
            output.Sort((a, c) => string.CompareOrdinal(a.Trader, c.Trader));
            return output;
        }
    }

    public ulong Version()
    {
        lock (_gate)
        {
            return _revision;
        }
    }

    public void Drop(string trader)
    {
        lock (_gate)
        {
            if (_records is not null && _records.Remove(trader))
            {
                _revision++;
            }
        }
    }
}

public sealed partial class Rounder
{
    internal TradeOfferBook TradeOffers { get; } = new();
}

public sealed partial class RoundsTradePlanner
{
    /// <summary>
    /// Books what the open session's sheet prices: every non-currency row the trader holds at a known price,
    /// with the colony's silver and the trader's goods stacks the record's freshness is judged against. The plan
    /// reads it from the next plan build on (the book's revision keys the cache).
    /// </summary>
    private void RecordOffers(CancellationToken call, ControlState state, TradeSheetRead sheet, long stacks)
    {
        var (silver, _, silverKnown) = TradeSheetSilver(sheet.Rows);
        if (!silverKnown)
        {
            return;
        }
        var tables = Native.FrameTables(call, Boundary.Identity(state.Snapshot));
        if (tables.Catalog is null)
        {
            throw new ControlException("recordOffers: no definition catalog");
        }
        var rows = new List<Pol.TradeOffer>();
        foreach (var row in sheet.Rows)
        {
            if ((row.CurrencyKnown && row.Currency) || row.TraderCount <= 0 || !row.BuyPriceKnown)
            {
                continue;
            }
            var food = TradeFoodFact(row, tables.Catalog);
            rows.Add(new Pol.TradeOffer { Def = row.DefName, Count = row.TraderCount, Price = row.BuyPrice, Pawn = row.PawnKnown && row.Pawn, Gender = row.PawnGender, Food = food });
        }
        var offers = new Pol.TradeOffers { Participant = BridgePackage.TradeParticipantOf(sheet.Target), Trader = sheet.Trader, Negotiator = sheet.Negotiator, Tick = new Tick(sheet.Context.Tick), Silver = silver, GoodsStacks = stacks, Rows = rows };
        Reviewer.TradeOffers.Record(state.Snapshot, offers);
    }

    /// <summary>The units of each resource the Round's supply plan opened to buy from trader, empty when MaintainResource has no work.</summary>
    private IReadOnlyDictionary<Pol.Resource, long> PlannedPurchases(CancellationToken call, ControlState state, Sto.Rounds review, string trader)
    {
        var (goal, workable) = Reviewer.Player.Journal.Workable(call, review, MaintainResource);
        if (!workable)
        {
            return new Dictionary<Pol.Resource, long>();
        }
        var supply = Reviewer.ResourceSupply(call, state, review, goal);
        return supply.TradeLines(trader);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// Keeps the resource purchases of need (a MaintainResource shortfall, the component target) and its food
    /// nutrition (what the food plan opened to buy from this trader) only as far as the plans opened them;
    /// medicine, surgery parts, ingredient upgrades and every sale stay.
    /// </summary>
    internal static Pol.TradeNeed RestrictToPlan(Pol.TradeNeed need, IReadOnlyDictionary<Pol.Resource, long> planned, double food)
    {
        var kept = new List<Pol.Amount>();
        foreach (var shortfall in need.Shortfall)
        {
            var units = Math.Min(shortfall.Count, planned.GetValueOrDefault(shortfall.Resource));
            if (units > 0)
            {
                kept.Add(new Pol.Amount { Resource = shortfall.Resource, Count = units });
            }
        }
        return need with
        {
            Food = need.Food with { Nutrition = food },
            Shortfall = kept,
            ComponentShortfall = Math.Min(need.ComponentShortfall, planned.GetValueOrDefault(Pol.PolicyConstants.ComponentResource)),
        };
    }
}
