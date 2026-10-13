using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record DecreeRecipe
{
    public string Bench { get; init; } = "";

    public string Recipe { get; init; } = "";

    public Resource Product { get; init; }

    public long Units { get; init; }

    public Fact<bool> Available { get; init; }

    public Fact<IReadOnlyList<IReadOnlyList<Amount>>> Ingredients { get; init; }

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
/// What one Round's produce-decree declaration reads. Now is the review's tick and Recipes the bench
/// census's recipes making an objective's item.
/// </summary>
public sealed record DecreeOrderRequest
{
    public Fact<EquatableArray<JoinerOffer>> Offers { get; init; }

    public long Now { get; init; }

    public Fact<EquatableArray<Amount>> Stock { get; init; }

    public Func<QuestObjective, IReadOnlyList<DecreeRecipe>> Recipes { get; init; } = _ => [];
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Identifies outstanding native progress; inventory is never progress for a production or harvest decree.
    /// </summary>
    public static (QuestObjective? Objective, long Remaining, bool Owed) DecreeObjective(JoinerOffer offer)
    {
        if (offer.State != "Ongoing" || !offer.Profile.TryGetValue(out var profile))
        {
            return (null, 0, false);
        }
        Pb.QuestObjectiveKind kind;
        switch (profile.Family)
        {
            case QuestFamily.DecreeProduce:
                kind = Pb.QuestObjectiveKind.ProduceItem;
                break;
            case QuestFamily.DecreeHarvest:
                kind = Pb.QuestObjectiveKind.HarvestPlant;
                break;
            case QuestFamily.DecreeHunt:
                kind = Pb.QuestObjectiveKind.KillAnimals;
                break;
            default:
                return (null, 0, false);
        }
        foreach (var objective in offer.Objectives)
        {
            var ck = objective.Count.TryGetValue(out var count);
            var pk = objective.Produced.TryGetValue(out var produced);
            var ak = objective.Active.TryGetValue(out var active);
            if (objective.Kind == kind && ck && pk && ak && active && count > produced && objective.Def.Length != 0)
            {
                return (objective, count - produced, true);
            }
        }
        return (null, 0, false);
    }

    public static bool DecreeDeficit(RoundsFacts f)
    {
        if (!f.QuestOffers.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var offer in rows)
        {
            if (DecreeObjective(offer).Owed)
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
    public static (ProductionBill? Bill, string Reason) PlanDecreeProduction<TStock>(QuestObjective objective, long remaining, IReadOnlyList<DecreeRecipe> recipes, Fact<TStock> stock)
        where TStock : IReadOnlyList<Amount>
    {
        if (remaining <= 0)
        {
            return (null, "complete");
        }
        if (!stock.TryGetValue(out var rows))
        {
            return (null, "ingredients_unknown");
        }
        var available = new Dictionary<Resource, long>();
        foreach (var row in rows)
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
            if (!recipe.Available.TryGetValue(out var usable) || !usable)
            {
                continue;
            }
            if (!recipe.Ingredients.TryGetValue(out var groups))
            {
                continue;
            }
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
            try
            {
                return (new ProductionBill(recipe.Bench, recipe.Recipe, BillMode.GearBatch, (int)iterations, names), "");
            }
            catch (DomainException)
            {
            }
        }
        return (null, "production_infeasible");
    }

    public static (FieldPlan? Plan, string Reason) PlanDecreeHarvest(QuestObjective objective, long remaining, long standing, IReadOnlyList<CropChoice> choices, CropClimate climate, FarmSiteRequest site, Fact<int> skill)
    {
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
                exact.Add(crop with { Harvests = Known(new Resource(crop.Name)), UnitsPerCell = Known(1.0) });
            }
        }
        var plan = PlanFieldByResource(new ResourceFieldRequest
        {
            Resource = new Resource(objective.Def),
            Deficit = Known((double)(remaining - standing)),
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

    public static (IReadOnlyList<AcquisitionSource> Prey, string Reason) PlanDecreeHunt<TSources>(QuestObjective objective, long remaining, Fact<bool> allowed, Fact<TSources> sources)
        where TSources : IReadOnlyList<AcquisitionSource>
    {
        if (!allowed.TryGetValue(out var violent) || !violent)
        {
            return ([], "violent_quests_off");
        }
        if (!sources.TryGetValue(out var rows))
        {
            return ([], "prey_unknown");
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
        return (prey.OrderBy(p => p.ID, StringComparer.Ordinal).Take((int)(remaining - pending)).ToList(), "");
    }

    /// <summary>
    /// MaintainPopulation's wanted production orders: for every accepted produce-item decree of the
    /// player's faction that is still open, the batch PlanDecreeProduction funds from the crafted count
    /// left, or the bill that already makes it, declared as it stands. A decree the quest has completed,
    /// expired or that no recipe can fund declares nothing, so the ledger removes its bill. Abstain while
    /// the offers, the asker or the stock are unread.
    /// </summary>
    public static Declared DeclareDecreeOrders(DecreeOrderRequest r)
    {
        if (!r.Offers.TryGetValue(out var offers))
        {
            return Abstaining(Cause.UnreadQuestOffers);
        }
        var output = new Declared();
        foreach (var offer in offers.OrderBy(o => o.Quest))
        {
            var (objective, remaining, owed) = DecreeObjective(offer);
            if (!owed || objective!.Kind != Pb.QuestObjectiveKind.ProduceItem)
            {
                continue;
            }
            if (!offer.AskerFactionPlayer.TryGetValue(out var player))
            {
                output.Unread(Cause.UnreadQuestAsker);
                continue;
            }
            var hasDeadline = objective.DeadlineTicks.TryGetValue(out var deadline);
            if (!player || (hasDeadline && deadline <= r.Now))
            {
                continue;
            }
            var recipes = r.Recipes(objective);
            var (bill, why) = PlanDecreeProduction(objective, remaining, recipes, r.Stock);
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
                case "ingredients_unknown":
                    output.Unread(Cause.UnreadRecipes);
                    break;
            }
        }
        return output;
    }
}
