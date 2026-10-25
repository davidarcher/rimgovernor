using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Caller-supplied snapshot tick; evaluating never reads live world state.</summary>
public sealed record ColonyForecastOrigin(long ObservedTick);

/// <summary>Positive units enter inventory; negative units consume it. Day is relative to the observed tick.</summary>
public sealed record ForecastStockChange(Resource Resource, double Day, double Units);
/// <summary>Supplied Method effects, not a promise of native completion.</summary>
public sealed record ForecastCommitment(string Id, EquatableArray<ForecastStockChange> StockChanges)
{
    public EquatableArray<string> ActivityCompletions { get; init; }
    public string ExclusiveOutcome { get; init; } = "";
    public ForecastActivityOutcome? Outcome { get; init; }
    public EquatableArray<ForecastMaterialClaim> MaterialClaims { get; init; }
    public EquatableArray<ForecastEquipmentClaim> EquipmentClaims { get; init; }
    public EquatableArray<string> UnsupportedEffects { get; init; }
    /// <summary>Mood memories this Method grants when all its activities complete (at the forecast start when it has
    /// none); in-memory forecast evidence, not a wire or save field. The same effective tick applies below.</summary>
    public EquatableArray<ForecastMoodMemory> MoodMemories { get; init; }
    /// <summary>Changes to situational thoughts the forecast otherwise holds constant.</summary>
    public EquatableArray<ForecastSituationalChange> SituationalChanges { get; init; }
    /// <summary>Shifts of a pawn's observed comfortable temperature range (e.g. an outfit's insulation difference).</summary>
    public EquatableArray<ForecastComfortShift> ComfortShifts { get; init; }
    /// <summary>The outfits pawns wear from this commitment's effective tick (#3546, #3621); the harm term prices their armor.
    /// Per pawn the changes apply in effective-tick order.</summary>
    public EquatableArray<ForecastOutfitChange> OutfitChanges { get; init; }
}

/// <summary>The whole outfit (apparel and primary weapon) <paramref name="Pawn"/> wears from the commitment's effective tick;
/// with <see cref="Overlay"/>, the items instead join the outfit then worn in place of the items they conflict with.</summary>
public sealed record ForecastOutfitChange(string Pawn, EquatableArray<GearOption> Items)
{
    /// <summary>A crafted garment (#3621): it replaces only the items it conflicts with (<see cref="GearLoadoutRules.GearConflicts"/>),
    /// so it composes with a swap of the pawn's other slots.</summary>
    public bool Overlay { get; init; }

    /// <summary>The outfit worn after this change to <paramref name="worn"/>: the items, or with <see cref="Overlay"/> the worn items they do not conflict with and the items.</summary>
    public EquatableArray<GearOption> Apply(IEnumerable<GearOption> worn) => Overlay
        ? EquatableArray.From(worn.Where(w => !Items.Any(i => GearLoadoutRules.GearConflicts(w, i))).Concat(Items))
        : Items;
}

/// <summary>The tick, from the forecast start, at which <paramref name="Commitment"/>'s activities all completed: positive
/// infinity when one does not complete inside the horizon or fails (#3881).</summary>
public sealed record ForecastEffectiveTick(string Commitment, double Tick);

/// <summary>One pawn's memory granted by a commitment; it starts at the commitment's effective tick.</summary>
public sealed record ForecastMoodMemory(string Pawn, NeedMemory Memory);

/// <summary>Adds OffsetDelta to the pawn's mood target from the commitment's effective tick; removing an active
/// -11 thought is +11. Every other situational thought stays constant.</summary>
public sealed record ForecastSituationalChange(string Pawn, string Thought, double OffsetDelta);

/// <summary>Adds the deltas to the pawn's observed ComfortableMin/ComfortableMax from the commitment's effective tick.
/// Observed stats include the current outfit, so the deltas are relative to it. Shifts for one pawn add.</summary>
public sealed record ForecastComfortShift(string Pawn, double MinDelta, double MaxDelta);


public sealed record ColonyForecastRequest
{
    public DerivedDemand Demand { get; init; } = new();
    public required ColonyForecastOrigin Origin { get; init; }
    public required double HorizonDays { get; init; }
    public ColonyForecastFacts Facts { get; init; } = new();
    public EquatableArray<ForecastCommitment> Commitments { get; init; }
    public ForecastCoupledContext? Coupled { get; init; }
}

public sealed record ForecastFinding(string Reason, string Commitment);

public sealed record ColonyForecastScenario
{
    public ForecastCoupledScenario? Coupled { get; init; }
    public EquatableArray<ForecastCommitment> Commitments { get; init; }
}

public static class ColonyForecastRules
{
    internal static Dictionary<string, ForecastCommitment> ForecastCommitmentsById(IEnumerable<ForecastCommitment> commitments)
    {
        var result = new Dictionary<string, ForecastCommitment>(StringComparer.Ordinal);
        foreach (var commitment in commitments)
        {
            if (string.IsNullOrWhiteSpace(commitment.Id) || !result.TryAdd(commitment.Id, commitment))
            {
                throw new DomainException("invalid or duplicate forecast commitment");
            }
            foreach (var change in commitment.StockChanges)
            {
                if (!AdmissionRules.ValidResource(change.Resource)
                    || !double.IsFinite(change.Day) || change.Day < 0 || !double.IsFinite(change.Units))
                {
                    throw new DomainException("invalid forecast stock change");
                }
            }
        }
        return result;
    }

    /// <summary>The commitment id a coupled schedule's own timed stock effects are projected under.</summary>
    internal const string TimedEffects = "coupled/timed-effects";

    /// <summary>The scenario frame over the validated observed stock and commitments. The stock shortfalls are each coupled
    /// outcome's findings, walked by <see cref="InventoryWalk"/> against the schedule's own effects (#3468).</summary>
    internal static ColonyForecastScenario ForecastInventory(EquatableArray<Amount> observed,
        IReadOnlyDictionary<string, ForecastCommitment> commitments, double horizon)
        => new() { Commitments = EquatableArray.From(new InventoryBase(observed, commitments, horizon).Ordered) };

    /// <summary>One stock effect in projection order, with the finding it always raises (a day beyond the horizon), made
    /// once. Only a schedule's own activity output can carry unread units (#3894's activity yields).</summary>
    internal readonly record struct InventoryEffect(string Id, Resource Resource, double Day, double Units, ForecastFinding? Note);

    /// <summary>What a stock projection reads besides a schedule's own timed effects (#3468): the observed stock, the
    /// commitments by id with their effects split where the schedule's own sort, the resources and the findings every
    /// projection repeats. A coupled search builds one per commitment set and walks each schedule's effects against it.</summary>
    internal sealed class InventoryBase
    {
        public readonly Dictionary<Resource, double> Stocks = [];
        public readonly HashSet<Resource> Resources = [];
        public readonly Resource[] Sorted;
        public readonly ForecastCommitment[] Ordered;
        public readonly InventoryEffect[] Before, After;
        public readonly ForecastFinding[] Constant;
        public readonly double Horizon;

        public InventoryBase(EquatableArray<Amount> observed, IReadOnlyDictionary<string, ForecastCommitment> commitments, double horizon)
        {
            Horizon = horizon;
            foreach (var row in observed)
            {
                if (!AdmissionRules.ValidResource(row.Resource) || row.Count < 0)
                {
                    throw new DomainException("invalid forecast observed stock");
                }
                Stocks[row.Resource] = Stocks.GetValueOrDefault(row.Resource) + row.Count;
            }
            Ordered = commitments.Values.ToArray();
            Array.Sort(Ordered, static (a, b) => string.CompareOrdinal(a.Id, b.Id));
            Resources.UnionWith(Stocks.Keys);
            var before = new List<InventoryEffect>(); var after = new List<InventoryEffect>();
            foreach (var commitment in Ordered)
                foreach (var change in commitment.StockChanges)
                {
                    Resources.Add(change.Resource);
                    var note = change.Day > horizon ? new ForecastFinding("beyond_horizon", commitment.Id) : null;
                    (string.CompareOrdinal(commitment.Id, TimedEffects) < 0 ? before : after).Add(new(commitment.Id, change.Resource, change.Day, change.Units, note));
                }
            (Before, After) = ([.. before], [.. after]);
            Sorted = [.. Resources];
            Array.Sort(Sorted, static (a, b) => string.CompareOrdinal(a.Value, b.Value));
            var constant = new List<ForecastFinding>();
            foreach (var commitment in Ordered)
            {
                foreach (var effect in commitment.UnsupportedEffects)
                {
                    constant.Add(new ForecastFinding("unsupported:" + effect, commitment.Id));
                }
            }
            Constant = [.. constant];
        }
    }

    /// <summary>One projection's scratch, reused by the next (#3468): walks a base's effects with a schedule's own, adding
    /// the stock findings in projection order.</summary>
    internal sealed class InventoryWalk
    {
        internal readonly Dictionary<Resource, double> Terminal = [];
        private readonly List<InventoryEffect> merged = [];
        private readonly List<Resource> resources = [];
        private readonly HashSet<Resource> extra = [];
        private readonly List<int> timed = [];

        public void Run(InventoryBase stock, ReadOnlySpan<(Resource Resource, double Day, double Units)> own,
            List<ForecastFinding> findings)
        {
            foreach (var finding in stock.Constant) findings.Add(finding);
            merged.Clear(); resources.Clear(); extra.Clear(); Terminal.Clear();
            foreach (var e in stock.Before) merged.Add(e);
            foreach (var e in own) merged.Add(new(TimedEffects, e.Resource, e.Day, e.Units, null));
            foreach (var e in stock.After) merged.Add(e);
            foreach (var r in stock.Sorted) resources.Add(r);
            foreach (var e in own) if (!stock.Resources.Contains(e.Resource) && extra.Add(e.Resource)) resources.Add(e.Resource);
            if (extra.Count != 0) resources.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
            var horizon = stock.Horizon;
            foreach (var resource in resources)
            {
                var balance = stock.Stocks.GetValueOrDefault(resource);
                timed.Clear();
                for (var i = 0; i < merged.Count; i++)
                {
                    var e = merged[i];
                    if (e.Resource != resource) continue;
                    if (e.Day > horizon) findings.Add(e.Note ?? new ForecastFinding("beyond_horizon", e.Id));
                    if (e.Day <= horizon) Place(i);
                }
                for (var start = 0; start < timed.Count;)
                {
                    var day = merged[timed[start]].Day;
                    var end = start;
                    while (end < timed.Count && merged[timed[end]].Day == day) end++;
                    for (var k = start; k < end; k++)
                    {
                        var e = merged[timed[k]];
                        var after = balance + e.Units;
                        if (!double.IsFinite(after))
                        {
                            throw new DomainException("forecast stock balance overflow");
                        }
                        balance = after;
                        if (after < 0 && e.Units < 0)
                        {
                            findings.Add(new ForecastFinding("stock_shortfall", e.Id));
                        }
                    }
                    start = end;
                }
                // With the observed stock unread (named by its own finding) there is no balance to hand on.
                Terminal[resource] = balance;
            }
        }

        /// <summary>Keeps the timed effects by day; same-time inputs precede outputs, a batch cannot finance its own
        /// prerequisites; then by commitment, then in effect order.</summary>
        private void Place(int index)
        {
            var at = timed.Count;
            while (at > 0 && Compare(index, timed[at - 1]) < 0) at--;
            timed.Insert(at, index);
        }

        private int Compare(int x, int y)
        {
            var (a, b) = (merged[x], merged[y]);
            var c = a.Day.CompareTo(b.Day);
            if (c == 0) c = Sign(a.Units).CompareTo(Sign(b.Units));
            if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
            return c == 0 ? x.CompareTo(y) : c;
        }

        private static int Sign(double units) => units > 0 ? 1 : 0;
    }
}
