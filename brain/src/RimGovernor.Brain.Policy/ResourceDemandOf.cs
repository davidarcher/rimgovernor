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
    /// <summary>Every stock level some source asks for, per resource: the targets MaintainResource and the trade stock against.</summary>
    public IReadOnlyDictionary<Resource, long> Needs { get; init; } = new Dictionary<Resource, long>();

    /// <summary>
    /// The demand of the colony's own works alone (construction, open bills, clothing, fuel, animal feed),
    /// without the evidence needs and the runway targets: stock a chop or a mine provides.
    /// </summary>
    public IReadOnlyDictionary<Resource, long> Works { get; init; } = new Dictionary<Resource, long>();

    /// <summary>Maps the stuffs that may serve a clothing demand to the stuff it names (ClothingRunway).</summary>
    public IReadOnlyDictionary<Resource, Resource> Serves { get; init; } = new Dictionary<Resource, Resource>();

    /// <summary>
    /// The stock a sale keeps of each resource (TradeRetained): the runways' protected lines plus Works.
    /// Unknown while the consumption is.
    /// </summary>
    public Fact<IReadOnlyDictionary<Resource, long>> Retained { get; init; }
}

public static partial class PolicyFunctions
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
        var works = ConstructionDemandOf(f, p, l);
        var holds = ProductionMaterialHoldsOf(f.ProductionPromises, f.BillReservations);
        var material = SharedMaterialBudgetOf(f.Resources, f.ConstructionDeficit, holds);
        if (holds.TryGetValue(out var held))
        {
            works = new Dictionary<Resource, long>(works);
            var admitted = AdmittedNeeds(f.Admitted);
            foreach (var (resource, amount) in held)
                if (amount.TryGetValue(out var units))
                {
                    // ConstructionDemand drops already funded lines; two individually funded promises can still compete.
                    var construction = f.ConstructionDeficit.TryGetValue(out var owed) ? owed.GetValueOrDefault(resource) : 0;
                    var floor = Math.Max(works.GetValueOrDefault(resource), Math.Max(construction, admitted.GetValueOrDefault(resource)));
                    works[resource] = checked(floor + (long)Math.Ceiling(units));
                }
        }
        IReadOnlyDictionary<Resource, long>[] sources =
        [
            OpenBillDemand(f.OpenBills, new StockReader { Resources = f.Resources, Wood = f.Wood }),
            clothing.Needs,
            f.FuelRunway().Needs,
            f.AnimalFeedRunway().Needs,
        ];
        foreach (var source in sources)
        {
            works = new Dictionary<Resource, long>(ResourceConcernTargets(works, source));
        }
        var needs = ResourceConcernTargets(ResourceConcernTargets(f.ResourceNeeds, works), ResourceRunwayTargets(f.ResourceRunways));
        return new DerivedDemand
        {
            Work = ForecastWorkInputsOf(f),
            MaterialAvailability = material,
            Needs = needs,
            Works = works,
            Serves = clothing.Serves,
            Retained = holds.TryGetValue(out var knownHolds) && knownHolds.Values.All(a => a.IsKnown)
                ? TradeRetained(f.ResourceConsumption, f.ResourceRunways, works) : default,
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
            if (v > 0 && v > output.GetValueOrDefault(r) && ValidResource(r))
            {
                output[r] = v;
            }
        }
        return output;
    }
}
