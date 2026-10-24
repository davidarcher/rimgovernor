using System.Text;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A loose or stored garment to wear. Candidates have already passed native outfit, forced/locked gear,
/// player-job, ownership, reachability and resource-policy checks. Proposals still require a fresh native
/// admission against the exact inspected loadout before dispatch.
/// </summary>
public sealed record GearCandidate(string Target, double Gain, Resource Definition);

/// <summary>
/// A definition a pawn or the armory batch needs made. It names no stuff: a clothing craft takes its stuff from the
/// forecast's selected outcome, armor and weapons from the bill filter, and stored spares are claimed by definition.
/// </summary>
public sealed record GearReplacement
{
    public Resource Definition { get; init; }
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
        if (!AdmissionRules.ValidResource(Definition) || !FoodForecastRules.FoodNumber(Condition) || Condition > 1)
        {
            return false;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in Groups)
        {
            if (!FoodForecastRules.FoodId(g) || !seen.Add(g))
            {
                return false;
            }
        }
        return true;
    }
}

public sealed record GearPawn
{
    /// <summary>The pawn's outfit census; null for a pawn without an outfit (it cannot wear apparel).</summary>
    public ApparelPolicyState? Policy { get; init; }

    /// <summary>
    /// Unknown only for a pawn that cannot wear apparel (Blocked, no apparel policy) or one the model refused,
    /// whose cause ModelRefusal names; PlanColonyGear fails on a refused pawn rather than judging it by a weaker rule.
    /// </summary>
    public Fact<GearLoadoutInput> LoadoutModel { get; init; }

    public string ModelRefusal { get; init; } = "";
    public PawnId Pawn { get; init; }

    /// <summary>
    /// Whether the pawn's outfit allows tainted apparel, as the latest review's coupled comparator chose it (GearChoices,
    /// stamped once on the rounds census). False otherwise, the default.
    /// </summary>
    public bool AllowTainted { get; init; }

    /// <summary>The pawn's insulation floor as the comparator chose it (GearChoices, #3329); null is its worn insulation, the default.</summary>
    public double? ColdFloor { get; init; }

    public double? HeatFloor { get; init; }
    public string Loadout { get; init; } = "";
    public bool Blocked { get; init; }

    /// <summary>Deficit and Replacements are projected from the loadout model's gaps (ModeledGearObservation); a census decoder never sets them.</summary>
    public Fact<bool> Deficit { get; init; }

    public EquatableArray<GearCandidate> Candidates { get; init; }
    public Fact<EquatableArray<GearReplacement>> Replacements { get; init; }
    public EquatableArray<GearApparel> Apparel { get; init; }

}

public sealed record GearObservation
{
    public IReadOnlyList<GearPawn> Pawns { get; init; } = [];

    /// <summary>The map's seasonal climate the census dresses for.</summary>
    public required GearClimate Climate { get; init; }

    public EquatableArray<GearStock> Stored { get; init; }

    /// <summary>Every outfit policy's load id; one no pawn keeps (OutfitKeep) is owed a prune.</summary>
    public EquatableArray<string> Outfits { get; init; }

    /// <summary>
    /// The bill options of apparel only unfinished knowledge projects (Anomaly) keep from the colony (#3848). No pawn's options hold
    /// them, since no bill of one can start; the colony model prices what finishing those projects adds with them.
    /// </summary>
    public EquatableArray<GearOption> KnowledgeBills { get; init; }

    /// <summary>The outfits owed a prune: none until every pawn is on its own outfit (OutfitKeep).</summary>
    public IReadOnlyList<string> OutfitsToPrune()
    {
        var settled = ApparelPolicyRules.OutfitKeep(Pawns, out var keep);
        if (!settled)
        {
            return [];
        }
        return Outfits.Where(id => !keep.ContainsKey(id)).ToList();
    }

    /// <summary>Throws <see cref="DomainException"/>.</summary>
    public void Validate()
    {
        var seen = new HashSet<GearStock>();
        foreach (var row in Stored)
        {
            var key = row with { Count = 0 };
            DomainException.Require(
                AdmissionRules.ValidResource(row.Definition)
                    && (string.IsNullOrEmpty(row.Stuff.Value) || AdmissionRules.ValidResource(row.Stuff))
                    && row.Quality >= 0
                    && row.Quality <= 6
                    && row.HPBand >= 5
                    && row.HPBand <= 9
                    && row.Count > 0
                    && seen.Add(key),
                "invalid gear storage"
            );
        }
        Climate.Validate();
        var pawns = new HashSet<PawnId>();
        foreach (var p in Pawns)
        {
            DomainException.Require(FoodForecastRules.FoodId(p.Pawn.Value) && FoodForecastRules.FoodId(p.Loadout) && pawns.Add(p.Pawn), "invalid gear pawn or loadout");
            if (p.LoadoutModel.TryGetValue(out var model))
            {
                model.Validate();
            }
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in p.Candidates)
            {
                DomainException.Require(
                    FoodForecastRules.FoodId(c.Target) && AdmissionRules.ValidResource(c.Definition) && FoodForecastRules.FoodNumber(c.Gain) && c.Gain > 0 && targets.Add(c.Target),
                    "invalid gear candidate"
                );
            }
            if (p.Replacements.TryGetValue(out var needs))
            {
                var identities = new HashSet<GearReplacement>();
                foreach (var n in needs)
                {
                    DomainException.Require(
                        AdmissionRules.ValidResource(n.Definition)
                            && FoodForecastRules.FoodId(n.Reason)
                            && identities.Add(n),
                        "invalid gear replacement need"
                    );
                }
            }
            foreach (var a in p.Apparel)
            {
                DomainException.Require(a.Valid(), "invalid gear apparel");
            }
        }
    }
}

/// <summary>
/// The census the military/equipment Need is judged on. Recovered and Deficit follow the loadout model's gaps and the
/// outfit policy.
/// </summary>
public sealed record GearReview
{
    public IReadOnlyList<GearLoadout> Loadouts { get; init; } = [];
    public Fact<bool> Recovered { get; init; }
    public Fact<double> Deficit { get; init; }
}

/// <summary>Observed or proposed production supply for a loadout requirement; not a pawn labor winner.</summary>
public enum GearMethodKind
{
    [WireName("unknown")]
    Unknown = 0,

    [WireName("produce")]
    Produce,

    [WireName("wait_for_existing_work")]
    WaitForExistingWork,
}

public sealed record GearMethod
{
    public int Count { get; init; }
    public GearMethodKind Kind { get; init; }
    public string Bench { get; init; } = "";
    public string Recipe { get; init; } = "";
    public EquatableArray<Resource> Filter { get; init; }

    /// <summary>The standing bill a WaitForExistingWork found already making the need.</summary>
    public GearBill? Bill { get; init; }
}

public sealed record GearBill
{
    public string Id { get; init; } = "";
    public string Recipe { get; init; } = "";

    /// <summary>The recipe's role by its catalog row.</summary>
    public RecipeRole Role { get; init; }

    /// <summary>Neither suspended nor finished.</summary>
    public required bool Active { get; init; }

    /// <summary>The pinned pawn, "" when unrestricted.</summary>
    public required string Worker { get; init; }

    public EquatableArray<Resource> Products { get; init; }

    /// <summary>The bill as the work ledger identifies it.</summary>
    public required OrderSpec Spec { get; init; }

    /// <summary>The bill as the production bill selectors read it (the art selectors' bench rows); active as <see cref="Active"/> is.</summary>
    public required ExistingProductionBill Existing { get; init; }

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
    /// <summary>The ingredient slots as alternatives, each allowed def with the count it takes (the catalog's RecipeIngredients).</summary>
    public required EquatableArray<EquatableArray<Amount>> Ingredients { get; init; }

    /// <summary>The work a bill of the recipe at the bench needs covered (the catalog's RecipeWork).</summary>
    public required EquatableArray<WorkRequirement> RequiredWork { get; init; }

    /// <summary>The work one unit takes (the catalog's RecipeWorkAmount); RequiredWork is the skill gate, not this.</summary>
    public required double WorkAmount { get; init; }

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
    public required EquatableArray<EquatableArray<Amount>> Ingredients { get; init; }

    /// <summary>The ResearchProjectDefs the recipe itself requires (sorted); empty when only its bench gates it.</summary>
    public EquatableArray<string> Research { get; init; }
}

public sealed record GearPlanningRequest
{
    public Fact<GearObservation> Observation { get; init; }
    public Fact<EquatableArray<GearBench>> Benches { get; init; }

    /// <summary>The catalog's market values: the member a bill's filter picks in each slot (GearFilter).</summary>
    public ItemFacts Items { get; init; } = new();

    /// <summary>
    /// The catalog's craft recipes (RoundsFacts.Crafts): a need no standing bench's recipe makes is ordered from
    /// one of these with no bench kind (#3659), and the station graph hosts it.
    /// </summary>
    public EquatableArray<GearRecipe> Crafts { get; init; }
}

/// <summary>One definition to produce, attributed to a pawn or to the armory's colony weapon batch.</summary>
internal readonly record struct GearNeed(GearPawn Pawn, GearReplacement Need);

public static class GearRules
{
    /// <summary>The hit-point fraction at or under which native upkeep counts a garment as worn out and raises a replacement need.</summary>
    public const double GearTatteredCondition = .5;

    /// <summary>The body-part groups a dressed colonist covers; a pawn wearing nothing over one of them is uncovered.</summary>
    public static readonly IReadOnlyList<string> GearCoreGroups = ["Torso", "Legs"];

    // The gear review's span, under the scope prefix the trace listener samples; Policy carries no telemetry
    // reference, so it names the source itself.
    private static readonly System.Diagnostics.ActivitySource s_spans = new("github.com/davidarcher/RimGovernor/policy");

    // The colony loadouts of the last few distinct pawn rows, most recent first: one step reviews the same census
    // from every planner that reads gear, each holding its own copy of the rows, so the search runs once per
    // distinct census rather than once per caller.
    private const int SharedLoadoutsKept = 8;
    private static readonly object s_loadoutsLock = new();
    private static readonly List<(EquatableArray<GearPawn> Pawns, IReadOnlyList<GearLoadout> Loadouts)> s_loadouts = [];

    /// <summary>
    /// Matches the complete census used by equipment upkeep. An unknown or empty census cannot establish
    /// recovery; a pawn the loadout model refuses throws naming the cause. The loadouts are planned once per
    /// distinct set of pawn rows (<see cref="SharedColonyGear"/>).
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
        using var span = s_spans.StartActivity("gear.review");
        span?.SetTag("rimgovernor.pawns", v.Pawns.Count);
        var missing = 0;
        var loadouts = SharedColonyGear(v.Pawns, out var shared);
        span?.SetTag("rimgovernor.shared", shared);
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
                    deficit = deficit || GearLoadoutRules.GearGapWanted(l.Role, gap);
                }
            }
            var policyNeeded = ApparelPolicyRules.DesiredApparelPolicy(p, out _);
            if (deficit || policyNeeded)
            {
                missing++;
            }
        }
        // Outfits owed a prune keep the goal open for the prune.
        if (missing == 0 && v.OutfitsToPrune().Count > 0)
        {
            missing++;
        }
        double n = v.Pawns.Count;
        return new GearReview
        {
            Loadouts = loadouts,
            Recovered = Known(missing == 0),
            Deficit = Known(missing / n),
        };
    }

    /// <summary>
    /// <see cref="GearLoadoutRules.PlanColonyGear"/> over <paramref name="pawns"/>, or the loadouts already planned
    /// for equal rows (<paramref name="shared"/>). Rows compare by value, so a copy of the same census shares its
    /// plan. A refusal throws again on every call; only a plan is kept.
    /// </summary>
    internal static IReadOnlyList<GearLoadout> SharedColonyGear(IReadOnlyList<GearPawn> pawns, out bool shared)
    {
        var key = EquatableArray.From(pawns);
        lock (s_loadoutsLock)
        {
            var i = s_loadouts.FindIndex(e => e.Pawns.Equals(key));
            if (i >= 0)
            {
                var hit = s_loadouts[i];
                s_loadouts.RemoveAt(i);
                s_loadouts.Insert(0, hit);
                shared = true;
                return hit.Loadouts;
            }
        }
        IReadOnlyList<GearLoadout> loadouts = [.. GearLoadoutRules.PlanColonyGear(pawns)];
        lock (s_loadoutsLock)
        {
            // A planner reviewing the same census concurrently may have filed it first.
            s_loadouts.RemoveAll(e => e.Pawns.Equals(key));
            s_loadouts.Insert(0, (key, loadouts));
            if (s_loadouts.Count > SharedLoadoutsKept)
            {
                s_loadouts.RemoveAt(s_loadouts.Count - 1);
            }
        }
        shared = false;
        return loadouts;
    }

    /// <summary>
    /// The definitions the census asks a bench to produce for pawns in deficit (replacement needs with no loose
    /// candidate to wear), sorted and deduplicated. Empty unless the census is known, valid and not recovered,
    /// so the work planner enables a bench's work type exactly while the equipment Need is unmet and could
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
        var v = GearLoadoutRules.ModeledGearObservation(observed!, review.Loadouts);
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

    /// <summary>Encodes a string as encoding/json does: short escapes for quote, backslash and common controls, \u00XX for other controls, HTML-safe escapes for &lt;, &gt;, &amp;, and U+2028/2029.</summary>
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

    /// <summary>
    /// The bench census sorted by id and, per definition, how many items the needs still lack after netting the
    /// stored stock of any stuff. Known is false while the bench census is unread.
    /// </summary>
    internal static (IReadOnlyList<GearBench> Benches, Dictionary<Resource, int> Demand, bool Known) GearBenchDemand(
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
        var demand = new Dictionary<Resource, int>();
        foreach (var n in needs)
        {
            demand[n.Need.Definition] = demand.GetValueOrDefault(n.Need.Definition) + 1;
        }
        var stored = GearStockRules.UnassignedGearStock(v.Stored, review.Loadouts);
        foreach (var row in stored)
        {
            if (row.HPBand >= 5 && row.Quality >= 2 && demand.TryGetValue(row.Definition, out var owed))
            {
                demand[row.Definition] = Math.Max(0, owed - row.Count);
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
    /// it, Unknown while a bench's bills or a recipe's facts are unread, Produce with the first funded recipe, else
    /// with the first funded catalog recipe no bench lists (CraftOf, #3659). Resolved is false when no recipe is
    /// funded, so the next need is judged.
    /// </summary>
    internal static (GearMethod Method, bool Resolved) ProduceNeed(GearNeed n, int count, IReadOnlyList<GearBench> benches, GearPlanningRequest r)
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
                if (bill.Active)
                {
                    return (new GearMethod { Kind = GearMethodKind.WaitForExistingWork, Bill = bill }, true);
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
                var batch = GearStockRules.GearBatchIngredients(recipe.Ingredients, count);
                if (GearFilter(batch, default, r.Items) is { } filter)
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
        // No standing bench works a recipe making the need: the catalog's recipe is ordered with no bench (#3659).
        if (CraftOf(r.Crafts, benches, n.Need.Definition, count, r.Items) is { } craft)
        {
            return (craft, true);
        }
        return (new GearMethod(), false);
    }

    /// <summary>The bench id of a method a catalog recipe makes (CraftOf): no bench stands for it, so its order names no bench kind.</summary>
    public const string CraftBench = "";

    /// <summary>
    /// The catalog recipes no standing bench lists, by definition: the recipes a declarer may order with no bench
    /// (#3659). A recipe a standing bench lists is that bench's, whatever its availability there.
    /// </summary>
    public static IEnumerable<GearRecipe> UnhostedCrafts(EquatableArray<GearRecipe> crafts, IReadOnlyList<GearBench> benches)
    {
        var hosted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in benches)
        {
            foreach (var recipe in b.Recipes.ValueOrDefault)
            {
                hosted.Add(recipe.Definition);
            }
        }
        return crafts.Where(c => !hosted.Contains(c.Definition) && c.Available.TryGetValue(out var available) && available).OrderBy(c => c.Definition, StringComparer.Ordinal);
    }

    /// <summary>The Produce of the first funded available catalog recipe making product that no standing bench lists; null when there is none.</summary>
    internal static GearMethod? CraftOf(EquatableArray<GearRecipe> crafts, IReadOnlyList<GearBench> benches, Resource product, int count, ItemFacts items)
    {
        foreach (var recipe in UnhostedCrafts(crafts, benches))
        {
            if (!ContainsResource(recipe.Products, product))
            {
                continue;
            }
            if (GearFilter(GearStockRules.GearBatchIngredients(recipe.Ingredients, count), default, items) is { } filter)
            {
                return new GearMethod { Kind = GearMethodKind.Produce, Count = count, Bench = CraftBench, Recipe = recipe.Definition, Filter = EquatableArray.From(filter) };
            }
        }
        return null;
    }

    internal static void ValidateGearProduction(IReadOnlyList<GearBench> benches)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        static bool Products(IReadOnlyList<Resource> v)
        {
            var seen = new HashSet<Resource>();
            foreach (var p in v)
            {
                if (!AdmissionRules.ValidResource(p) || !seen.Add(p))
                {
                    return false;
                }
            }
            return true;
        }
        foreach (var b in benches)
        {
            DomainException.Require(FoodForecastRules.FoodId(b.Id) && ids.Add(b.Id), "invalid gear bench");
            var bills = b.Bills.ValueOrDefault;
            var recipes = b.Recipes.ValueOrDefault;
            foreach (var bill in bills)
            {
                DomainException.Require(Products(bill.Products), "invalid bill products");
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in recipes)
            {
                DomainException.Require(FoodForecastRules.FoodId(recipe.Definition) && names.Add(recipe.Definition) && Products(recipe.Products), "invalid gear recipe");
                WorkRules.AssignWork([], recipe.RequiredWork);
                foreach (var slot in recipe.Ingredients)
                {
                    DomainException.Require(slot.Count != 0, "invalid ingredient alternatives");
                    var resources = new HashSet<Resource>();
                    foreach (var a in slot)
                    {
                        DomainException.Require(AdmissionRules.ValidResource(a.Resource) && a.Count > 0 && resources.Add(a.Resource), "invalid ingredient amount");
                    }
                }
            }
        }
    }

    /// <summary>
    /// The ingredient filter of a bill placed with nothing in stock. A slot holding <paramref name="stuff"/> (the
    /// runway's chosen stuff, a goal's named one, an export variant's) admits that stuff alone; none (default) names
    /// no stuff. Any other slot admits only its cheapest alternative by total cost, the units the slot takes of it
    /// times its market value (then name); a member without a market value is never picked. Null when a slot has no
    /// admissible member, the named stuff is in no slot, or the flat native filter would let another slot's member
    /// stand in for a stuff-bearing slot.
    /// </summary>
    internal static IReadOnlyList<Resource>? GearFilter(EquatableArray<EquatableArray<Amount>> slots, Resource stuff, ItemFacts items)
    {
        double? Cost(Amount a) => items.TryMarketValue(a.Resource, out var v) ? a.Count * v : null;
        var hasStuff = !string.IsNullOrEmpty(stuff.Value);
        var admitted = new HashSet<Resource>();
        var stuffSlots = new List<EquatableArray<Amount>>();
        foreach (var slot in slots)
        {
            if (hasStuff && slot.Any(a => a.Resource == stuff))
            {
                admitted.Add(stuff);
                stuffSlots.Add(slot);
                continue;
            }
            Resource? pick = null;
            double pickCost = 0;
            foreach (var a in slot)
            {
                if (
                    Cost(a) is { } priced
                    && (pick is null || priced < pickCost || (priced == pickCost && string.CompareOrdinal(a.Resource.Value, pick.Value.Value) < 0))
                )
                {
                    pick = a.Resource;
                    pickCost = priced;
                }
            }
            if (pick is not { } p)
            {
                return null;
            }
            admitted.Add(p);
        }
        if (hasStuff && stuffSlots.Count == 0)
        {
            return null;
        }
        if (stuffSlots.Any(slot => slot.Any(a => a.Resource != stuff && admitted.Contains(a.Resource))))
        {
            return null;
        }
        return admitted.OrderBy(r => r.Value, StringComparer.Ordinal).ToList();
    }
}
