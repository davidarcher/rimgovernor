using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Caller-supplied snapshot identity; evaluating never reads live world state.</summary>
public sealed record ColonyForecastOrigin(string World, long ObservedTick);

/// <summary>Positive units enter inventory; negative units consume it. Day is relative to the observed tick.</summary>
public sealed record ForecastStockChange(Resource Resource, Fact<double> Day, Fact<double> Units);

/// <summary>Supplied Method effects, not a promise of native completion.</summary>
public sealed record ForecastCommitment(string Id, EquatableArray<ForecastStockChange> StockChanges)
{
    public EquatableArray<string> ActivityCompletions { get; init; }
    public string ExclusiveOutcome { get; init; } = "";
    public ForecastActivityOutcome? Outcome { get; init; }
    public EquatableArray<ForecastMaterialClaim> MaterialClaims { get; init; }
    public EquatableArray<ForecastEquipmentClaim> EquipmentClaims { get; init; }
    public EquatableArray<string> UnsupportedEffects { get; init; }
    /// <summary>Observed standing construction owned by this Method; no second supplied stock effect.</summary>
    public Building? Construction { get; init; }
    public Fact<double> AlternativeLaborCost { get; init; }
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
}

/// <summary>The tick, from the forecast start, at which <paramref name="Commitment"/>'s activities all completed: positive
/// infinity when one does not complete inside the horizon, unknown when a completion is.</summary>
public sealed record ForecastEffectiveTick(string Commitment, Fact<double> Tick);

/// <summary>One pawn's memory granted by a commitment; it starts at the commitment's effective tick.</summary>
public sealed record ForecastMoodMemory(string Pawn, NeedMemory Memory);

/// <summary>Adds OffsetDelta to the pawn's mood target from the commitment's effective tick; removing an active
/// -11 thought is +11. Every other situational thought stays constant.</summary>
public sealed record ForecastSituationalChange(string Pawn, string Thought, double OffsetDelta);

/// <summary>Adds the deltas to the pawn's observed ComfortableMin/ComfortableMax from the commitment's effective tick.
/// Observed stats include the current outfit, so the deltas are relative to it. Shifts for one pawn add.</summary>
public sealed record ForecastComfortShift(string Pawn, double MinDelta, double MaxDelta);

/// <summary>Replace by removing an ID and adding its new version. Empty means continue.</summary>
public sealed record ColonyForecastDelta
{
    public EquatableArray<string> Remove { get; init; }
    public EquatableArray<ForecastCommitment> Add { get; init; }
}

public sealed record ColonyForecastRequest
{
    public DerivedDemand Demand { get; init; } = new();
    public required ColonyForecastOrigin Origin { get; init; }
    public required double HorizonDays { get; init; }
    public ColonyForecastFacts Facts { get; init; } = new();
    public EquatableArray<ForecastCommitment> Commitments { get; init; }
    public ColonyForecastDelta Proposed { get; init; } = new();
    public ForecastConstructionContext Construction { get; init; } = new();
    public ForecastCoupledContext? Coupled { get; init; }
}

public sealed record ForecastFinding(string Reason, string Commitment, Resource Resource, Fact<double> Day, Fact<double> Units);

public sealed record ColonyForecastScenario
{
    public ForecastCoupledScenario? Coupled { get; init; }
    /// <summary>Free for new declared claims, not an inventory trajectory or native reservation.</summary>
    public Fact<IReadOnlyDictionary<Resource, Fact<double>>> MaterialAvailability { get; init; }
    public EquatableArray<ForecastEquipmentCapacity> EquipmentAvailability { get; init; }
    public EquatableArray<ForecastCommitment> Commitments { get; init; }
    public EquatableArray<ForecastConstructionResult> Construction { get; init; }
}

/// <summary>Baseline/delta consequences and observed diagnostics, not native feasibility or a utility score.</summary>
public sealed record ColonyForecast
{
    public required ColonyForecastOrigin Origin { get; init; }
    public double HorizonDays { get; init; }
    public required ColonyDiagnostics Observed { get; init; }
    public required ColonyForecastScenario Baseline { get; init; }
    public required ColonyForecastScenario Proposed { get; init; }
    internal static readonly EquatableArray<string> UnsupportedDomains = EquatableArray.From(new[]
    {
        "pawn_capacity", "needs_and_mood", "activity_dependencies", "infrastructure_effects", "combat_outcomes",
    });
    public EquatableArray<string> Unsupported { get; init; } = UnsupportedDomains;
}

public static class ColonyForecastRules
{
    /// <summary>Evaluate both alternatives from the same inventory. Target floors are not consumption events.</summary>
    public static ColonyForecast ForecastColony(ColonyForecastRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Origin.World) || request.Origin.ObservedTick < 0
            || !double.IsFinite(request.HorizonDays) || request.HorizonDays <= 0)
        {
            throw new DomainException("invalid colony forecast origin or horizon");
        }
        var baseline = ForecastCommitmentsById(request.Commitments);
        var proposed = new Dictionary<string, ForecastCommitment>(baseline, StringComparer.Ordinal);
        foreach (var id in request.Proposed.Remove)
        {
            if (!proposed.Remove(id))
            {
                throw new DomainException("forecast removes an absent or repeated commitment: " + id);
            }
        }
        foreach (var (id, commitment) in ForecastCommitmentsById(request.Proposed.Add))
        {
            if (!proposed.TryAdd(id, commitment))
            {
                throw new DomainException("forecast addition duplicates existing commitment: " + id);
            }
        }
        return new ColonyForecast
        {
            Origin = request.Origin,
            HorizonDays = request.HorizonDays,
            Unsupported = request.Coupled is null ? ColonyForecast.UnsupportedDomains : ["combat_outcomes", "future_weather", "unmodeled_thermal_geometry", "future_meal_thoughts"],
            Observed = ColonyDiagnosticsRules.DiagnoseColony(request.Facts),
            Baseline = request.Coupled is null ? ForecastConstruction.ForecastConstructionScenario(request, baseline) : ForecastCoupled.ForecastCoupledScenarioOf(request, baseline),
            Proposed = request.Coupled is null ? ForecastConstruction.ForecastConstructionScenario(request, proposed) : ForecastCoupled.ForecastCoupledScenarioOf(request, proposed),
        };
    }

    internal static Dictionary<string, ForecastCommitment> ForecastCommitmentsById(IEnumerable<ForecastCommitment> commitments)
    {
        var result = new Dictionary<string, ForecastCommitment>(StringComparer.Ordinal);
        foreach (var commitment in commitments)
        {
            if (commitment.Construction is not null && commitment.StockChanges.Count != 0)
                throw new DomainException("construction stock effects come from observed outstanding materials");
            if (string.IsNullOrWhiteSpace(commitment.Id) || !result.TryAdd(commitment.Id, commitment))
            {
                throw new DomainException("invalid or duplicate forecast commitment");
            }
            foreach (var change in commitment.StockChanges)
            {
                if (!AdmissionRules.ValidResource(change.Resource)
                    || (change.Day.TryGetValue(out var day) && (!double.IsFinite(day) || day < 0))
                    || (change.Units.TryGetValue(out var units) && !double.IsFinite(units)))
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
    internal static ColonyForecastScenario ForecastInventory(Fact<EquatableArray<Amount>> observed,
        IReadOnlyDictionary<string, ForecastCommitment> commitments, double horizon)
        => new() { Commitments = EquatableArray.From(new InventoryBase(observed, commitments, horizon).Ordered) };

    /// <summary>One stock effect in projection order, with the finding it always raises (unknown day or units, or a day
    /// beyond the horizon), made once.</summary>
    internal readonly record struct InventoryEffect(string Id, Resource Resource, Fact<double> Day, Fact<double> Units, ForecastFinding? Note);

    /// <summary>What a stock projection reads besides a schedule's own timed effects (#3468): the observed stock, the
    /// commitments by id with their effects split where the schedule's own sort, the resources and the findings every
    /// projection repeats. A coupled search builds one per commitment set and walks each schedule's effects against it.</summary>
    internal sealed class InventoryBase
    {
        public readonly bool StockKnown;
        public readonly Dictionary<Resource, double> Stocks = [];
        public readonly HashSet<Resource> Resources = [];
        public readonly Resource[] Sorted;
        public readonly ForecastCommitment[] Ordered;
        public readonly InventoryEffect[] Before, After;
        public readonly ForecastFinding[] Constant;
        public readonly double Horizon;

        public InventoryBase(Fact<EquatableArray<Amount>> observed, IReadOnlyDictionary<string, ForecastCommitment> commitments, double horizon)
        {
            Horizon = horizon;
            StockKnown = observed.TryGetValue(out var rows);
            foreach (var row in rows)
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
                    var note = !change.Day.IsKnown || !change.Units.IsKnown ? new ForecastFinding("unknown_stock_effect", commitment.Id, change.Resource, change.Day, change.Units)
                        : change.Day.Value > horizon ? new ForecastFinding("beyond_horizon", commitment.Id, change.Resource, change.Day, change.Units) : null;
                    (string.CompareOrdinal(commitment.Id, TimedEffects) < 0 ? before : after).Add(new(commitment.Id, change.Resource, change.Day, change.Units, note));
                }
            (Before, After) = ([.. before], [.. after]);
            Sorted = [.. Resources];
            Array.Sort(Sorted, static (a, b) => string.CompareOrdinal(a.Value, b.Value));
            var constant = new List<ForecastFinding>();
            if (!StockKnown)
            {
                constant.Add(new ForecastFinding("unknown_initial_stock", "", default, Known(0.0), Unknown<double>()));
            }
            foreach (var commitment in Ordered)
            {
                foreach (var effect in commitment.UnsupportedEffects)
                {
                    constant.Add(new ForecastFinding("unsupported:" + effect, commitment.Id, default, Unknown<double>(), Unknown<double>()));
                }
            }
            Constant = [.. constant];
        }
    }

    /// <summary>One projection's scratch, reused by the next (#3468): walks a base's effects with a schedule's own, adding
    /// the stock findings in projection order.</summary>
    internal sealed class InventoryWalk
    {
        internal readonly Dictionary<Resource, Fact<double>> Terminal = [];
        private readonly List<InventoryEffect> merged = [];
        private readonly List<Resource> resources = [];
        private readonly HashSet<Resource> extra = [];
        private readonly List<int> timed = [];

        public void Run(InventoryBase stock, ReadOnlySpan<(Resource Resource, Fact<double> Day, Fact<double> Units)> own,
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
                var balance = stock.StockKnown ? Known(stock.Stocks.GetValueOrDefault(resource)) : Unknown<double>();
                timed.Clear();
                for (var i = 0; i < merged.Count; i++)
                {
                    var e = merged[i];
                    if (e.Resource != resource) continue;
                    if (!e.Day.TryGetValue(out var day))
                    {
                        // Unknown time may affect any future point, but never rewrites the observed stock.
                        balance = Unknown<double>();
                        findings.Add(e.Note ?? new ForecastFinding("unknown_stock_effect", e.Id, resource, e.Day, e.Units));
                        continue;
                    }
                    if (!e.Units.IsKnown)
                    {
                        findings.Add(e.Note ?? new ForecastFinding("unknown_stock_effect", e.Id, resource, e.Day, e.Units));
                    }
                    else if (day > horizon)
                    {
                        findings.Add(e.Note ?? new ForecastFinding("beyond_horizon", e.Id, resource, e.Day, e.Units));
                    }
                    if (day <= horizon) Place(i);
                }
                for (var start = 0; start < timed.Count;)
                {
                    var day = merged[timed[start]].Day.Value;
                    var end = start;
                    while (end < timed.Count && merged[timed[end]].Day.Value == day) end++;
                    for (var k = start; k < end; k++)
                    {
                        var e = merged[timed[k]];
                        if (balance.TryGetValue(out var before) && e.Units.TryGetValue(out var units))
                        {
                            var after = before + units;
                            if (!double.IsFinite(after))
                            {
                                throw new DomainException("forecast stock balance overflow");
                            }
                            balance = Known(after);
                            if (after < 0 && units < 0)
                            {
                                findings.Add(new ForecastFinding("stock_shortfall", e.Id, resource, Known(day), Known(-after)));
                            }
                        }
                        else
                        {
                            balance = Unknown<double>();
                        }
                    }
                    start = end;
                }
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
            var c = a.Day.Value.CompareTo(b.Day.Value);
            if (c == 0) c = Sign(a.Units).CompareTo(Sign(b.Units));
            if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
            return c == 0 ? x.CompareTo(y) : c;
        }

        private static int Sign(Fact<double> units) => units.TryGetValue(out var value) && value > 0 ? 1 : 0;
    }
}
