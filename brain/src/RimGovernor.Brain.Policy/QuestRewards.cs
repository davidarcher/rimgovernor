using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Gives pending titles priority, then observed shortages, then useful benefits. Scores compare only quest
/// rewards, never colony purchases.
/// </summary>
public readonly record struct QuestRewardValue(long TitleFavor, double Needed, double Benefits)
{
    public bool BetterThan(QuestRewardValue other)
    {
        if (TitleFavor != other.TitleFavor)
        {
            return TitleFavor > other.TitleFavor;
        }
        if (Needed != other.Needed)
        {
            return Needed > other.Needed;
        }
        return Benefits > other.Benefits;
    }
}

public sealed record QuestRewardChoice
{
    public int Choice { get; init; }

    public QuestRewardValue Value { get; init; }

    public QuestSkipReason Reason { get; init; }
}

public static class QuestRewards
{
    private static bool QuestWantsTitles(RoundsFacts f)
    {
        if (!f.Royalty.TryGetValue(out var royalty))
        {
            return false;
        }
        foreach (var holdings in royalty.Holders.Values)
        {
            foreach (var holding in holdings)
            {
                var (rung, more) = TitleClaimRules.NextClaim(royalty.Ladder, holding.Title);
                var nk = rung.FavorNeeded.TryGetValue(out var needed);
                if (more && nk && holding.Favor < needed)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Scores each native option (its index in Rewards) with the favor the quest grants whatever is taken; a quest
    /// without a reward choice is scored on that favor alone as choice -1. A multiple-part quest is refused because
    /// native cannot choose it.
    /// </summary>
    public static QuestRewardChoice SelectQuestReward(JoinerOffer offer, RoundsFacts f)
    {
        if (offer.RewardChoiceParts > 1)
        {
            return new QuestRewardChoice { Choice = -1, Reason = "reward_choices" };
        }
        var indices = new List<int>();
        if (offer.Rewards.Count == 0)
        {
            indices.Add(-1);
        }
        else
        {
            for (var i = 0; i < offer.Rewards.Count; i++)
            {
                indices.Add(i);
            }
        }
        var stockKnown = f.Resources.TryGetValue(out var stock);
        var need = new Dictionary<Resource, long>();
        if (stockKnown)
        {
            foreach (var (def, floor) in f.ResourceNeeds)
            {
                long have = 0;
                foreach (var amount in stock)
                {
                    if (amount.Resource == def)
                    {
                        have += amount.Count;
                    }
                }
                need[def] = Math.Max(0L, floor - have);
            }
        }
        if (f.CurrentConstruction is { Deficit: var deficits })
        {
            foreach (var (def, count) in deficits)
            {
                need[def] = Math.Max(need.GetValueOrDefault(def), count);
            }
        }
        var titles = QuestWantsTitles(f);
        var hosted = Math.Max(1L, f.Colonists);
        var royaltyKnown = f.Royalty.TryGetValue(out var royalty);
        var best = new QuestRewardChoice();
        for (var ordinal = 0; ordinal < indices.Count; ordinal++)
        {
            var index = indices[ordinal];
            long titleFavor = 0;
            double needed = 0, benefits = 0;
            long favor = offer.Favor + (index >= 0 ? offer.Rewards[index].Favor : 0);
            if (titles)
            {
                titleFavor = favor;
            }
            benefits += favor;
            var quantities = new Dictionary<Resource, long>();
            // The option's own row; choice -1 belongs to a quest with no option rows.
            foreach (var row in offer.Rewards.Skip(Math.Max(index, 0)).Take(1))
            {
                foreach (var item in row.Items)
                {
                    var resource = new Resource(item.DefName);
                    quantities[resource] = quantities.GetValueOrDefault(resource) + item.Units;
                }
                benefits += Math.Max(0, row.Goodwill);
                benefits += (double)row.Psylink * PermitChoiceRules.PermitValue(PermitCategory.Psycast, true);
                benefits += (double)row.PermitPoints * PermitChoiceRules.PermitValue(PermitCategory.Aid, false);
                if (royaltyKnown)
                {
                    foreach (var name in row.Permits)
                    {
                        if (royalty!.Permits.TryGetValue(name, out var permit))
                        {
                            benefits += PermitChoiceRules.PermitValue(PermitChoiceRules.PermitCategoryOf(permit), royalty.Casters.Count > 0);
                        }
                    }
                }
            }
            var defs = quantities.Keys.ToList();
            defs.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
            foreach (var def in defs)
            {
                var count = quantities[def];
                var price = f.Items.Market.GetValueOrDefault(def);
                if (price <= 0)
                {
                    price = 1;
                }
                benefits += count * price;
                needed += Math.Min(count, need.GetValueOrDefault(def)) * price;
                if (f.FoodDays.TryGetValue(out var days) && days < PopulationRules.JoinerFoodFloorDays(hosted))
                {
                    needed += count * f.Items.Nutrition.GetValueOrDefault(def);
                }
            }
            var value = new QuestRewardValue(titleFavor, needed, benefits);
            if (ordinal == 0 || value.BetterThan(best.Value))
            {
                best = new QuestRewardChoice { Choice = index, Value = value };
            }
        }
        return best;
    }
}
