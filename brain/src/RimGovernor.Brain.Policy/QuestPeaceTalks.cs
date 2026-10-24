using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static class QuestPeaceTalksPolicy
{
    public static QuestSkipReason PeaceTalksAdmission(QuestState offer, RoundsFacts f)
    {
        if (!QuestExpedition.QuestExpeditionSite(offer, f, out var site))
        {
            return "site_unknown";
        }
        return PlanPeaceTalks(offer, site, f, f.AnimalUpkeep.Food, RoundsRules.DefaultRoundsPolicy()).Reason;
    }

    public static ExpeditionPlan PlanPeaceTalks(QuestState offer, ExpeditionSite site, RoundsFacts f, Fact<FoodSupply> food, RoundsPolicy p)
    {
        ExpeditionPlan Fail(QuestSkipReason reason) => new() { Quest = offer.Id, Site = site.Id, Reason = reason };

        if (!f.QuestSites.TryGetValue(out var sites))
        {
            return Fail("diplomacy_unknown");
        }
        Pb.QuestPeaceTalks? risk = null;
        foreach (var row in sites)
        {
            if (row.Id == site.Id)
            {
                risk = row.PeaceTalks;
                break;
            }
        }
        if (risk is null)
        {
            return Fail("diplomacy_unknown");
        }
        var downside = PeaceTalksDownside(risk);
        if (!downside.IsNone)
        {
            return Fail(downside);
        }
        if (!f.QuestDeparturePawns.TryGetValue(out var departures))
        {
            return Fail("capacity_unknown");
        }
        // A colonist whose NegotiationAbility is disabled is Incapable: that pawn is no diplomat.
        var pawns = new List<PeaceTalksPawn>(departures.Count);
        foreach (var row in departures)
        {
            if (row.Negotiation is Pb.QuestNegotiation.Able able)
            {
                pawns.Add(new PeaceTalksPawn { Id = row.Id, Social = able.Social, Leader = row.FactionLeader });
            }
        }
        var (diplomat, reason) = PeaceTalksNegotiator(offer, f, pawns, risk.IdeologyActive);
        if (!reason.IsNone)
        {
            return Fail(reason);
        }
        return QuestExpedition.PlanExpeditionCrew(offer, site, f, food, p, [diplomat]);
    }

    public static QuestSkipReason PeaceTalksDownside(Pb.QuestPeaceTalks risk)
    {
        if (risk.WorstGoodwillLoss < 0 || risk.BestGoodwillGain < 0)
        {
            return "diplomacy_unknown";
        }
        if (risk.WorstGoodwillLoss > risk.BestGoodwillGain)
        {
            return "goodwill_downside";
        }
        return "";
    }

    /// <summary>
    /// Negotiator staffing first preserves ordinary home work/defense. Ideology's leader is preferred when
    /// eligible; otherwise Social ranks candidates. Vanilla still chooses its diplomat by NegotiationAbility
    /// when the caravan arrives.
    /// </summary>
    public static (PawnId Pawn, QuestSkipReason Reason) PeaceTalksNegotiator(QuestState offer, RoundsFacts f, IReadOnlyList<PeaceTalksPawn> pawns, bool ideo)
    {
        var rows = pawns.ToList();
        if (rows.Count == 0)
        {
            return (default, "diplomacy_unknown");
        }
        rows.Sort((x, y) =>
        {
            if (ideo && x.Leader != y.Leader)
            {
                return x.Leader ? -1 : 1;
            }
            if (x.Social != y.Social)
            {
                return x.Social > y.Social ? -1 : 1;
            }
            return x.Id.CompareTo(y.Id);
        });
        var sk = f.QuestSparePawns.TryGetValue(out var spare);
        var dk = f.QuestDeparturePawns.TryGetValue(out var departures);
        var ck = f.DefenseCapacity.TryGetValue(out var capacity);
        var rk = f.RaidPoints.TryGetValue(out var raid);
        if (!sk || !dk || !ck || !rk || !TradePolicyRules.Finite(capacity) || !TradePolicyRules.Finite(raid) || capacity < 0 || raid < 0)
        {
            return (default, "capacity_unknown");
        }
        foreach (var pawn in rows)
        {
            if (!spare!.Contains(pawn.Id))
            {
                continue;
            }
            var trial = f with { QuestSparePawns = Known(EquatableArray.From(new[] { pawn.Id })) };
            var (crew, reason) = QuestDeparture.DepartureSquadFor(offer.Id, QuestFamily.PawnLend, 1, trial, false, false);
            if (!reason.IsNone || crew.Count != 1)
            {
                continue;
            }
            foreach (var departure in departures!)
            {
                if (departure.Id != pawn.Id)
                {
                    continue;
                }
                if (capacity - departure.DefensePoints < raid)
                {
                    continue;
                }
                return (crew[0], "");
            }
        }
        return (default, "negotiator_capacity");
    }
}
