using RimGovernor.Brain.Domain;
using Pb = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record CoreSiteWork
{
    public string Kind { get; init; } = "";

    public string Reason { get; init; } = "";

    public Acquisition? Mine { get; init; }

    public RecoveryService? Help { get; init; }

    public PrisonerInteraction? Recruit { get; init; }
}

public static class QuestCoreSite
{
    /// <summary>
    /// Follows only the exact quest-linked site and native targets. A designation starts work; the
    /// mineable disappearing is its completion evidence.
    /// </summary>
    public static CoreSiteWork PlanCoreSiteWork(JoinerOffer offer, WorldSite site, RoundsFacts f, MapId currentMap)
    {
        var result = new CoreSiteWork();
        if (!offer.Profile.TryGetValue(out var profile)
            || (profile.Family != QuestFamily.Site && profile.Family != QuestFamily.BanditCamp
                && !(profile.Family == QuestFamily.Hack && offer.State == "EndedFailed") && !QuestFamiliesPolicy.OdysseyGroundSiteRoot(offer.ScriptDef))
            || (offer.State != "Ongoing" && offer.State != "EndedSuccess" && offer.State != "EndedFailed")
            || !site.QuestIds.Contains(offer.Quest))
        {
            return result;
        }
        result = result with { Kind = "wait" };
        if (!site.Map.TryGetValue(out var siteMap) || siteMap != currentMap)
        {
            return result with { Reason = "site_map_unavailable" };
        }
        if (!site.Threat.TryGetValue(out var threat) || threat)
        {
            return result with { Reason = "site_security" };
        }
        if (offer.State == "EndedFailed")
        {
            return result with { Kind = "return", Reason = "quest_failed" };
        }
        switch (offer.ScriptDef)
        {
            case "OpportunitySite_DownedRefugee" or "OpportunitySite_PrisonerWillingToJoin":
                return CoreSiteJoiner(offer, site, f, result);
            case "OpportunitySite_LongRangeMineralScannerLump" or "LongRangeMineralScannerLump":
                var targets = site.MiningTargets.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
                foreach (var target in targets)
                {
                    if (target.Map != currentMap)
                    {
                        return result with { Reason = "mining_target_map" };
                    }
                    if (!target.Designated.TryGetValue(out var designated))
                    {
                        return result with { Reason = "mining_designation_unknown" };
                    }
                    if (designated)
                    {
                        continue;
                    }
                    try
                    {
                        return result with { Kind = "mine", Mine = new Acquisition(target.Id, target.Def, target.Cell) };
                    }
                    catch (DomainException)
                    {
                        return result with { Reason = "mining_target_unknown" };
                    }
                }
                if (targets.Count > 0)
                {
                    return result with { Reason = "mining_work" };
                }
                break;
        }
        return result with { Kind = "return" };
    }

    private static CoreSiteWork CoreSiteJoiner(JoinerOffer offer, WorldSite site, RoundsFacts f, CoreSiteWork result)
    {
        var ids = new List<PawnId>();
        foreach (var objective in offer.Objectives)
        {
            if (objective.Kind == Pb.QuestObjectiveKind.RescuePawns)
            {
                ids.AddRange(objective.PawnIds);
            }
        }
        ids.Sort();
        if (!f.Population.TryGetValue(out var census))
        {
            throw new RequiredFactException("population census");
        }
        if (ids.Count == 0)
        {
            return result with { Reason = "rescue_target_unknown" };
        }
        var rows = census.Custody;
        foreach (var id in ids)
        {
            var row = rows.FirstOrDefault(r => r.Pawn == id);
            if (row is null)
            {
                return result with { Reason = "rescue_target_unavailable" };
            }
            if (row.Dead)
            {
                return result with { Kind = "return", Reason = "rescue_target_dead" };
            }
            if (row.Admitted)
            {
                continue;
            }
            if (row.WillJoinIfRescued)
            {
                if (site.Extraction.TryGetValue(out var extraction))
                {
                    foreach (var actor in extraction.Crew.OrderBy(a => a))
                    {
                        var performer = rows.FirstOrDefault(r => r.Pawn == actor);
                        if (performer is null || actor == id)
                        {
                            continue;
                        }
                        if (performer.Dead || performer.Downed || !performer.Admitted)
                        {
                            continue;
                        }
                        try
                        {
                            return result with { Kind = "help", Help = new RecoveryService((Domain.PawnId)actor, id.ToString(), RecoveryMethod.OfferHelp) };
                        }
                        catch (DomainException)
                        {
                        }
                    }
                }
                return result with { Reason = "rescue_actor_unavailable" };
            }
            foreach (var prisoner in census.Prisoners)
            {
                if (prisoner.Pawn != id)
                {
                    continue;
                }
                if (prisoner.Recruitable && prisoner.CurrentInteraction is PrisonerHeldInteraction.Settable { Mode: not PrisonerInteractionMode.Recruit })
                {
                    try
                    {
                        return result with { Kind = "recruit", Recruit = new PrisonerInteraction((Domain.PawnId)id, PrisonerInteractionMode.Recruit) };
                    }
                    catch (DomainException)
                    {
                    }
                }
            }
            return result with { Kind = "waitrescue", Reason = "rescue_work" };
        }
        return result with { Kind = "return" };
    }
}
