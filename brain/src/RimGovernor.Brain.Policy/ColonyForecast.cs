namespace RimGovernor.Brain.Policy;

/// <summary>Caller-supplied snapshot identity; evaluating never reads live world state.</summary>
public sealed record ColonyForecastOrigin(string World, long ObservedTick);

/// <summary>Positive units enter inventory; negative units consume it. Day is relative to the observed tick.</summary>
public sealed record ForecastStockChange(Resource Resource, Fact<double> Day, Fact<double> Units);

/// <summary>Supplied Method effects, not a promise of native completion.</summary>
public sealed record ForecastCommitment(string Id, EquatableArray<ForecastStockChange> StockChanges)
{
    public EquatableArray<string> UnsupportedEffects { get; init; }
    /// <summary>Observed standing construction owned by this Method; no second supplied stock effect.</summary>
    public Building? Construction { get; init; }
    public Fact<double> AlternativeLaborCost { get; init; }
}

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
    public EquatableArray<string> Assumptions { get; init; }
    public ForecastConstructionContext Construction { get; init; } = new();
}

/// <summary>Negative balance denotes unmet units, not spendable inventory.</summary>
public sealed record ForecastStockPoint(double Day, Fact<double> BalanceUnits);
public sealed record ForecastStockTrajectory(Resource Resource, EquatableArray<ForecastStockPoint> Points);
public sealed record ForecastFinding(string Reason, string Commitment, Resource Resource, Fact<double> Day, Fact<double> Units);

public sealed record ColonyForecastScenario
{
    public EquatableArray<ForecastCommitment> Commitments { get; init; }
    public EquatableArray<ForecastStockTrajectory> Stocks { get; init; }
    public EquatableArray<ForecastFinding> Findings { get; init; }
    public EquatableArray<ForecastConstructionResult> Construction { get; init; }
}

/// <summary>Baseline/delta consequences and observed diagnostics, not native feasibility or a utility score.</summary>
public sealed record ColonyForecast
{
    public DerivedDemand Demand { get; init; } = new();
    public required ColonyForecastOrigin Origin { get; init; }
    public double HorizonDays { get; init; }
    public EquatableArray<string> Assumptions { get; init; }
    public required ColonyDiagnostics Observed { get; init; }
    public required ColonyForecastScenario Baseline { get; init; }
    public required ColonyForecastScenario Proposed { get; init; }
    private static readonly EquatableArray<string> UnsupportedDomains = EquatableArray.From(new[]
    {
        "pawn_capacity", "needs_and_mood", "activity_dependencies", "infrastructure_effects", "combat_outcomes",
    });
    public EquatableArray<string> Unsupported { get; init; } = UnsupportedDomains;
}

public static partial class PolicyFunctions
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
            Demand = request.Demand,
            Origin = request.Origin,
            HorizonDays = request.HorizonDays,
            Assumptions = request.Assumptions,
            Observed = DiagnoseColony(request.Facts),
            Baseline = ForecastConstructionScenario(request, baseline),
            Proposed = ForecastConstructionScenario(request, proposed),
        };
    }

    private static Dictionary<string, ForecastCommitment> ForecastCommitmentsById(IEnumerable<ForecastCommitment> commitments)
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
                if (!ValidResource(change.Resource)
                    || (change.Day.TryGetValue(out var day) && (!double.IsFinite(day) || day < 0))
                    || (change.Units.TryGetValue(out var units) && !double.IsFinite(units)))
                {
                    throw new DomainException("invalid forecast stock change");
                }
            }
        }
        return result;
    }

    private static ColonyForecastScenario ForecastInventory(Fact<EquatableArray<Amount>> observed,
        IReadOnlyDictionary<string, ForecastCommitment> commitments, double horizon)
    {
        var known = observed.TryGetValue(out var rows);
        var stocks = new Dictionary<Resource, double>();
        foreach (var row in rows)
        {
            if (!ValidResource(row.Resource) || row.Count < 0)
            {
                throw new DomainException("invalid forecast observed stock");
            }
            stocks[row.Resource] = stocks.GetValueOrDefault(row.Resource) + row.Count;
        }
        var ordered = commitments.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var effects = ordered.SelectMany(c => c.StockChanges.Select(change => (c.Id, Change: change))).ToArray();
        var resources = stocks.Keys.Concat(effects.Select(e => e.Change.Resource)).Distinct().OrderBy(r => r.Value, StringComparer.Ordinal);
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
        foreach (var resource in resources)
        {
            var relevant = effects.Where(e => e.Change.Resource == resource).ToArray();
            var balance = known ? Known(stocks.GetValueOrDefault(resource)) : Unknown<double>();
            var points = new List<ForecastStockPoint> { new(0, balance) };
            // Unknown time may affect any future point, but never rewrites the observed stock.
            if (relevant.Any(e => !e.Change.Day.IsKnown))
            {
                balance = Unknown<double>();
            }
            foreach (var e in relevant)
            {
                if (!e.Change.Day.TryGetValue(out var day) || !e.Change.Units.IsKnown)
                {
                    findings.Add(new ForecastFinding("unknown_stock_effect", e.Id, resource, e.Change.Day, e.Change.Units));
                }
                else if (day > horizon)
                {
                    findings.Add(new ForecastFinding("beyond_horizon", e.Id, resource, e.Change.Day, e.Change.Units));
                }
            }
            var timed = relevant.Where(e => e.Change.Day.TryGetValue(out var day) && day <= horizon)
                .GroupBy(e => e.Change.Day.Value).OrderBy(group => group.Key);
            foreach (var group in timed)
            {
                // Same-time inputs precede outputs; a batch cannot finance its own prerequisites.
                foreach (var e in group.OrderBy(e => e.Change.Units.TryGetValue(out var units) && units > 0 ? 1 : 0)
                    .ThenBy(e => e.Id, StringComparer.Ordinal))
                {
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
                            findings.Add(new ForecastFinding("stock_shortfall", e.Id, resource, Known(group.Key), Known(-after)));
                        }
                    }
                    else
                    {
                        balance = Unknown<double>();
                    }
                }
                points.Add(new ForecastStockPoint(group.Key, balance));
            }
            if (points[^1].Day < horizon)
            {
                points.Add(new ForecastStockPoint(horizon, balance));
            }
            trajectories.Add(new ForecastStockTrajectory(resource, EquatableArray.From(points)));
        }
        return new ColonyForecastScenario
        {
            Commitments = EquatableArray.From(ordered), Stocks = EquatableArray.From(trajectories), Findings = EquatableArray.From(findings),
        };
    }
}
