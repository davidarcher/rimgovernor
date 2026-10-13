using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

// MaintainResource-* pure primitives: ingredient deficits, mining progress, resource method, and stock
// selection. The dispatch vertical built on top of these is a single config-only policy.MaintainResource
// goal, whose method is a generic bench/recipe StockTarget production bill exactly like
// GearProduce/MaintainMedicalReserves dispatch through. Native mining-source acquisition
// (SelectResourceSources below), and material-storage zoning remain unwired to any native call.

/// <summary>One native recipe-ingredient alternative's exact required quantity and the deficit against current stock.</summary>
public sealed record ResourceRequirement
{
    public Resource Resource { get; init; }

    public long Required { get; init; }

    public long Deficit { get; init; }
}

/// <summary>Mirrors one designated mineable source's native hit points; a decrease since the prior observation is excavation progress.</summary>
public sealed record MiningProgress
{
    public string ThingId { get; init; } = "";

    public long HitPoints { get; init; }
}

/// <summary>Mirrors one owned native drill's progress fraction; an increase since the prior observation is drilling progress.</summary>
public sealed record DrillingProgress
{
    public string ThingId { get; init; } = "";

    public double Progress { get; init; }
}

/// <summary>
/// Names how one native resource source is acquired. "mine" sources need excavation and are subject to the
/// one-per-selection and safety rules below; any other value (harvest, haul, etc.) is treated as an ordinary,
/// freely combinable source and maps to <see cref="Other"/>.
/// </summary>
public enum ResourceSourceMethod
{
    [WireName("")]
    Other = 0,
    Mine,
}

/// <summary>Mirrors one Observations.ListResourceSources row.</summary>
public sealed record ResourceSource
{
    public string ThingId { get; init; } = "";

    public long Yield { get; init; }

    public double Distance { get; init; }

    public ResourceSourceMethod Method { get; init; }

    public bool Designated { get; init; }

    /// <summary>DesignatedTick and Taken mirror AcquisitionSource's; mine rows only.</summary>
    public Tick DesignatedTick { get; init; }

    public bool Taken { get; init; }

    /// <summary>
    /// Gates a "mine" source only: an older companion cannot certify excavation geometry, so a mine source is
    /// usable only when native reports a MineSafe safety.
    /// </summary>
    public string Safety { get; init; } = "";

    public EquatableArray<WorkType> WorkTypes { get; init; }

    /// <summary>
    /// Cell and Token are populated for a "mine" source only: the exact position and CAS snapshot token
    /// required to dispatch an acquisition Designate against it. Harvest/hunt sources still carry neither;
    /// they are reached only through the AcquisitionFacts census path's own token, not this one.
    /// </summary>
    public Cell Cell { get; init; }

    public string Token { get; init; } = "";

    public Fact<bool> Reachable { get; init; }

    /// <summary>
    /// Marks a supported "mine" deposit no colonist can reach yet: it is never mined directly, only tunnelled
    /// to by a corridor excavation (CorridorExcavationSites).
    /// </summary>
    public bool Buried { get; init; }

    /// <summary>The layout plan's MineTier for a mine source (0 without a plan); lower tiers are selected first.</summary>
    public int Tier { get; init; }
}

/// <summary>Mirrors one Observations.ListResourceSources storage payload (NativeResourceSourcesTool.Storage).</summary>
public sealed record ResourceStorage
{
    public Resource Resource { get; init; }

    public long Capacity { get; init; }

    public long Stored { get; init; }

    public long StackLimit { get; init; }

    /// <summary>The count of eligible haulers native found (only its emptiness is checked).</summary>
    public long Haulers { get; init; }

    public EquatableArray<Cell> Candidates { get; init; }
}

/// <summary>The exact new allow-listed stockpile zone the storage check sizes (SelectStockpileCapacity; no zone is built).</summary>
public sealed record ResourceStorageZone
{
    public EquatableArray<Cell> Cells { get; init; }
}

/// <summary>One configured stock floor still unmet by the census: the resource and its absolute target.</summary>
public sealed record ResourceTarget
{
    public Resource Resource { get; init; }

    public long Target { get; init; }
}

/// <summary>Names the shape of one proposed MaintainResource method.</summary>
public enum ResourceMethodKind
{
    [WireName("")]
    None = 0,
    Unknown,
    Recovered,
    Produce,
    [WireName("wait_for_existing_work")]
    Wait,
    [WireName("no_eligible_method")]
    Blocked,
}

/// <summary>One proposed StockTarget production bill for the resource SelectResourceTarget chose this tick.</summary>
public sealed record ResourceMethod
{
    public ResourceMethodKind Kind { get; init; }

    public MethodId Id { get; init; }

    public string Bench { get; init; } = "";

    public string Recipe { get; init; } = "";

    public string Replace { get; init; } = "";

    public Resource Resource { get; init; }

    public long Target { get; init; }

    /// <summary>
    /// The recipe's ingredients for one craft, the slots with a single alternative (a slot that may be filled
    /// several ways has no known draw).
    /// </summary>
    public EquatableArray<Amount> Ingredients { get; init; }
}

/// <summary>
/// Names the one dynamically-selected resource and target SelectResourceMethod should fund a bill for, plus
/// the same generic bench/recipe census GearProduce/MaintainMedicalReserves already read. No Safeguards or
/// Holds are threaded through yet: the same disclosed no-cross-goal-ingredient-reservation gap applies.
/// </summary>
public sealed record ResourceMethodRequest
{
    public Resource Resource { get; init; }

    public long Target { get; init; }

    public EquatableArray<MethodId> Seen { get; init; }

    public Fact<EquatableArray<GearBench>> Benches { get; init; }

    public EquatableArray<Stock> Stock { get; init; }
}

public static partial class PolicyFunctions
{
    // Native mine-source safety verdicts that permit excavation: open ground, or a roofed deposit whose
    // removal keeps the roof supported.
    public const string MineSafetyOpenSurface = "open_surface";

    public const string MineSafetySupportedRoof = "supported_roof";

    /// <summary>Whether a native safety verdict lets a "mine" source be counted and selected.</summary>
    public static bool MineSafe(string safety) => safety is MineSafetyOpenSurface or MineSafetySupportedRoof;

    /// <summary>
    /// The deposit MaintainResource tunnels to when no mine source can be selected directly: the nearest
    /// undesignated, MineSafe, buried mine row with yield left. Nothing is returned while a known threat or
    /// urgent competing work would hold any mining.
    /// </summary>
    public static (ResourceSource Source, bool Found) BuriedResourceSource(IReadOnlyList<ResourceSource> sources, RemoteWorkRequest r)
    {
        if (RemoteThreatHold(r.Safety).Length != 0 || r.Competition.UrgentPriority > 0)
        {
            return (new ResourceSource(), false);
        }
        var best = new ResourceSource();
        var found = false;
        foreach (var s in sources)
        {
            if (s.Method != ResourceSourceMethod.Mine || !s.Buried || s.Designated || s.Yield <= 0 || !MineSafe(s.Safety))
            {
                continue;
            }
            if (!found || s.Distance < best.Distance || (s.Distance == best.Distance && string.CompareOrdinal(s.ThingId, best.ThingId) < 0))
            {
                best = s;
                found = true;
            }
        }
        return (best, found);
    }

    /// <summary>The open-surface roof check, then the observed route; an unobserved route holds like an unsafe one.</summary>
    internal static string MineRouteHold(ResourceSource s)
    {
        if (!MineSafe(s.Safety) || s.Buried)
        {
            return RemoteHoldRoofSupport;
        }
        if (!s.Reachable.TryGetValue(out var route) || !route)
        {
            return RemoteHoldRouteUnsafe;
        }
        return "";
    }

    /// <summary>
    /// Narrows mining to safe, observed-route deposits. Existing designations reserve estimated yield but are
    /// never adopted or removed. Each new method still contains at most one rock; its final native yield may
    /// overshoot the remaining demand by one rock's output. Every mine source kept back is returned as a hold
    /// with its explicit reason (RemoteHoldReason): a known threat or urgent competing work holds every
    /// deposit, then the open-surface roof check, then the observed route.
    /// </summary>
    public static (IReadOnlyList<ResourceSource> Sources, IReadOnlyList<RemoteWorkHold> Holds) SelectReachableResourceSources(
        IReadOnlyList<ResourceSource> sources, long target, long stock, RemoteWorkRequest r)
    {
        long pending = 0;
        var candidates = new List<ResourceSource>();
        var holds = new List<RemoteWorkHold>();
        foreach (var original in sources)
        {
            var source = original;
            if (source.Designated && source.Yield > 0)
            {
                pending += source.Yield;
                continue;
            }
            if (source.Method == ResourceSourceMethod.Mine)
            {
                var reason = RemoteThreatHold(r.Safety);
                if (reason.Length == 0 && r.Competition.UrgentPriority > 0)
                {
                    reason = RemoteHoldUrgentWork;
                }
                if (reason.Length == 0 && source.Buried)
                {
                    // Native confirmed its support; it waits on a corridor.
                    reason = RemoteHoldBuried;
                }
                if (reason.Length == 0)
                {
                    reason = MineRouteHold(source);
                }
                if (reason.Length != 0)
                {
                    holds.Add(new RemoteWorkHold { Kind = RemoteWorkKind.Mining, Target = source.ThingId, Reason = reason });
                    continue;
                }
                if (r.Plan.TryGetValue(out var plan))
                {
                    source = source with { Tier = plan.MineTier(source.Cell) };
                }
            }
            candidates.Add(source);
        }
        return (SelectResourceSources(candidates, target, stock, pending), holds);
    }

    /// <summary>
    /// Chooses, nearest first, the undesignated sources whose combined yield covers the outstanding deficit
    /// (target minus current stock minus already-pending acquisition). At most one "mine" source is ever
    /// selected per call, and it is the last source selected. A "mine" source lacking native mine safety
    /// confirmation (MineSafe) can never be selected. The result is capped at 8 sources, matching the native
    /// selection this ports.
    /// </summary>
    public static IReadOnlyList<ResourceSource> SelectResourceSources(IReadOnlyList<ResourceSource> sources, long target, long stock, long pending)
    {
        var needed = target - stock - pending;
        if (needed <= 0)
        {
            return [];
        }
        var usable = sources
            .Where(s => !(s.Method == ResourceSourceMethod.Mine && (!MineSafe(s.Safety) || s.Buried)))
            .OrderBy(s => s.Tier)
            .ThenBy(s => s.Distance)
            .ThenBy(s => s.ThingId, StringComparer.Ordinal)
            .ToList();
        var selected = new List<ResourceSource>();
        foreach (var s in usable)
        {
            if (needed <= 0 || selected.Count == 8)
            {
                break;
            }
            if (s.Designated || s.Yield <= 0)
            {
                continue;
            }
            if (s.Method == ResourceSourceMethod.Mine && selected.Count > 0)
            {
                break;
            }
            selected.Add(s);
            needed -= s.Yield;
            if (s.Method == ResourceSourceMethod.Mine)
            {
                break;
            }
        }
        return selected;
    }

    /// <summary>
    /// Decides whether hauling a selection of resource sources (from SelectResourceSources) needs a new
    /// covered stockpile zone. The check only ever applies when at least one selected source is a "mine"
    /// source (an ordinary harvest/hunt yield needs no dedicated storage zone). Blocked is true when there is
    /// no route to funding storage at all this tick (no eligible hauler, or no free candidate cell covers the
    /// shortfall); a caller should treat this as a hard stop. Needed is true only when blocked is false and a
    /// new zone must be built. Throws <see cref="DomainException"/> on an invalid observation.
    /// </summary>
    public static (ResourceStorageZone Zone, bool Needed, bool Blocked) SelectResourceStorageZone(
        IReadOnlyList<ResourceSource> selected, long pending, ResourceStorage storage)
    {
        if (pending < 0 || storage.Capacity < 0 || storage.Stored < 0 || storage.StackLimit < 0 || storage.Haulers < 0)
        {
            throw new DomainException("invalid resource storage observation");
        }
        var hasMine = false;
        long yield = 0;
        foreach (var s in selected)
        {
            if (s.Method == ResourceSourceMethod.Mine)
            {
                hasMine = true;
            }
            yield += s.Yield;
        }
        if (!hasMine)
        {
            return (new ResourceStorageZone(), false, false);
        }
        return SelectStockpileCapacity(yield + pending, storage);
    }

    /// <summary>Uses native covered, reachable storage candidates.</summary>
    public static (ResourceStorageZone Zone, bool Needed, bool Blocked) SelectStockpileCapacity(long capacityNeeded, ResourceStorage storage)
    {
        if (capacityNeeded < 0 || storage.Capacity < 0 || storage.Stored < 0 || storage.StackLimit < 0 || storage.Haulers < 0)
        {
            throw new DomainException("invalid stockpile capacity");
        }
        if (storage.Haulers == 0)
        {
            return (new ResourceStorageZone(), false, true);
        }
        if (storage.Capacity >= capacityNeeded)
        {
            return (new ResourceStorageZone(), false, false);
        }
        if (storage.StackLimit == 0)
        {
            return (new ResourceStorageZone(), false, true);
        }
        var shortfall = capacityNeeded - storage.Capacity;
        var cellsNeeded = (shortfall + storage.StackLimit - 1) / storage.StackLimit;
        if (cellsNeeded > storage.Candidates.Count)
        {
            cellsNeeded = storage.Candidates.Count;
        }
        if (cellsNeeded <= 0)
        {
            return (new ResourceStorageZone(), false, true);
        }
        var cells = storage.Candidates.Take((int)cellsNeeded);
        return (new ResourceStorageZone { Cells = EquatableArray.From(cells) }, true, false);
    }

    /// <summary>
    /// MaintainResource's dynamic-target selection: given every operator-configured resource target and a
    /// fresh native stock census, it picks the single resource whose stock is furthest below its own target
    /// (by proportion, so a small target is not starved behind a large one merely stuck a few units short). A
    /// configured resource absent from the census is treated as fully unstocked rather than refused. Ok is
    /// false when stock is not yet known, no resource is configured, or every configured resource already
    /// meets its target. Throws <see cref="DomainException"/> on invalid input.
    /// </summary>
    public static (Resource Resource, long Target, bool Ok) SelectResourceTarget(
        IReadOnlyDictionary<Resource, long> targets, Fact<EquatableArray<Amount>> stock)
    {
        var ranked = RankResourceTargets(targets, stock);
        if (ranked.Count == 0)
        {
            return (default, 0, false);
        }
        return (ranked[0].Resource, ranked[0].Target, true);
    }

    /// <summary>
    /// SelectResourceTarget's full order: every unmet configured floor, worst-covered first (proportional
    /// deficit descending, definition name ascending on a tie), so a caller whose first choice has no
    /// dispatchable method can go on to the next demanded resource in the same step. Empty when stock is
    /// unknown, nothing is configured, or every floor is met.
    /// </summary>
    public static IReadOnlyList<ResourceTarget> RankResourceTargets(
        IReadOnlyDictionary<Resource, long> targets, Fact<EquatableArray<Amount>> stock)
    {
        if (targets.Count == 0)
        {
            return [];
        }
        var names = new List<Resource>(targets.Count);
        foreach (var (name, want) in targets)
        {
            if (!ValidResource(name) || want <= 0)
            {
                throw new DomainException("invalid resource target");
            }
            names.Add(name);
        }
        names.Sort();
        if (!stock.TryGetValue(out var rows))
        {
            return [];
        }
        var have = new Dictionary<Resource, long>();
        foreach (var q in rows)
        {
            if (!ValidResource(q.Resource) || q.Count < 0)
            {
                throw new DomainException("invalid resource stock");
            }
            if (!have.TryAdd(q.Resource, q.Count))
            {
                throw new DomainException("duplicate resource stock");
            }
        }
        var unmet = new List<(ResourceTarget Row, double Ratio)>();
        foreach (var name in names)
        {
            var want = targets[name];
            var deficit = want - have.GetValueOrDefault(name);
            if (deficit <= 0)
            {
                continue;
            }
            unmet.Add((new ResourceTarget { Resource = name, Target = want }, (double)deficit / want));
        }
        return unmet.OrderByDescending(u => u.Ratio).Select(u => u.Row).ToList();
    }

    internal static MethodId ResourceMethodId(Resource resource, string bench, string recipe)
    {
        // Go hashed fmt.Sprintf("%v", struct{Resource; Bench, Recipe}); the same text keeps ids stable.
        var text = $"{{{resource} {bench} {recipe}}}";
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return new MethodId("resource-produce-" + Convert.ToHexStringLower(sum.AsSpan(0, 16)));
    }

    /// <summary>
    /// Proposes one repeat-count StockTarget bill that keeps at least Target units of Resource in stock,
    /// mirroring SelectMedicineMethod exactly but over whichever resource SelectResourceTarget dynamically
    /// chose rather than one hardcoded definition. It covers only the bench/recipe production path of the
    /// resource method; a resource with no producing recipe and no covering bill is simply Blocked. It
    /// issues no game orders and does not reserve resources. Throws <see cref="DomainException"/> on an invalid history.
    /// </summary>
    public static ResourceMethod SelectResourceMethod(ResourceMethodRequest r)
    {
        if (!ValidResource(r.Resource) || r.Target <= 0 || r.Target > int.MaxValue)
        {
            return new ResourceMethod { Kind = ResourceMethodKind.Unknown };
        }
        var seen = new HashSet<MethodId>();
        foreach (var id in r.Seen)
        {
            if (!FoodId(id.Value) || !seen.Add(id))
            {
                throw new DomainException("invalid resource method history");
            }
        }
        if (!r.Benches.TryGetValue(out var knownBenches))
        {
            return new ResourceMethod { Kind = ResourceMethodKind.Unknown };
        }
        ValidateGearProduction(knownBenches);
        var benches = knownBenches.OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
        foreach (var b in benches)
        {
            if (!b.Bills.TryGetValue(out var bills))
            {
                return new ResourceMethod { Kind = ResourceMethodKind.Unknown };
            }
            foreach (var bill in bills)
            {
                if (!ContainsResource(bill.Products, r.Resource))
                {
                    continue;
                }
                if (!bill.Active.TryGetValue(out var active))
                {
                    return new ResourceMethod { Kind = ResourceMethodKind.Unknown };
                }
                if (active)
                {
                    return new ResourceMethod { Kind = ResourceMethodKind.Wait };
                }
            }
        }
        foreach (var b in benches)
        {
            if (!b.Recipes.TryGetValue(out var knownRecipes))
            {
                return new ResourceMethod { Kind = ResourceMethodKind.Unknown };
            }
            var recipes = knownRecipes.OrderBy(x => x.Definition, StringComparer.Ordinal).ToList();
            foreach (var recipe in recipes)
            {
                if (!ContainsResource(recipe.Products, r.Resource))
                {
                    continue;
                }
                var ak = recipe.Available.TryGetValue(out var available);
                var ok = recipe.AvailableOn.TryGetValue(out var on);
                if ((ak && !available) || (ok && !on))
                {
                    continue;
                }
                if (!ak || !ok)
                {
                    return new ResourceMethod { Kind = ResourceMethodKind.Unknown };
                }
                var id = ResourceMethodId(r.Resource, b.Id, recipe.Definition);
                if (seen.Contains(id))
                {
                    return new ResourceMethod { Kind = ResourceMethodKind.Wait, Id = id };
                }
                // Reuse the native replacement operation for an inactive matching bill; adding beside it is
                // refused by the native duplicate guard.
                var replace = "";
                foreach (var bill in b.Bills.ValueOrDefault)
                {
                    if (bill.Recipe != recipe.Definition)
                    {
                        continue;
                    }
                    if (!bill.Active.TryGetValue(out var active) || !FoodId(bill.Id))
                    {
                        return new ResourceMethod { Kind = ResourceMethodKind.Unknown };
                    }
                    if (!active)
                    {
                        replace = bill.Id;
                        break;
                    }
                }
                return new ResourceMethod
                {
                    Kind = ResourceMethodKind.Produce,
                    Id = id,
                    Bench = b.Id,
                    Recipe = recipe.Definition,
                    Replace = replace,
                    Resource = r.Resource,
                    Target = r.Target,
                    Ingredients = EquatableArray.From(RecipeDraws(recipe)),
                };
            }
        }
        return new ResourceMethod { Kind = ResourceMethodKind.Blocked };
    }

    /// <summary>What one craft of the recipe consumes from the catalog: each slot with a single alternative. Unknown ingredients declare nothing.</summary>
    internal static IReadOnlyList<Amount> RecipeDraws(GearRecipe recipe)
    {
        if (!recipe.Ingredients.TryGetValue(out var slots))
        {
            return [];
        }
        var output = new List<Amount>();
        foreach (var slot in slots)
        {
            if (slot.Count == 1 && slot[0].Count > 0)
            {
                output.Add(slot[0]);
            }
        }
        return output;
    }
}
