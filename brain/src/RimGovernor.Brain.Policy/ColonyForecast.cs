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
}

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

/// <summary>Negative balance denotes unmet units, not spendable inventory.</summary>
public sealed record ForecastStockPoint(double Day, Fact<double> BalanceUnits);
public sealed record ForecastStockTrajectory(Resource Resource, EquatableArray<ForecastStockPoint> Points);
public sealed record ForecastFinding(string Reason, string Commitment, Resource Resource, Fact<double> Day, Fact<double> Units);

public sealed record ColonyForecastScenario
{
    public ForecastCoupledScenario? Coupled { get; init; }
    /// <summary>Free for new declared claims, not an inventory trajectory or native reservation.</summary>
    public Fact<IReadOnlyDictionary<Resource, Fact<double>>> MaterialAvailability { get; init; }
    public EquatableArray<ForecastEquipmentCapacity> EquipmentAvailability { get; init; }
    public EquatableArray<ForecastCommitment> Commitments { get; init; }
    public EquatableArray<ForecastStockTrajectory> Stocks { get; init; }
    public EquatableArray<ForecastFinding> Findings { get; init; }
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

    internal static ColonyForecastScenario ForecastInventory(Fact<EquatableArray<Amount>> observed,
        IReadOnlyDictionary<string, ForecastCommitment> commitments, double horizon)
    {
        var known = observed.TryGetValue(out var rows);
        var stocks = new Dictionary<Resource, double>();
        foreach (var row in rows)
        {
            if (!AdmissionRules.ValidResource(row.Resource) || row.Count < 0)
            {
                throw new DomainException("invalid forecast observed stock");
            }
            stocks[row.Resource] = stocks.GetValueOrDefault(row.Resource) + row.Count;
        }
        var ordered = commitments.Values.ToArray();
        Array.Sort(ordered, static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        var effects = new List<(string Id, ForecastStockChange Change)>();
        var resources = new HashSet<Resource>(stocks.Keys);
        foreach (var commitment in ordered)
            foreach (var change in commitment.StockChanges) { effects.Add((commitment.Id, change)); resources.Add(change.Resource); }
        var trajectories = new List<ForecastStockTrajectory>();
        var findings = new List<ForecastFinding>();
        if (!known)
        {
            findings.Add(new ForecastFinding("unknown_initial_stock", "", default, Known(0.0), Unknown<double>()));
        }
        foreach (var commitment in ordered)
        {
            foreach (var effect in commitment.UnsupportedEffects)
            {
                findings.Add(new ForecastFinding("unsupported:" + effect, commitment.Id, default, Unknown<double>(), Unknown<double>()));
            }
        }
        var sorted = resources.ToArray();
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        var timed = new List<int>();
        foreach (var resource in sorted)
        {
            var balance = known ? Known(stocks.GetValueOrDefault(resource)) : Unknown<double>();
            var points = new List<ForecastStockPoint> { new(0, balance) };
            timed.Clear();
            for (var i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                if (e.Change.Resource != resource) continue;
                if (!e.Change.Day.TryGetValue(out var day))
                {
                    // Unknown time may affect any future point, but never rewrites the observed stock.
                    balance = Unknown<double>();
                    findings.Add(new ForecastFinding("unknown_stock_effect", e.Id, resource, e.Change.Day, e.Change.Units));
                    continue;
                }
                if (!e.Change.Units.IsKnown)
                {
                    findings.Add(new ForecastFinding("unknown_stock_effect", e.Id, resource, e.Change.Day, e.Change.Units));
                }
                else if (day > horizon)
                {
                    findings.Add(new ForecastFinding("beyond_horizon", e.Id, resource, e.Change.Day, e.Change.Units));
                }
                if (day <= horizon) timed.Add(i);
            }
            // By day; same-time inputs precede outputs, a batch cannot finance its own prerequisites; then by commitment.
            static int Sign(ForecastStockChange change) => change.Units.TryGetValue(out var units) && units > 0 ? 1 : 0;
            timed.Sort((x, y) =>
            {
                var (a, b) = (effects[x], effects[y]);
                var c = a.Change.Day.Value.CompareTo(b.Change.Day.Value);
                if (c == 0) c = Sign(a.Change).CompareTo(Sign(b.Change));
                if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
                return c == 0 ? x.CompareTo(y) : c;
            });
            for (var start = 0; start < timed.Count;)
            {
                var day = effects[timed[start]].Change.Day.Value;
                var end = start;
                while (end < timed.Count && effects[timed[end]].Change.Day.Value == day) end++;
                for (var k = start; k < end; k++)
                {
                    var e = effects[timed[k]];
                    if (balance.TryGetValue(out var before) && e.Change.Units.TryGetValue(out var units))
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
                points.Add(new ForecastStockPoint(day, balance));
                start = end;
            }
            if (points[^1].Day < horizon)
            {
                points.Add(new ForecastStockPoint(horizon, balance));
            }
            trajectories.Add(new ForecastStockTrajectory(resource, EquatableArray.From(points)));
        }
        return new ColonyForecastScenario
        {
            Commitments = EquatableArray.From(ordered),
            Stocks = EquatableArray.From(trajectories),
            Findings = EquatableArray.From(findings),
        };
    }
}
