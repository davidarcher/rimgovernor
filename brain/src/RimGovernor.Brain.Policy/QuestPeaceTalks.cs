using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public static class QuestPeaceTalksPolicy
{
    public static QuestSkipReason PeaceTalksAdmission(JoinerOffer offer, RoundsFacts f)
    {
        if (!QuestExpedition.QuestExpeditionSite(offer, f, out var site))
        {
            return "site_unknown";
        }
        return PlanPeaceTalks(offer, site, f, f.AnimalUpkeep.Food, RoundsRules.DefaultRoundsPolicy()).Reason;
    }

    public static ExpeditionPlan PlanPeaceTalks(JoinerOffer offer, ExpeditionSite site, RoundsFacts f, Fact<FoodSupply> food, RoundsPolicy p)
    {
        ExpeditionPlan Fail(QuestSkipReason reason) => new() { Quest = offer.Quest, Site = site.Id, Reason = reason };

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
        var pawns = new List<PeaceTalksPawn>(departures.Count);
        foreach (var row in departures)
        {
            pawns.Add(new PeaceTalksPawn { Id = row.Id, Social = row.SocialLevel, Leader = row.FactionLeader });
        }
        var (diplomat, reason) = PeaceTalksNegotiator(offer, f, Known(EquatableArray.From(pawns)), risk.IdeologyActive);
        if (!reason.IsNone)
        {
            return Fail(reason);
        }
        return QuestExpedition.PlanExpeditionCrew(offer, site, f, food, p, [diplomat]);
    }

    public static QuestSkipReason PeaceTalksDownside(Pb.QuestPeaceTalks risk)
    {
        if (risk.WorstGoodwillLoss is not { } loss || risk.BestGoodwillGain is not { } gain || loss < 0 || gain < 0)
        {
            return "diplomacy_unknown";
        }
        if (loss > gain)
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
    public static (PawnId Pawn, QuestSkipReason Reason) PeaceTalksNegotiator(JoinerOffer offer, RoundsFacts f, Fact<EquatableArray<PeaceTalksPawn>> pawns, bool? ideology)
    {
        var known = pawns.TryGetValue(out var all);
        if (!known || ideology is not { } ideo)
        {
            return (default, "diplomacy_unknown");
        }
        // Native omits the diplomacy skill when NegotiationAbility is disabled. Unavailable candidates cannot
        // displace a known capable diplomat.
        var rows = all!.Where(pawn =>
        {
            var socialKnown = pawn.Social.TryGetValue(out var social);
            var leaderKnown = pawn.Leader.TryGetValue(out _);
            return !(!socialKnown || social < 0 || (ideo && !leaderKnown));
        }).ToList();
        if (rows.Count == 0)
        {
            return (default, "diplomacy_unknown");
        }
        rows.Sort((x, y) =>
        {
            x.Leader.TryGetValue(out var a);
            y.Leader.TryGetValue(out var b);
            if (ideo && a != b)
            {
                return a ? -1 : 1;
            }
            x.Social.TryGetValue(out var sa);
            y.Social.TryGetValue(out var sb);
            if (sa != sb)
            {
                return sa > sb ? -1 : 1;
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
            var mission = offer with
            {
                Profile = Known(new QuestProfile { Family = QuestFamily.PawnLend }),
                Objectives = [new QuestObjective { Kind = Pb.QuestObjectiveKind.LoadPawns, Count = Known(1L) }],
            };
            var (crew, reason) = QuestDeparture.DepartureSquadOf(mission, trial, false);
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
                if (!departure.DefensePoints.TryGetValue(out var strength) || !TradePolicyRules.Finite(strength) || strength < 0)
                {
                    return (default, "defense_unknown");
                }
                if (capacity - strength < raid)
                {
                    continue;
                }
                return (crew[0], "");
            }
        }
        return (default, "negotiator_capacity");
    }
}
