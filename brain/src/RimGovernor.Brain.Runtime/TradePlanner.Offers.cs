namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The priced offers of the caravans the negotiator has looked at, in memory only (derived state):
/// TradePlanner records one at each sheet read, the Round's supply plan reads the fresh ones as trade
/// candidates, and a record is dropped when its trader leaves the census or the world changes. The food plan
/// reads the same records to present traders as trade candidates.
/// </summary>
internal sealed partial class TradeOfferBook
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
    public List<Pol.TradeOffers> Fresh(GenerationSnapshot snapshot, Tick now, long silver, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.Trader> traders)
    {
        lock (_gate)
        {
            if (!SameOfferWorld(_snapshot, snapshot) || _records is null)
            {
                return [];
            }
            var present = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.Trader>();
            foreach (var t in traders)
            {
                present[t.Key()] = t;
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
                if (t.CanTrade && Pol.TradePolicyRules.TradeOffersFresh(record, now, silver, t.GoodsStacks))
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

public sealed partial class ColonyReview
{
    internal TradeOfferBook TradeOffers { get; } = new();
}

public sealed partial class TradePlanner
{
    /// <summary>
    /// Books what the open session's sheet prices: every non-currency row the trader holds at a known price,
    /// with the colony's silver and the trader's goods stacks the record's freshness is judged against. The plan
    /// reads it from the next plan build on (the book's revision keys the cache).
    /// </summary>
    private async Task RecordOffersAsync(ControlState state, RimGovernor.Brain.GameFacts.Observations.TradeSheet sheet, long stacks, CancellationToken call)
    {
        var (silver, _, silverKnown) = TradeSheetSilver(sheet.Lines);
        if (!silverKnown)
        {
            return;
        }
        if (Native is not IDefinitionSource definitions)
        {
            throw new ControlException("recordOffers: the native source serves no definitions");
        }
        var catalog = await definitions.DefinitionCatalogAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        var rows = new List<Pol.TradeOffer>();
        foreach (var row in sheet.Lines)
        {
            if (row.Currency || row.TraderCount <= 0)
            {
                continue;
            }
            rows.Add(row.Pawn is { } pawn
                ? new Pol.TradeOffer.Pawn { Def = row.DefName, Count = row.TraderCount, Price = row.BuyPrice, Gender = pawn.Gender }
                : row.DefName.Length != 0 && catalog.TradeFood(row.DefName) is { } good
                    ? new Pol.TradeOffer.Food { Def = row.DefName, Count = row.TraderCount, Price = row.BuyPrice, Good = good }
                    : new Pol.TradeOffer.Goods { Def = row.DefName, Count = row.TraderCount, Price = row.BuyPrice });
        }
        var participant = TradeTarget.TradeParticipantOf(sheet.Target);
        var offers = new Pol.TradeOffers { Participant = participant, Trader = participant.Key(), Tick = new Tick(sheet.Context.Tick), Silver = silver, GoodsStacks = stacks, Rows = rows };
        Reviewer.TradeOffers.Record(state.Snapshot, offers);
    }

    /// <summary>The units of each resource the Round's supply plan opened to buy from trader, empty without resource targets.</summary>
    private async Task<IReadOnlyDictionary<Pol.Resource, long>> PlannedPurchasesAsync(ControlState state, Sto.ReviewState review, string trader, CancellationToken call)
    {
        if (Reviewer.ResourceTargets(state.Snapshot).Count == 0)
        {
            return new Dictionary<Pol.Resource, long>();
        }
        var goal = Reviewer.Player.Journal.LoadNeed(EconomyFamily.Resource, state.Snapshot, call);
        var supply = await Reviewer.ResourceSupplyAsync(state, review, goal, call).ConfigureAwait(false);
        return supply.TradeLines(trader);
    }
}
