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
    /// The pawn's loadout model: modeled, a pawn with no apparel policy (it cannot wear apparel), or one the model refused;
    /// PlanColonyGear fails on a refused pawn rather than judging it by a weaker rule.
    /// </summary>
    public required GearModel LoadoutModel { get; init; }

    /// <summary>The modeled loadout input; null for a pawn with no outfit or one the model refused.</summary>
    public GearLoadoutInput? Model => LoadoutModel is GearModel.Modeled m ? m.Input : null;

    public PawnId Pawn { get; init; }

    /// <summary>
    /// Whether the pawn's outfit allows tainted apparel, as the latest review's coupled comparator chose it (GearChoices,
    /// stamped once on the rounds census). False otherwise, the default.
    /// </summary>
    public bool AllowTainted { get; init; }

    /// <summary>The pawn's cold insulation floor (GearChoices, #3329): its worn insulation unless the comparator chose one.</summary>
    public InsulationFloor ColdFloor { get; init; } = new InsulationFloor.Worn();

    /// <summary>The pawn's heat insulation floor, as <see cref="ColdFloor"/> is.</summary>
    public InsulationFloor HeatFloor { get; init; } = new InsulationFloor.Worn();
    public string Loadout { get; init; } = "";
    public bool Blocked { get; init; }

    public EquatableArray<GearCandidate> Candidates { get; init; }
    public EquatableArray<GearApparel> Apparel { get; init; }

}

/// <summary>An insulation floor the loadout model dresses a pawn to (<see cref="GearPawn.ColdFloor"/>, <see cref="GearPawn.HeatFloor"/>).</summary>
public abstract record InsulationFloor
{
    private InsulationFloor()
    {
    }

    /// <summary>The pawn's worn insulation, the default: warmth never drops without the comparator.</summary>
    public sealed record Worn : InsulationFloor;

    /// <summary>The floor the latest review's coupled comparator chose (GearChoices).</summary>
    public sealed record Chosen(double Value) : InsulationFloor;

    /// <summary>The floor in degrees: the chosen one, else <paramref name="worn"/>.</summary>
    public double Or(double worn) => this is Chosen c ? c.Value : worn;
}

/// <summary>A census pawn's loadout model (<see cref="GearPawn.LoadoutModel"/>).</summary>
public abstract record GearModel
{
    private GearModel()
    {
    }

    /// <summary>The model's inputs for the pawn.</summary>
    public sealed record Modeled(GearLoadoutInput Input) : GearModel;

    /// <summary>A pawn with no apparel policy: it cannot wear apparel, so nothing is modeled.</summary>
    public sealed record NoOutfit : GearModel;

    /// <summary>A pawn the model cannot be built for; <see cref="Cause"/> names why.</summary>
    public sealed record Refused(string Cause) : GearModel;
}

/// <summary>
/// One census pawn as the loadout model leaves it (<see cref="GearLoadoutRules.ModeledGearPawns"/>): its candidates narrowed
/// to the model's wanted gaps, whether any gap is wanted, and the bill-sourced definitions those gaps need made.
/// </summary>
public sealed record ModeledGearPawn
{
    public required GearPawn Pawn { get; init; }
    public required bool Deficit { get; init; }
    public required EquatableArray<GearReplacement> Replacements { get; init; }
}

public sealed record GearObservation
{
    public IReadOnlyList<GearPawn> Pawns { get; init; } = [];

    /// <summary>The map's seasonal climate the census dresses for.</summary>
    public required GearClimate Climate { get; init; }

    /// <summary>The colony's expected raid every pawn's model values armor against (<see cref="GearLoadoutInput.Threat"/>).</summary>
    public required RaidThreat Threat { get; init; }

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
            p.Model?.Validate();
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in p.Candidates)
            {
                DomainException.Require(
                    FoodForecastRules.FoodId(c.Target) && AdmissionRules.ValidResource(c.Definition) && FoodForecastRules.FoodNumber(c.Gain) && c.Gain > 0 && targets.Add(c.Target),
                    "invalid gear candidate"
                );
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
    public required IReadOnlyList<GearLoadout> Loadouts { get; init; }

    /// <summary>No pawn wants a gap filled or an apparel policy, and no outfit is owed a prune.</summary>
    public required bool Recovered { get; init; }

    /// <summary>The share of census pawns short of gear or policy; an outfit prune alone counts as one pawn.</summary>
    public required double Deficit { get; init; }
}

/// <summary>Observed or proposed production supply for a loadout requirement; not a pawn labor winner.</summary>
public enum GearMethodKind
{
    [WireName("produce")]
    Produce,

    [WireName("wait_for_existing_work")]
    WaitForExistingWork,
}

public sealed record GearMethod
{
    public int Count { get; init; }
    public required GearMethodKind Kind { get; init; }
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

    /// <summary>Whether the recipe's own gates (research) admit it now.</summary>
    public required bool Available { get; init; }

    /// <summary>Whether the bench offers the recipe now; true for a catalog recipe, which no bench stands to refuse.</summary>
    public required bool AvailableOn { get; init; }

    /// <summary>The ingredient slots as alternatives, each allowed def with the count it takes (the catalog's RecipeIngredients).</summary>
    public required EquatableArray<EquatableArray<Amount>> Ingredients { get; init; }

    /// <summary>The work a bill of the recipe at the bench needs covered (the catalog's RecipeWork).</summary>
    public required EquatableArray<WorkRequirement> RequiredWork { get; init; }

    /// <summary>The work one unit takes (the catalog's RecipeWorkAmount); RequiredWork is the skill gate, not this.</summary>
    public required double WorkAmount { get; init; }

    /// <summary>The pawn stat that work goes at (the catalog's RecipeWorkSpeedStat, #3868); "" works at 1.</summary>
    public string WorkSpeedStat { get; init; } = "";

    /// <summary>The PawnKindDef of the mech a gestation recipe makes, "" for every other recipe.</summary>
    public string MechKind { get; init; } = "";

    /// <summary>The declare-only class of the recipe by what it makes, production for every other recipe.</summary>
    public LedgerBillKind Kind { get; init; }

    /// <summary>The armory ladder rung of the recipe at this bench, unladdered for a recipe that makes no ordinary weapon.</summary>
    public ArmoryRung Armory { get; init; } = new ArmoryRung.Unladdered();
}

public sealed record GearBench
{
    public string Id { get; init; } = "";

    /// <summary>The workbench definition, empty when unread.</summary>
    public string Def { get; init; } = "";

    /// <summary>Whether the bench takes a new bill.</summary>
    public required bool Usable { get; init; }

    /// <summary>The bench's WorkTableWorkSpeedFactor now.</summary>
    public required double WorkSpeed { get; init; }

    public required EquatableArray<GearBill> Bills { get; init; }
    public required EquatableArray<GearRecipe> Recipes { get; init; }
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
    public required GearObservation Observation { get; init; }

    /// <summary>The bench readback: every standing workbench with its bills and recipes.</summary>
    public required EquatableArray<GearBench> Benches { get; init; }

    /// <summary>The catalog's market values: the member a bill's filter picks in each slot (GearFilter).</summary>
    public ItemFacts Items { get; init; } = new();

    /// <summary>
    /// The catalog's craft recipes (RoundsFacts.Crafts): a need no standing bench's recipe makes is ordered from
    /// one of these with no bench kind (#3659), and the station graph hosts it.
    /// </summary>
    public EquatableArray<GearRecipe> Crafts { get; init; }
}

/// <summary>One definition to produce, attributed to a pawn or to the armory's colony weapon batch.</summary>
internal readonly record struct GearNeed(PawnId Pawn, GearReplacement Need);

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
    /// The pawns of the complete census whose loadout search spent its budget.
    /// </summary>
    public static IEnumerable<PawnId> ExhaustedGearSearches(GearObservation census) =>
        ReviewGear(census).Loadouts.Where(l => l.SearchBudgetExhausted).Select(l => l.Pawn);

    /// <summary>
    /// Matches the complete census used by equipment upkeep. A census with no pawns has none short of gear and owes no
    /// outfit prune (OutfitKeep settles nothing), so it is recovered; a pawn the loadout model refuses throws naming the
    /// cause. The loadouts are planned once per distinct set of pawn rows (<see cref="SharedColonyGear"/>).
    /// </summary>
    public static GearReview ReviewGear(GearObservation v)
    {
        v.Validate();
        if (v.Pawns.Count == 0)
        {
            return new GearReview { Loadouts = [], Recovered = true, Deficit = 0 };
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
            Recovered = missing == 0,
            Deficit = missing / n,
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
    /// candidate to wear), sorted and deduplicated. Empty unless the census is valid and not recovered,
    /// so the work planner enables a bench's work type exactly while the equipment Need is unmet and could
    /// raise a bill. A pawn the loadout model refuses throws, not an empty answer.
    /// </summary>
    public static IReadOnlyList<Resource> GearReplacementNeeds(GearObservation observed)
    {
        var review = ReviewGear(observed);
        if (review.Recovered)
        {
            return [];
        }
        var seen = new HashSet<Resource>();
        var @out = new List<Resource>();
        foreach (var p in GearLoadoutRules.ModeledGearPawns(observed, review.Loadouts))
        {
            if (!p.Deficit)
            {
                continue;
            }
            foreach (var n in p.Replacements)
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
    /// stored stock of any stuff.
    /// </summary>
    internal static (IReadOnlyList<GearBench> Benches, Dictionary<Resource, int> Demand) GearBenchDemand(
        IReadOnlyList<GearNeed> needs,
        GearObservation v,
        GearReview review,
        GearPlanningRequest r
    )
    {
        ValidateGearProduction(r.Benches);
        var benches = r.Benches.OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
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
        return (benches, demand);
    }

    /// <summary>
    /// Resolves one need of count items against the benches: Wait with the standing bill (Bill) that already makes
    /// it, Produce with the first funded recipe, else with the first funded catalog recipe no bench lists (CraftOf,
    /// #3659). Null when no recipe is funded, so the next need is judged.
    /// </summary>
    internal static GearMethod? ProduceNeed(GearNeed n, int count, IReadOnlyList<GearBench> benches, GearPlanningRequest r)
    {
        // Check every bench before adding a bill, including later-sorted player benches.
        foreach (var b in benches)
        {
            foreach (var bill in b.Bills)
            {
                if (bill.Spent || !ContainsResource(bill.Products, n.Need.Definition))
                {
                    continue;
                }
                if (bill.Active)
                {
                    return new GearMethod { Kind = GearMethodKind.WaitForExistingWork, Bill = bill };
                }
            }
        }
        foreach (var b in benches)
        {
            foreach (var recipe in b.Recipes.OrderBy(x => x.Definition, StringComparer.Ordinal))
            {
                if (!ContainsResource(recipe.Products, n.Need.Definition) || !recipe.Available || !recipe.AvailableOn)
                {
                    continue;
                }
                var batch = GearStockRules.GearBatchIngredients(recipe.Ingredients, count);
                if (GearFilter(batch, default, r.Items) is { } filter)
                {
                    return new GearMethod
                    {
                        Kind = GearMethodKind.Produce,
                        Count = count,
                        Bench = b.Id,
                        Recipe = recipe.Definition,
                        Filter = EquatableArray.From(filter),
                    };
                }
            }
        }
        // No standing bench works a recipe making the need: the catalog's recipe is ordered with no bench (#3659).
        return CraftOf(r.Crafts, benches, n.Need.Definition, count, r.Items);
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
            foreach (var recipe in b.Recipes)
            {
                hosted.Add(recipe.Definition);
            }
        }
        return crafts.Where(c => !hosted.Contains(c.Definition) && c.Available).OrderBy(c => c.Definition, StringComparer.Ordinal);
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
            foreach (var bill in b.Bills)
            {
                DomainException.Require(Products(bill.Products), "invalid bill products");
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in b.Recipes)
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
