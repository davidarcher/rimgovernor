namespace RimGovernor.Brain.Policy;

/// <summary>
/// The one resource-demand value a review derives from its facts. The detectors read it during the review and
/// the planners read the same value afterwards (architecture rule 7).
/// </summary>
public sealed record DerivedDemand
{
    /// <summary>Activity inputs from the same review, consumed alongside these resource targets.</summary>
    public ForecastWorkInputs Work { get; init; } = new();

    /// <summary>Free material after construction and reconciled production/native promises; absent keys mean zero.</summary>
    public Fact<IReadOnlyDictionary<Resource, Fact<double>>> MaterialAvailability { get; init; }
    /// <summary>Every stock level some source asks for, per resource: the targets the economy/resource Need and the trade stock against.</summary>
    public IReadOnlyDictionary<Resource, long> Needs { get; init; } = new Dictionary<Resource, long>();

    /// <summary>
    /// The demand of the colony's own works alone (construction, open bills, clothing, fuel, animal feed),
    /// without the evidence needs and the runway targets: stock a chop or a mine provides.
    /// </summary>
    public IReadOnlyDictionary<Resource, long> Works { get; init; } = new Dictionary<Resource, long>();

    /// <summary>The stuffs the clothing runway demands (ClothingRunway): a hunt serves them, at their clothing horizon.</summary>
    public IReadOnlySet<Resource> Clothing { get; init; } = new HashSet<Resource>();

    /// <summary>
    /// The stock a sale keeps of each resource (TradeRetained): the runways' protected lines plus Works.
    /// Unknown while the consumption ring is unread or a production hold is.
    /// </summary>
    public Fact<IReadOnlyDictionary<Resource, long>> Retained { get; init; }
}

public static partial class ResourceRules
{
    /// <summary>
    /// Derives the review's resource demand from its facts, in one merge: the evidence needs other goals
    /// recorded (f.ResourceNeeds), then construction , open bills, clothing,
    /// fuel, animal feed and the runways' targets, each raising a level and never lowering one. A new demand
    /// source is one entry here.
    /// </summary>
    public static DerivedDemand ResourceDemandOf(RoundsFacts f, RoundsPolicy p, RoundsLatches l)
    {
        var clothing = f.ClothingRunway();
        var works = ConstructionDemandRules.ConstructionDemandOf(f, p, l);
        var holds = ProductionRules.ProductionMaterialHoldsOf(f.ProductionPromises, f.BillReservations);
        var material = ProductionRules.SharedMaterialBudgetOf(f.Resources, f.CurrentConstruction.Deficit, holds);
        if (holds.TryGetValue(out var held))
        {
            works = new Dictionary<Resource, long>(works);
            var admitted = ConstructionDemandRules.AdmittedNeeds(f.Admitted);
            foreach (var (resource, amount) in held)
                if (amount.TryGetValue(out var units))
                {
                    // ConstructionDemand drops already funded lines; two individually funded promises can still compete.
                    var construction = f.CurrentConstruction.Deficit.GetValueOrDefault(resource);
                    var floor = Math.Max(works.GetValueOrDefault(resource), Math.Max(construction, admitted.GetValueOrDefault(resource)));
                    works[resource] = checked(floor + (long)Math.Ceiling(units));
                }
        }
        IReadOnlyDictionary<Resource, long>[] sources =
        [
            BillDemandRules.OpenBillDemand(f.OpenBills, new StockReader { Resources = f.Resources, Wood = f.Wood }),
            clothing.Needs,
            f.FuelRunway().Needs,
            f.AnimalFeedRunway().Needs,
        ];
        foreach (var source in sources)
        {
            works = new Dictionary<Resource, long>(ResourceConcernTargets(works, source));
        }
        var needs = ResourceConcernTargets(ResourceConcernTargets(f.ResourceNeeds, works), ResourceRunwayTargets(f.ResourceRunways));
        if (f.Preferences.Forbids(ForbiddenMethod.Drugs))
        {
            // A colony without recreational drugs stocks and brews none: their open bills and runways ask for nothing.
            var recreation = f.Items.RecreationDrugs().Select(d => d.Def).ToHashSet();
            needs = needs.Where(kv => !recreation.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            works = works.Where(kv => !recreation.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        return new DerivedDemand
        {
            Work = ForecastWorkInputs.ForecastWorkInputsOf(f),
            MaterialAvailability = material,
            Needs = needs,
            Works = works,
            Clothing = clothing.Needs.Keys.ToHashSet(),
            Retained = holds.TryGetValue(out var knownHolds) && knownHolds.Values.All(a => a.IsKnown) && f.ResourceConsumption is not null
                ? TradePolicyRules.TradeRetained(f.ResourceRunways, works) : default,
        };
    }

    /// <summary>
    /// Merges two stock-floor maps by maximum: derived raises a level of base, never lowers it, and a
    /// non-positive or invalid resource is left out. It returns base itself when derived is empty.
    /// </summary>
    public static IReadOnlyDictionary<Resource, long> ResourceConcernTargets(
        IReadOnlyDictionary<Resource, long> @base, IReadOnlyDictionary<Resource, long> derived)
    {
        if (derived.Count == 0)
        {
            return @base;
        }
        var output = new Dictionary<Resource, long>(@base.Count + derived.Count);
        foreach (var (r, v) in @base)
        {
            output[r] = v;
        }
        foreach (var (r, v) in derived)
        {
            if (v > 0 && v > output.GetValueOrDefault(r) && AdmissionRules.ValidResource(r))
            {
                output[r] = v;
            }
        }
        return output;
    }
}
