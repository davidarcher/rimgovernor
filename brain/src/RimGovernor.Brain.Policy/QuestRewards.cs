using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Protocol.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Typed native benefit evidence. Choice -1 applies to every option.</summary>
public sealed record QuestReward
{
    public int Choice { get; init; }

    public IReadOnlyList<Amount> Items { get; init; } = [];

    public int Goodwill { get; init; }

    public int Psylink { get; init; }

    public int PermitPoints { get; init; }

    public IReadOnlyList<string> Permits { get; init; } = [];

    public string TitleDef { get; init; } = "";

    public string FactionId { get; init; } = "";
}

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

public static partial class PolicyFunctions
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
                var (rung, more) = NextClaim(royalty.Ladder, holding.Title);
                var fk = holding.Favor.TryGetValue(out var favor);
                var nk = rung.FavorNeeded.TryGetValue(out var needed);
                if (more && fk && nk && favor < needed)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Keeps native option indices and aggregates all rows for a choice. A multiple-part quest is refused
    /// because native cannot choose it.
    /// </summary>
    public static QuestRewardChoice SelectQuestReward(JoinerOffer offer, RoundsFacts f)
    {
        if (offer.RewardChoiceParts.TryGetValue(out var parts) && parts > 1)
        {
            return new QuestRewardChoice { Choice = -1, Reason = "reward_choices" };
        }
        var indices = new List<int>();
        if (offer.ChoiceCount == 0)
        {
            indices.Add(-1);
        }
        else
        {
            for (var i = 0; i < offer.ChoiceCount; i++)
            {
                indices.Add(i);
            }
        }
        var stockKnown = f.Resources.TryGetValue(out var stock);
        var need = new Dictionary<Resource, long>();
        if (stockKnown && f.ResourceNeeds is not null)
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
        if (f.ConstructionDeficit.TryGetValue(out var deficits))
        {
            foreach (var (def, count) in deficits)
            {
                need[def] = Math.Max(need.GetValueOrDefault(def), count);
            }
        }
        var titles = QuestWantsTitles(f);
        long hosted = 1;
        if (f.Colonists.TryGetValue(out var colonists))
        {
            hosted = Math.Max(1L, colonists);
        }
        var royaltyKnown = f.Royalty.TryGetValue(out var royalty);
        var best = new QuestRewardChoice();
        for (var ordinal = 0; ordinal < indices.Count; ordinal++)
        {
            var index = indices[ordinal];
            long titleFavor = 0;
            double needed = 0, benefits = 0;
            long favor = 0;
            foreach (var row in offer.Favor)
            {
                if (row.Choice == index || row.Choice == -1)
                {
                    favor += row.Favor;
                }
            }
            if (titles)
            {
                titleFavor = favor;
            }
            benefits += favor;
            var quantities = new Dictionary<Resource, long>();
            foreach (var row in offer.Rewards)
            {
                if (row.Choice != index && row.Choice != -1)
                {
                    continue;
                }
                foreach (var item in row.Items)
                {
                    quantities[item.Resource] = quantities.GetValueOrDefault(item.Resource) + item.Count;
                }
                benefits += Math.Max(0, row.Goodwill);
                benefits += (double)row.Psylink * PermitValue(PermitCategory.Psycast, true);
                benefits += (double)row.PermitPoints * PermitValue(PermitCategory.Aid, false);
                if (royaltyKnown)
                {
                    foreach (var name in row.Permits)
                    {
                        if (royalty!.Permits.TryGetValue(name, out var permit))
                        {
                            benefits += PermitValue(PermitCategoryOf(permit), royalty.Casters.Count > 0);
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
                if (f.FoodDays.TryGetValue(out var days) && days < JoinerFoodFloorDays(hosted))
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
