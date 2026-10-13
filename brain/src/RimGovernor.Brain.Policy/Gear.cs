using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A loose or stored garment to wear. Candidates have already passed native outfit, forced/locked gear,
/// player-job, ownership, reachability and resource-policy checks. Proposals still require a fresh native
/// admission against the exact inspected loadout before dispatch.
/// </summary>
public sealed record GearCandidate(string Target, double Gain, Resource Definition);

public sealed record GearReplacement
{
    public Resource Definition { get; init; }
    public Resource Stuff { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>One worn garment as the native loadout reports it: Condition is its hit-point fraction (1 for a definition without hit points) and Groups the body-part groups it covers.</summary>
public sealed record GearApparel
{
    public Resource Definition { get; init; }
    public double Condition { get; init; }
    public EquatableArray<string> Groups { get; init; }

    internal bool Valid()
    {
        if (!PolicyFunctions.ValidResource(Definition) || !PolicyFunctions.FoodNumber(Condition) || Condition > 1)
        {
            return false;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in Groups)
        {
            if (!PolicyFunctions.FoodId(g) || !seen.Add(g))
            {
                return false;
            }
        }
        return true;
    }
}

public sealed record GearPawn
{
    public GearClimate? Climate { get; init; }
    public Fact<ApparelPolicyState> Policy { get; init; }

    /// <summary>
    /// Unknown only for a pawn that cannot wear apparel (Blocked, no apparel policy) or one the model refused,
    /// whose cause ModelRefusal names; PlanColonyGear fails on a refused pawn rather than judging it by a weaker rule.
    /// </summary>
    public Fact<GearLoadoutInput> LoadoutModel { get; init; }

    public string ModelRefusal { get; init; } = "";
    public PawnId Pawn { get; init; }
    public string Loadout { get; init; } = "";
    public bool Blocked { get; init; }

    /// <summary>Deficit and Replacements are projected from the loadout model's gaps (ModeledGearObservation); a census decoder never sets them.</summary>
    public Fact<bool> Deficit { get; init; }

    public Fact<EquatableArray<GearCandidate>> Candidates { get; init; }
    public Fact<EquatableArray<GearReplacement>> Replacements { get; init; }
    public Fact<EquatableArray<GearApparel>> Apparel { get; init; }

    /// <summary>Reports a pawn wearing any garment at or under the threshold.</summary>
    internal bool Tattered()
    {
        foreach (var a in Apparel.ValueOrDefault)
        {
            if (a.Condition <= PolicyFunctions.GearTatteredCondition)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Reports a pawn with a core body-part group no garment covers.</summary>
    internal bool Uncovered()
    {
        var apparel = Apparel.ValueOrDefault;
        foreach (var group in PolicyFunctions.GearCoreGroups)
        {
            var covered = apparel.Any(a => a.Groups.Contains(group));
            if (!covered)
            {
                return true;
            }
        }
        return false;
    }
}

public sealed record GearObservation
{
    public IReadOnlyList<GearPawn> Pawns { get; init; } = [];
    public Fact<EquatableArray<GearStock>> Stored { get; init; }

    /// <summary>Every outfit policy's load id; one no pawn keeps (OutfitKeep) is owed a prune.</summary>
    public Fact<EquatableArray<string>> Outfits { get; init; }

    /// <summary>The outfits owed a prune: none until every pawn is on its own outfit (OutfitKeep).</summary>
    public IReadOnlyList<string> OutfitsToPrune()
    {
        var settled = PolicyFunctions.OutfitKeep(Pawns, out var keep);
        if (!settled || !Outfits.TryGetValue(out var all))
        {
            return [];
        }
        return all.Where(id => !keep.ContainsKey(id)).ToList();
    }

    /// <summary>Throws <see cref="DomainException"/> where Go returned an error.</summary>
    public void Validate()
    {
        if (Stored.TryGetValue(out var stored))
        {
            var seen = new HashSet<GearStock>();
            foreach (var row in stored)
            {
                var key = row with { Count = 0 };
                DomainException.Require(
                    PolicyFunctions.ValidResource(row.Definition)
                        && (string.IsNullOrEmpty(row.Stuff.Value) || PolicyFunctions.ValidResource(row.Stuff))
                        && row.Quality >= 0
                        && row.Quality <= 6
                        && row.HPBand >= 5
                        && row.HPBand <= 9
                        && row.Count > 0
                        && seen.Add(key),
                    "invalid gear storage"
                );
            }
        }
        var pawns = new HashSet<PawnId>();
        foreach (var p in Pawns)
        {
            p.Climate.Validate();
            DomainException.Require(PolicyFunctions.FoodId(p.Pawn.Value) && PolicyFunctions.FoodId(p.Loadout) && pawns.Add(p.Pawn), "invalid gear pawn or loadout");
            if (p.LoadoutModel.TryGetValue(out var model))
            {
                model.Validate();
            }
            if (p.Candidates.TryGetValue(out var candidates))
            {
                var targets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in candidates)
                {
                    DomainException.Require(
                        PolicyFunctions.FoodId(c.Target) && PolicyFunctions.ValidResource(c.Definition) && PolicyFunctions.FoodNumber(c.Gain) && c.Gain > 0 && targets.Add(c.Target),
                        "invalid gear candidate"
                    );
                }
            }
            if (p.Replacements.TryGetValue(out var needs))
            {
                var identities = new HashSet<GearReplacement>();
                foreach (var n in needs)
                {
                    DomainException.Require(
                        PolicyFunctions.ValidResource(n.Definition)
                            && (string.IsNullOrEmpty(n.Stuff.Value) || PolicyFunctions.ValidResource(n.Stuff))
                            && PolicyFunctions.FoodId(n.Reason)
                            && identities.Add(n),
                        "invalid gear replacement need"
                    );
                }
            }
            if (p.Apparel.TryGetValue(out var apparel))
            {
                foreach (var a in apparel)
                {
                    DomainException.Require(a.Valid(), "invalid gear apparel");
                }
            }
        }
    }
}

/// <summary>
/// The census MaintainEquipment is judged on. Recovered and Deficit follow the loadout model's gaps and the
/// outfit policy; WornOut and Uncovered are the apparel-condition census (the fraction of pawns wearing any
/// garment at or under the tattered threshold, and the fraction with a core body-part group uncovered), known
/// only when every pawn's worn apparel was observed.
/// </summary>
public sealed record GearReview
{
    public IReadOnlyList<GearLoadout> Loadouts { get; init; } = [];
    public Fact<EquatableArray<GearDemand>> Demand { get; init; }
    public Fact<bool> Recovered { get; init; }
    public Fact<double> Deficit { get; init; }
    public Fact<double> WornOut { get; init; }
    public Fact<double> Uncovered { get; init; }
}

/// <summary>The way a gear method resolved. Wire strings are the method id's kind words.</summary>
public enum GearMethodKind
{
    [WireName("unknown")]
    Unknown = 0,

    [WireName("recovered")]
    Recovered,

    [WireName("replace")]
    Replace,

    [WireName("produce")]
    Produce,

    [WireName("wait_for_existing_work")]
    WaitForExistingWork,

    [WireName("no_eligible_method")]
    Blocked,
}

public sealed record GearMethod
{
    public int Count { get; init; }
    public GearMethodKind Kind { get; init; }
    public MethodId Id { get; init; }
    public PawnId Pawn { get; init; }
    public string Loadout { get; init; } = "";
    public string Target { get; init; } = "";
    public GearReplacement Need { get; init; } = new();
    public string Bench { get; init; } = "";
    public string Recipe { get; init; } = "";
    public EquatableArray<Resource> Filter { get; init; }

    /// <summary>The standing bill a WaitForExistingWork found already making Need.</summary>
    public GearBill? Bill { get; init; }
}

public sealed record GearBill
{
    public string Id { get; init; } = "";
    public string Recipe { get; init; } = "";

    /// <summary>The recipe's role by its catalog row.</summary>
    public RecipeRole Role { get; init; }

    public Fact<bool> Active { get; init; }

    /// <summary>The pinned pawn, known "" when unrestricted.</summary>
    public Fact<string> Worker { get; init; }

    public EquatableArray<Resource> Products { get; init; }

    /// <summary>The bill as the work ledger identifies it; unknown when the readback lacked the repeat mode or count.</summary>
    public Fact<OrderSpec> Spec { get; init; }

    /// <summary>A finished bill.</summary>
    public bool Spent { get; init; }

    /// <summary>The declare-only class of its recipe (GearRecipe.Kind), production otherwise.</summary>
    public LedgerBillKind Kind { get; init; }
}

public sealed record GearRecipe
{
    public string Definition { get; init; } = "";

    /// <summary>What the recipe does by its catalog row.</summary>
    public RecipeRole Role { get; init; }

    public EquatableArray<Resource> Products { get; init; }
    public Fact<bool> Available { get; init; }
    public Fact<bool> AvailableOn { get; init; }
    public Fact<EquatableArray<EquatableArray<Amount>>> Ingredients { get; init; }
    public Fact<EquatableArray<WorkRequirement>> RequiredWork { get; init; }

    /// <summary>The work one unit takes (the catalog's RecipeWorkAmount); RequiredWork is the skill gate, not this.</summary>
    public Fact<double> WorkAmount { get; init; }

    /// <summary>The PawnKindDef of the mech a gestation recipe makes, "" for every other recipe.</summary>
    public string MechKind { get; init; } = "";

    /// <summary>The declare-only class of the recipe by what it makes, production for every other recipe.</summary>
    public LedgerBillKind Kind { get; init; }

    /// <summary>The armory ladder rung of the recipe at this bench, unknown for a recipe that makes no ordinary weapon.</summary>
    public ArmoryTier Armory { get; init; }
}

public sealed record GearBench
{
    public string Id { get; init; } = "";

    /// <summary>The workbench definition, empty when unread.</summary>
    public string Def { get; init; } = "";

    /// <summary>Whether the bench takes a new bill.</summary>
    public Fact<bool> Usable { get; init; }

    /// <summary>The bench's WorkTableWorkSpeedFactor now; unknown when native sent none.</summary>
    public Fact<double> WorkSpeed { get; init; }

    public Fact<EquatableArray<GearBill>> Bills { get; init; }
    public Fact<EquatableArray<GearRecipe>> Recipes { get; init; }
}

/// <summary>
/// One native recipe definition with the player-buildable bench definitions that host it, read before any such
/// bench exists so a workshop project can choose which bench to stage. Available is the recipe's own research
/// gate; the bench definitions' own availability is judged against the planning census separately.
/// </summary>
public sealed record RecipeHost
{
    public string Definition { get; init; } = "";
    public EquatableArray<Resource> Products { get; init; }
    public bool Available { get; init; }
    public EquatableArray<string> Benches { get; init; }
    public Fact<EquatableArray<EquatableArray<Amount>>> Ingredients { get; init; }
    public Fact<EquatableArray<WorkRequirement>> RequiredWork { get; init; }

    /// <summary>The ResearchProjectDefs the recipe itself requires (sorted); empty when only its bench gates it.</summary>
    public EquatableArray<string> Research { get; init; }
}

public sealed record GearPlanningRequest
{
    public Fact<GearObservation> Observation { get; init; }
    public IReadOnlyList<MethodId> Seen { get; init; } = [];
    public Fact<EquatableArray<GearBench>> Benches { get; init; }

    /// <summary>Each stuff's catalog categories (ItemFacts): the stuffs a bill's filter admits beside the loadout's own (GearFilter).</summary>
    public IReadOnlyDictionary<Resource, IReadOnlyList<string>> StuffCategories { get; init; } = new Dictionary<Resource, IReadOnlyList<string>>();
}

/// <summary>One definition to produce, attributed to a pawn or to the armory's colony weapon batch.</summary>
internal readonly record struct GearNeed(GearPawn Pawn, GearReplacement Need);

public static partial class PolicyFunctions
{
    /// <summary>The hit-point fraction at or under which native upkeep counts a garment as worn out and raises a replacement need.</summary>
    public const double GearTatteredCondition = .5;

    /// <summary>The body-part groups a dressed colonist covers; a pawn wearing nothing over one of them is uncovered.</summary>
    public static readonly IReadOnlyList<string> GearCoreGroups = ["Torso", "Legs"];

    /// <summary>
    /// Matches the complete census used by equipment upkeep. An unknown or empty census cannot establish
    /// recovery; a pawn the loadout model refuses throws naming the cause.
    /// </summary>
    public static GearReview ReviewGear(Fact<GearObservation> f)
    {
        if (!f.TryGetValue(out var v))
        {
            return new GearReview();
        }
        v.Validate();
        if (v.Pawns.Count == 0)
        {
            return new GearReview();
        }
        int missing = 0,
            tattered = 0,
            uncovered = 0;
        var dressed = true;
        var (loadouts, demand) = PlanColonyGear(v.Pawns);
        var modeled = new Dictionary<PawnId, GearLoadout>();
        foreach (var l in loadouts)
        {
            modeled[l.Pawn] = l;
        }
        foreach (var p in v.Pawns)
        {
            var deficit = false;
            if (modeled.TryGetValue(p.Pawn, out var l))
            {
                foreach (var gap in l.Gaps)
                {
                    deficit = deficit || gap.Gain > GearGapThreshold(l.Role);
                }
            }
            var policyNeeded = DesiredApparelPolicy(p, out _);
            if (deficit || policyNeeded)
            {
                missing++;
            }
            if (!p.Apparel.IsKnown)
            {
                dressed = false;
                continue;
            }
            if (p.Tattered())
            {
                tattered++;
            }
            if (p.Uncovered())
            {
                uncovered++;
            }
        }
        // Outfits owed a prune keep the goal open for the prune.
        if (missing == 0 && v.OutfitsToPrune().Count > 0)
        {
            missing++;
        }
        double n = v.Pawns.Count;
        var review = new GearReview
        {
            Loadouts = loadouts,
            Demand = demand,
            Recovered = Known(missing == 0),
            Deficit = Known(missing / n),
        };
        if (dressed)
        {
            review = review with { WornOut = Known(tattered / n), Uncovered = Known(uncovered / n) };
        }
        return review;
    }

    /// <summary>
    /// The definitions the census asks a bench to produce for pawns in deficit (replacement needs with no loose
    /// candidate to wear), sorted and deduplicated. Empty unless the census is known, valid and not recovered,
    /// so the work planner enables a bench's work type exactly while MaintainEquipment is in deficit and could
    /// raise a bill. A pawn the loadout model refuses throws, not an empty answer.
    /// </summary>
    public static IReadOnlyList<Resource> GearReplacementNeeds(Fact<GearObservation> f)
    {
        var review = ReviewGear(f);
        if (!review.Recovered.TryGetValue(out var recovered) || recovered)
        {
            return [];
        }
        f.TryGetValue(out var observed);
        var v = ModeledGearObservation(observed!, review.Loadouts);
        var seen = new HashSet<Resource>();
        var @out = new List<Resource>();
        foreach (var p in v.Pawns)
        {
            if (!p.Deficit.ValueOrDefault)
            {
                continue;
            }
            foreach (var n in p.Replacements.ValueOrDefault)
            {
                if (seen.Add(n.Definition))
                {
                    @out.Add(n.Definition);
                }
            }
        }
        return @out.OrderBy(r => r.Value, StringComparer.Ordinal).ToList();
    }

    /// <summary>Stable typed identity; retired methods stay seen for this Episode. The hash input is Go's JSON encoding of the identity.</summary>
    internal static MethodId GearMethodId(GearMethodKind kind, GearPawn p, string target, GearReplacement need)
    {
        var json =
            "{\"Pawn\":" + GoJsonString(p.Pawn.Value)
            + ",\"Loadout\":" + GoJsonString(p.Loadout)
            + ",\"Target\":" + GoJsonString(target)
            + ",\"Need\":{\"Definition\":" + GoJsonString(need.Definition.Value ?? "")
            + ",\"Stuff\":" + GoJsonString(need.Stuff.Value ?? "")
            + ",\"Reason\":" + GoJsonString(need.Reason) + "}}";
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return new MethodId("gear-" + Wire.Name(kind) + "-" + Convert.ToHexStringLower(sum.AsSpan(0, 16)));
    }

    /// <summary>Encodes a string as Go's encoding/json does: short escapes for quote, backslash and common controls, \u00XX for other controls, HTML-safe escapes for &lt;, &gt;, &amp;, and U+2028/2029.</summary>
    internal static string GoJsonString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '<':
                case '>':
                case '&':
                case (char)0x2028:
                case (char)0x2029:
                    sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    internal static bool ContainsResource(IReadOnlyList<Resource> values, Resource want) => values.Any(v => v == want);

    /// <summary>Proposes one exact replacement: wearing an item already observed. Crafting is the ledger's (DeclareGearOrders).</summary>
    public static GearMethod SelectGearMethod(GearPlanningRequest r)
    {
        var review = ReviewGear(r.Observation);
        if (!review.Recovered.IsKnown)
        {
            return new GearMethod { Kind = GearMethodKind.Unknown };
        }
        if (Positive(review.Recovered))
        {
            return new GearMethod { Kind = GearMethodKind.Recovered };
        }
        r.Observation.TryGetValue(out var v);
        return SelectGear(r, ModeledGearObservation(v!, review.Loadouts));
    }

    /// <summary>Chooses the method for a census already projected from the loadout model (ModeledGearObservation).</summary>
    private static GearMethod SelectGear(GearPlanningRequest r, GearObservation v)
    {
        var seen = GearSeen(r.Seen);
        var choices = new List<(GearPawn Pawn, GearCandidate Candidate)>();
        foreach (var p in v.Pawns)
        {
            if (p.Blocked)
            {
                continue;
            }
            foreach (var c in p.Candidates.ValueOrDefault)
            {
                choices.Add((p, c));
            }
        }
        var ordered = choices
            .OrderByDescending(c => c.Candidate.Gain)
            .ThenBy(c => c.Pawn.Pawn.Value, StringComparer.Ordinal)
            .ThenBy(c => c.Candidate.Target, StringComparer.Ordinal);
        foreach (var (pawn, candidate) in ordered)
        {
            var id = GearMethodId(GearMethodKind.Replace, pawn, candidate.Target, new GearReplacement());
            if (!seen.Contains(id))
            {
                return new GearMethod
                {
                    Kind = GearMethodKind.Replace,
                    Id = id,
                    Pawn = pawn.Pawn,
                    Loadout = pawn.Loadout,
                    Target = candidate.Target,
                };
            }
        }
        return new GearMethod { Kind = GearMethodKind.Blocked };
    }

    /// <summary>Indexes a goal's method history, refusing a malformed one.</summary>
    private static HashSet<MethodId> GearSeen(IReadOnlyList<MethodId> ids)
    {
        var seen = new HashSet<MethodId>();
        foreach (var id in ids)
        {
            DomainException.Require(FoodId(id.Value) && seen.Add(id), "invalid gear method history");
        }
        return seen;
    }

    /// <summary>
    /// The bench census sorted by id and, per definition and stuff, how many items the needs still lack after
    /// netting the stored stock. Known is false while the bench census is unread. (Go <c>gearProductionDemand</c>;
    /// the exported <c>GearProductionDemand</c> is the loadout-gap aggregate.)
    /// </summary>
    private static (IReadOnlyList<GearBench> Benches, Dictionary<GearStockKey, int> Demand, bool Known) GearBenchDemand(
        IReadOnlyList<GearNeed> needs,
        GearObservation v,
        GearReview review,
        GearPlanningRequest r
    )
    {
        if (!r.Benches.TryGetValue(out var all))
        {
            return ([], [], false);
        }
        ValidateGearProduction(all);
        var benches = all.OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
        var demand = new Dictionary<GearStockKey, int>();
        foreach (var n in needs)
        {
            var key = new GearStockKey(n.Need.Definition, n.Need.Stuff);
            demand[key] = demand.GetValueOrDefault(key) + 1;
        }
        var stored = UnassignedGearStock(v.Stored.ValueOrDefault, review.Loadouts);
        foreach (var row in stored)
        {
            if (row.HPBand >= 5 && row.Quality >= 2)
            {
                var key = new GearStockKey(row.Definition, row.Stuff);
                demand[key] = Math.Max(0, demand.GetValueOrDefault(key) - row.Count);
            }
        }
        foreach (var count in demand.Values)
        {
            DomainException.Require(count <= int.MaxValue, "gear batch exceeds bill bound");
        }
        return (benches, demand, true);
    }

    /// <summary>
    /// Resolves one need of count items against the benches: Wait with the standing bill (Bill) that already makes
    /// it, Unknown while a bench's bills or a recipe's facts are unread, Produce with the first funded recipe.
    /// Resolved is false when no recipe is funded, so the next need is judged.
    /// </summary>
    private static (GearMethod Method, bool Resolved) ProduceNeed(GearNeed n, int count, IReadOnlyList<GearBench> benches, GearPlanningRequest r)
    {
        // Check every bench before adding a bill, including later-sorted player benches.
        foreach (var b in benches)
        {
            if (!b.Bills.TryGetValue(out var bills))
            {
                return (new GearMethod { Kind = GearMethodKind.Unknown }, true);
            }
            foreach (var bill in bills)
            {
                if (bill.Spent || !ContainsResource(bill.Products, n.Need.Definition))
                {
                    continue;
                }
                if (!bill.Active.TryGetValue(out var active))
                {
                    return (new GearMethod { Kind = GearMethodKind.Unknown }, true);
                }
                if (active)
                {
                    return (new GearMethod { Kind = GearMethodKind.WaitForExistingWork, Need = n.Need, Bill = bill }, true);
                }
            }
        }
        foreach (var b in benches)
        {
            if (!b.Recipes.TryGetValue(out var all))
            {
                return (new GearMethod { Kind = GearMethodKind.Unknown }, true);
            }
            foreach (var recipe in all.OrderBy(x => x.Definition, StringComparer.Ordinal))
            {
                if (!ContainsResource(recipe.Products, n.Need.Definition))
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
                    return (new GearMethod { Kind = GearMethodKind.Unknown }, true);
                }
                if (!recipe.RequiredWork.IsKnown || !recipe.Ingredients.TryGetValue(out var slots))
                {
                    return (new GearMethod { Kind = GearMethodKind.Unknown }, true);
                }
                var batch = GearBatchIngredients(slots, count);
                if (GearFilter(batch, n.Need.Stuff, r.StuffCategories) is { } filter)
                {
                    return (
                        new GearMethod
                        {
                            Kind = GearMethodKind.Produce,
                            Count = count,
                            Bench = b.Id,
                            Recipe = recipe.Definition,
                            Filter = EquatableArray.From(filter),
                        },
                        true
                    );
                }
            }
        }
        return (new GearMethod(), false);
    }

    private static void ValidateGearProduction(IReadOnlyList<GearBench> benches)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        static bool Products(IReadOnlyList<Resource> v)
        {
            var seen = new HashSet<Resource>();
            foreach (var p in v)
            {
                if (!ValidResource(p) || !seen.Add(p))
                {
                    return false;
                }
            }
            return true;
        }
        foreach (var b in benches)
        {
            DomainException.Require(FoodId(b.Id) && ids.Add(b.Id), "invalid gear bench");
            var bills = b.Bills.ValueOrDefault;
            var recipes = b.Recipes.ValueOrDefault;
            foreach (var bill in bills)
            {
                DomainException.Require(Products(bill.Products), "invalid bill products");
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in recipes)
            {
                DomainException.Require(FoodId(recipe.Definition) && names.Add(recipe.Definition) && Products(recipe.Products), "invalid gear recipe");
                if (recipe.RequiredWork.TryGetValue(out var work))
                {
                    AssignWork([], work);
                }
                foreach (var slot in recipe.Ingredients.ValueOrDefault)
                {
                    DomainException.Require(slot.Count != 0, "invalid ingredient alternatives");
                    var resources = new HashSet<Resource>();
                    foreach (var a in slot)
                    {
                        DomainException.Require(ValidResource(a.Resource) && a.Count > 0 && resources.Add(a.Resource), "invalid ingredient amount");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The stuffs a bill never draws on unless the loadout names them: the currency and the favor-sale gold. A
    /// judgment table (DefTables): no row marks a stuff as currency or as the favor trade's.
    /// </summary>
    internal static IReadOnlyList<Resource> GearValuableDefs => [new Resource("Silver"), FavorSaleResource];

    private static bool GearValuable(Resource r) => GearValuableDefs.Contains(r);

    /// <summary>
    /// The ingredient filter of a bill placed with nothing in stock. A slot holding the loadout's stuff admits it
    /// and the stuffs sharing a catalog category with it (never a valuable); any other slot admits only its
    /// cheapest non-valuable alternative (fewest units, then name), the member OpenBillDemand asks for. Null when
    /// a slot has no admissible member, the loadout's stuff is in no slot, or the flat native filter would let
    /// another slot's member stand in for a stuff-bearing slot.
    /// </summary>
    internal static IReadOnlyList<Resource>? GearFilter(
        EquatableArray<EquatableArray<Amount>> slots,
        Resource stuff,
        IReadOnlyDictionary<Resource, IReadOnlyList<string>> categories
    )
    {
        IReadOnlyList<string> Categories(Resource r) => categories.TryGetValue(r, out var c) ? c : [];
        var hasStuff = !string.IsNullOrEmpty(stuff.Value);
        var admitted = new HashSet<Resource>();
        var members = new List<HashSet<Resource>>();
        var stuffSlot = new bool[slots.Count];
        var stuffed = false;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var set = new HashSet<Resource>();
            members.Add(set);
            stuffSlot[i] = hasStuff && slot.Any(a => a.Resource == stuff);
            stuffed = stuffed || stuffSlot[i];
            Resource? pick = null;
            long pickCount = 0;
            foreach (var a in slot)
            {
                if (stuffSlot[i])
                {
                    if (a.Resource == stuff || (!GearValuable(a.Resource) && Categories(stuff).Any(c => Categories(a.Resource).Contains(c))))
                    {
                        set.Add(a.Resource);
                    }
                }
                else if (
                    !GearValuable(a.Resource)
                    && (pick is null || a.Count < pickCount || (a.Count == pickCount && string.CompareOrdinal(a.Resource.Value, pick.Value.Value) < 0))
                )
                {
                    pick = a.Resource;
                    pickCount = a.Count;
                }
            }
            if (pick is { } p)
            {
                set.Add(p);
            }
            if (set.Count == 0)
            {
                return null;
            }
            admitted.UnionWith(set);
        }
        if (hasStuff && !stuffed)
        {
            return null;
        }
        for (var i = 0; i < slots.Count; i++)
        {
            if (!stuffSlot[i])
            {
                continue;
            }
            foreach (var a in slots[i])
            {
                if (admitted.Contains(a.Resource) && !members[i].Contains(a.Resource))
                {
                    return null;
                }
            }
        }
        return admitted.OrderBy(r => r.Value, StringComparer.Ordinal).ToList();
    }
}
