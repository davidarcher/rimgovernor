using System.Security.Cryptography;
using System.Text;
using BuildingRow = RimGovernor.Brain.GameFacts.Observations.BuildingState;

namespace RimGovernor.Brain.Policy;

// Power restoration (task 5.3 of redefine-habitat-requirements): a producer an empty tank or a breakdown took off a network whose
// consumers lack power is compared through the maintenance choice (ServicingChoiceRules) by its breakdown fix, its refuel and,
// when the colony holds none of its fuel, the chain that acquires the fuel and then refuels. A producer whose function the choice
// restores counts toward the network's capacity in SelectPowerMethod; one it cannot restore does not, and new generation is
// then priced as before. Everything here is re-derived each review from fresh facts and the journal (design Decision 4).

/// <summary>
/// What the restoration choice reads: the power topology, the servicing census of the same read (null when unread, and every
/// fix and refuel is then unassessed), the servicing underway and refused (the repairs Need's fixes and the power Need's refuels,
/// from the journal and the pawns' running jobs), the observed harvest sources and the sources open acquisition actions hold.
/// </summary>
public sealed record PowerRestorationRequest
{
    public PowerTopology Topology { get; init; } = new();
    public ServicingCensus? Census { get; init; }
    public IReadOnlyList<ServicingUnderway> Underway { get; init; } = [];
    public IReadOnlyList<RefusedServicing> Refused { get; init; } = [];
    public EquatableArray<AcquisitionSource> Sources { get; init; }
    public IReadOnlyDictionary<string, bool> Held { get; init; } = new Dictionary<string, bool>(StringComparer.Ordinal);
}

/// <summary>The decided restoration of each lost producer, most rated watts first.</summary>
public sealed record PowerRestoration(EquatableArray<ServicingDecision> Decisions)
{
    /// <summary>The producers whose function the decisions restore (<see cref="ServicingChoiceRules.Restores"/>).</summary>
    public IReadOnlySet<string> Restorable => Decisions.Where(ServicingChoiceRules.Restores).Select(d => d.Target).ToHashSet(StringComparer.Ordinal);
}

public static class PowerRestorationRules
{
    /// <summary>The MethodId prefixes of a refuel (<c>refuel-&lt;producer&gt;-&lt;pawn&gt;</c>) and a fuel acquisition (<c>fuel-&lt;def&gt;-&lt;sources&gt;</c>).</summary>
    public const string RefuelMethod = "refuel-", FuelMethod = "fuel-";

    /// <summary>The deficits restoration answers: a damaged producer still produces, so its condition stays home upkeep's.</summary>
    public static readonly IReadOnlySet<ServicingDeficit> Considered = new HashSet<ServicingDeficit> { ServicingDeficit.Breakdown, ServicingDeficit.Fuel };

    /// <summary>
    /// The connected, switched-on, unforbidden producers that are out of fuel or broken down on a network one of whose switched-on,
    /// unforbidden consumers is unpowered or whose output is negative; most rated watts first, then id.
    /// </summary>
    public static IReadOnlyList<BuildingRow> LostProducers(PowerTopology v)
    {
        var deficit = new HashSet<string>(StringComparer.Ordinal);
        foreach (var consumer in v.Buildings)
        {
            if (consumer.BaseW >= 0 || !consumer.Connected || consumer.Forbidden || !consumer.SwitchedOn) continue;
            var output = v.Buildings.Where(b => b.Connected && b.Network == consumer.Network).Sum(b => b.OutputW);
            if (!consumer.Powered || output < 0) deficit.Add(consumer.Network);
        }
        return [.. v.Buildings
            .Where(b => b.BaseW > 0 && b.Connected && deficit.Contains(b.Network) && !b.Forbidden && b.SwitchedOn && (b.Unfueled || b.BrokenDown))
            .OrderByDescending(b => b.BaseW).ThenBy(b => b.Id, StringComparer.Ordinal)];
    }

    /// <summary>Each lost producer's restoration, compared by the maintenance choice over its breakdown and fuel deficits.</summary>
    public static PowerRestoration Decide(PowerRestorationRequest r)
    {
        var lost = LostProducers(r.Topology);
        if (lost.Count == 0) return new([]);
        return new([.. ServicingChoiceRules.Decide(new ServicingRequest
        {
            Considered = Considered,
            Acquisitions = Acquisitions(lost, r.Census, r.Sources, r.Held),
            Targets = lost,
            Census = r.Census,
            Unneeded = new HashSet<string>(StringComparer.Ordinal),
            Underway = r.Underway,
            Refused = r.Refused,
        })]);
    }

    /// <summary>
    /// The acquisition of each fuel the empty producers burn that the stock does not cover: the items to fill them (their target
    /// fuel less their fuel, at the row's fuel per item) less the census stock. A fuel no observed non-hunt source yields is the
    /// named state <c>fuel acquisition:&lt;def&gt;</c>; otherwise the path designates the sources
    /// <see cref="AcquisitionRules.SelectResourceAcquisition"/> picks over the yield already designated.
    /// </summary>
    public static IReadOnlyDictionary<string, ServicingAcquisition> Acquisitions(IReadOnlyList<BuildingRow> lost, ServicingCensus? census,
        EquatableArray<AcquisitionSource> sources, IReadOnlyDictionary<string, bool> held)
    {
        var deficits = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var unread = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var producer in lost)
        {
            if (producer.Service.Fuel is not RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { OutOfFuel: true } tank) continue;
            foreach (var def in producer.FuelDefinitions)
            {
                if (tank.Burn.UnitsPerItem <= 0)
                {
                    unread[def] = "fuel per item:" + producer.Definition;
                    continue;
                }
                deficits[def] = deficits.GetValueOrDefault(def) + Math.Ceiling((tank.TargetFuel - tank.Fuel) / tank.Burn.UnitsPerItem);
            }
        }
        var output = new Dictionary<string, ServicingAcquisition>(StringComparer.Ordinal);
        foreach (var (def, state) in unread) output[def] = new ServicingAcquisition.None(state);
        foreach (var (def, need) in deficits)
        {
            var stocked = census?.Stock.GetValueOrDefault(def) ?? 0;
            var deficit = need - stocked;
            if (deficit <= 0) continue;
            var resource = new Resource(def);
            var yielding = sources.Where(s => !s.Hunt && new Resource(s.Resource) == resource).ToArray();
            if (yielding.Length == 0)
            {
                output[def] = new ServicingAcquisition.None(ServicingChoiceRules.FuelAcquisition + def);
                continue;
            }
            var pending = yielding.Where(s => s.Designated).Sum(s => s.Yield);
            output[def] = new ServicingAcquisition.Path(resource, deficit, pending,
                [.. AcquisitionRules.SelectResourceAcquisition(sources, deficit, pending, resource, held)]);
        }
        return output;
    }

    /// <summary>
    /// The power Need's restoration Methods: each chosen refuel as an exact RecoveryService refuel claiming its producer and pawn
    /// (an issued one continues its open Method; a running native refuel is observed, not reordered), and one acquisition per
    /// chain fuel that has sources to designate, claiming each source. A chosen breakdown fix is the home-upkeep repairs Need's
    /// Method (one owner): it only counts toward the restorable capacity here.
    /// </summary>
    public static IReadOnlyList<NeedMethod> Methods(NeedId need, PowerRestoration restoration)
    {
        var output = new List<NeedMethod>();
        var chains = new SortedDictionary<string, (ServicingAcquisition.Path Path, List<string> Targets)>(StringComparer.Ordinal);
        foreach (var d in restoration.Decisions)
        {
            if (d.Underway is { Issued: false }) continue;
            if (d.Chosen.Kind == ServicingChoiceKind.Refuel)
            {
                var pawn = new PawnId(d.Chosen.Pawn);
                var target = d.Target;
                output.Add(new NeedMethod(need, new MethodId(RefuelMethod + target + "-" + d.Chosen.Pawn), ServicingChoiceRules.Describe(d),
                    id => new PlanSpec(id, new PlanRevision(1), [DomainAction.NewRecoveryServiceAction(new ActionId(id.Value + "-0"), new RecoveryService(pawn, target, RecoveryMethod.Refuel))])));
            }
            else if (d.Chosen.Chain is { Sources.Count: > 0 } chain)
            {
                if (!chains.TryGetValue(chain.Resource.Value, out var entry)) chains[chain.Resource.Value] = entry = (chain, []);
                entry.Targets.Add(d.Target);
            }
        }
        foreach (var (def, (path, targets)) in chains)
        {
            var sources = path.Sources;
            output.Add(new NeedMethod(need, FuelMethodId(path), string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"acquire {def} for {string.Join(", ", targets)}: {path.Deficit:0.##} needed, {path.Pending:0.##} designated, {sources.Count} sources; then refuel"),
                id => new PlanSpec(id, new PlanRevision(1), [.. sources.Select((row, i) => DomainAction.NewAcquisitionAction(new ActionId($"{id.Value}-{i}"), new Acquisition(row.Id, row.Resource, row.Cell)))])));
        }
        return output;
    }

    /// <summary>A fuel acquisition's MethodId: its def and a digest of the sources it designates, so the same selection continues its Method.</summary>
    public static MethodId FuelMethodId(ServicingAcquisition.Path path)
    {
        var text = new StringBuilder();
        foreach (var row in path.Sources) text.Append($"{row.Id}/{row.Resource}/{row.Token}/{row.Cell.X}/{row.Cell.Z}\n");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return new MethodId(FuelMethod + path.Resource.Value + "-" + Convert.ToHexStringLower(digest.AsSpan(0, 8)));
    }
}
