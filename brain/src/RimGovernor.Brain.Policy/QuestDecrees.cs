using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

public sealed record DecreeRecipe
{
    public string Bench { get; init; } = "";

    public string Recipe { get; init; } = "";

    public Resource Product { get; init; }

    public long Units { get; init; }

    /// <summary>The bench can run the recipe now and no other-mode bill of it stands.</summary>
    public bool Available { get; init; }

    public required EquatableArray<EquatableArray<Amount>> Ingredients { get; init; }

    /// <summary>The recipe alternatives that determine its output's stuff.</summary>
    public IReadOnlyDictionary<Resource, bool> Stuff { get; init; } = new Dictionary<Resource, bool>();

    /// <summary>A finite bill with the required ingredient filter is already tracked.</summary>
    public bool Existing { get; init; }

    /// <summary>The specs of those bills as the bench reads them back.</summary>
    public IReadOnlyList<OrderSpec> Standing { get; init; } = [];

    /// <summary>The bench definition the bill is declared against.</summary>
    public string BenchKind { get; init; } = "";
}

/// <summary>
/// What one Round's produce-decree declaration reads. Facts carries the quest census, its roots and the stock, Now is
/// the review's tick and Recipes the bench census's recipes making a decree's item.
/// </summary>
public sealed record DecreeOrderRequest
{
    public required ReviewFacts Facts { get; init; }

    public long Now { get; init; }

    public Func<DecreeDue, IReadOnlyList<DecreeRecipe>> Recipes { get; init; } = _ => [];
}

/// <summary>
/// An accepted decree's open objective: the objective itself, the item (Def, made of Stuff, "" for any) it counts and
/// how many it still wants.
/// </summary>
public sealed record DecreeDue
{
    public required QuestObjective Objective { get; init; }

    public required string Def { get; init; }

    public required string Stuff { get; init; }

    public required long Remaining { get; init; }
}

public static class QuestDecrees
{
    private static readonly Dictionary<QuestFamily, RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind> DecreeKinds = new()
    {
        [QuestFamily.DecreeProduce] = RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.ProduceItem,
        [QuestFamily.DecreeHarvest] = RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.HarvestPlant,
        [QuestFamily.DecreeHunt] = RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.KillAnimals,
    };

    /// <summary>
    /// Identifies outstanding native progress; inventory is never progress for a production or harvest decree.
    /// </summary>
    public static DecreeDue? DecreeObjective(QuestState offer, ReviewFacts f)
    {
        if (offer.State != QuestStatus.Ongoing || f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile })
        {
            return null;
        }
        if (!DecreeKinds.TryGetValue(profile.Family, out var kind))
        {
            return null;
        }
        foreach (var objective in offer.Objectives)
        {
            if (objective.Kind == kind && objective.Active && objective.Goal is RimGovernor.Brain.GameFacts.Observations.QuestObjectiveGoal.Things things && things.Count > things.Done)
            {
                return new DecreeDue { Objective = objective, Def = things.Def, Stuff = things.Stuff ?? "", Remaining = things.Count - things.Done };
            }
        }
        return null;
    }

    public static bool DecreeDeficit(ReviewFacts f)
    {
        foreach (var offer in f.QuestOffers)
        {
            if (DecreeObjective(offer, f) is not null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Funds a repeat-count batch from the outstanding crafted count. A stock-target bill would stop on
    /// inventory that the quest never counts.
    /// </summary>
    public static (ProductionBill? Bill, string Reason) PlanDecreeProduction(DecreeDue objective, IReadOnlyList<DecreeRecipe> recipes, IReadOnlyList<Amount> stock)
    {
        var remaining = objective.Remaining;
        if (remaining <= 0)
        {
            return (null, "complete");
        }
        var available = new Dictionary<Resource, long>();
        foreach (var row in stock)
        {
            available[row.Resource] = row.Count;
        }
        var ordered = recipes
            .OrderBy(r => r.Bench, StringComparer.Ordinal)
            .ThenBy(r => r.Recipe, StringComparer.Ordinal);
        foreach (var recipe in ordered)
        {
            if (recipe.Product != new Resource(objective.Def) || recipe.Units <= 0)
            {
                continue;
            }
            if (recipe.Existing)
            {
                return (null, "existing_work");
            }
            if (!recipe.Available)
            {
                continue;
            }
            var groups = recipe.Ingredients;
            var iterations = (remaining + recipe.Units - 1) / recipe.Units;
            if (iterations > 10000)
            {
                return (null, "batch_too_large");
            }
            var filter = new List<string>();
            var spent = new Dictionary<Resource, long>();
            var funded = true;
            var stuffFound = objective.Stuff.Length == 0;
            foreach (var group in groups)
            {
                Amount? chosen = null;
                foreach (var alternative in group)
                {
                    if (recipe.Stuff.GetValueOrDefault(alternative.Resource) && objective.Stuff.Length != 0 && alternative.Resource != new Resource(objective.Stuff))
                    {
                        continue;
                    }
                    var need = alternative.Count * iterations;
                    if (alternative.Count <= 0 || available.GetValueOrDefault(alternative.Resource) - spent.GetValueOrDefault(alternative.Resource) < need)
                    {
                        continue;
                    }
                    if (chosen is null || string.CompareOrdinal(alternative.Resource.Value, chosen.Resource.Value) < 0)
                    {
                        chosen = alternative;
                    }
                }
                if (chosen is null)
                {
                    funded = false;
                    break;
                }
                spent[chosen.Resource] = spent.GetValueOrDefault(chosen.Resource) + (chosen.Count * iterations);
                filter.Add(chosen.Resource.Value);
                if (chosen.Resource.Value == objective.Stuff && recipe.Stuff.GetValueOrDefault(chosen.Resource))
                {
                    stuffFound = true;
                }
            }
            if (!funded || !stuffFound)
            {
                continue;
            }
            var names = filter.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            return (new ProductionBill(recipe.Bench, recipe.Recipe, BillMode.GearBatch, (int)iterations, names), "");
        }
        return (null, "production_infeasible");
    }

    public static (FieldPlan? Plan, string Reason) PlanDecreeHarvest(DecreeDue objective, long standing, IReadOnlyList<CropChoice> choices, CropClimate climate, FarmSiteRequest site, int skill)
    {
        var remaining = objective.Remaining;
        if (remaining <= 0)
        {
            return (null, "complete");
        }
        if (standing >= remaining)
        {
            return (null, "existing_work");
        }
        var exact = new List<CropChoice>();
        foreach (var crop in choices)
        {
            if (crop.Name == objective.Def)
            {
                exact.Add(crop with { Harvests = new Resource(crop.Name), UnitsPerCell = 1.0});
            }
        }
        var plan = FieldResourceRules.PlanFieldByResource(new ResourceFieldRequest
        {
            Resource = new Resource(objective.Def),
            Deficit = (double)(remaining - standing),
            Choices = exact,
            Climate = climate,
            Site = site,
            GrowerSkill = skill,
        });
        if (plan is null)
        {
            return (null, "plant_infeasible");
        }
        return (plan, "");
    }

    public static (IReadOnlyList<AcquisitionSource> Prey, string Reason) PlanDecreeHunt(DecreeDue objective, bool allowed, EquatableArray<AcquisitionSource> rows)
    {
        var remaining = objective.Remaining;
        if (!allowed)
        {
            return ([], "violent_quests_off");
        }
        long pending = 0;
        var prey = new List<AcquisitionSource>();
        foreach (var row in rows)
        {
            if (row.Hunt && row.Definition == objective.Def)
            {
                if (row.Designated || row.Taken)
                {
                    pending++;
                }
                else
                {
                    prey.Add(row);
                }
            }
        }
        if (pending >= remaining)
        {
            return ([], "existing_work");
        }
        if (prey.Count + pending < remaining)
        {
            return ([], "species_absent");
        }
        return (prey.OrderBy(p => p.Id, StringComparer.Ordinal).Take((int)(remaining - pending)).ToList(), "");
    }

    /// <summary>
    /// joiners/decrees' wanted production orders: for every accepted produce-item decree of the
    /// player's faction that is still open, the batch PlanDecreeProduction funds from the crafted count
    /// left, or the bill that already makes it, declared as it stands. A decree the quest has completed,
    /// expired or that no recipe can fund declares nothing, so the ledger removes its bill. Abstain while
    /// the offers, the asker or the stock are unread.
    /// </summary>
    public static Declared DeclareDecreeOrders(DecreeOrderRequest r)
    {
        var output = new Declared();
        foreach (var offer in r.Facts.QuestOffers.OrderBy(o => o.Id))
        {
            if (DecreeObjective(offer, r.Facts) is not { } objective || objective.Objective.Kind != RimGovernor.Brain.GameFacts.Observations.QuestObjectiveKind.ProduceItem)
            {
                continue;
            }
            if (offer.Asker != RimGovernor.Brain.GameFacts.Observations.DecreeAsker.PlayerFaction
                || (objective.Objective.Timing is RimGovernor.Brain.GameFacts.Observations.QuestObjectiveTiming.Deadline due && due.Tick <= r.Now))
            {
                continue;
            }
            var recipes = r.Recipes(objective);
            var (bill, why) = PlanDecreeProduction(objective, recipes, r.Facts.ObservedStock());
            switch (why)
            {
                case "":
                    var spec = new OrderSpec
                    {
                        Recipe = bill!.Recipe,
                        Ingredients = bill.Ingredients,
                        Mode = BillMode.GearBatch,
                        Target = bill.Target,
                    };
                    foreach (var recipe in recipes)
                    {
                        if (recipe.Bench == bill.Bench && recipe.Recipe == bill.Recipe)
                        {
                            spec = spec with { BenchKind = recipe.BenchKind };
                        }
                    }
                    output.Orders.Add(spec);
                    break;
                case "existing_work":
                    foreach (var recipe in recipes)
                    {
                        if (recipe.Product == new Resource(objective.Def) && recipe.Existing)
                        {
                            output.Orders.AddRange(recipe.Standing);
                        }
                    }
                    break;
            }
        }
        return output;
    }
}
