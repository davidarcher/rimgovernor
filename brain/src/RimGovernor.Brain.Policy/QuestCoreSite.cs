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
    public static CoreSiteWork PlanCoreSiteWork(QuestState offer, WorldSite site, RoundsFacts f, MapId currentMap)
    {
        var result = new CoreSiteWork();
        if (f.RootOf(offer) is not QuestRoot.Classified { Profile: var profile }
            || (profile.Family != QuestFamily.Site && profile.Family != QuestFamily.BanditCamp
                && !(profile.Family == QuestFamily.Hack && offer.State == QuestStatus.EndedFailed) && !QuestFamiliesPolicy.OdysseyGroundSiteRoot(offer.ScriptDef))
            || (offer.State != QuestStatus.Ongoing && offer.State != QuestStatus.EndedSuccess && offer.State != QuestStatus.EndedFailed)
            || !site.QuestIds.Contains(offer.Id))
        {
            return result;
        }
        result = result with { Kind = "wait" };
        if (site.Place is not Pb.SitePlace.Loaded { Map: var map } || map.Id != currentMap)
        {
            return result with { Reason = "site_map_unavailable" };
        }
        if (map.Threat is Pb.SiteThreat.Active or Pb.SiteThreat.Dormant)
        {
            return result with { Reason = "site_security" };
        }
        if (offer.State == QuestStatus.EndedFailed)
        {
            return result with { Kind = "return", Reason = "quest_failed" };
        }
        switch (offer.ScriptDef)
        {
            case "OpportunitySite_DownedRefugee" or "OpportunitySite_PrisonerWillingToJoin":
                return CoreSiteJoiner(offer, map, f, result);
            case "OpportunitySite_LongRangeMineralScannerLump" or "LongRangeMineralScannerLump":
                var targets = map.MiningTargets.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
                foreach (var target in targets)
                {
                    if (target.Designated)
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

    private static CoreSiteWork CoreSiteJoiner(QuestState offer, Pb.SiteMap map, RoundsFacts f, CoreSiteWork result)
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
        if (ids.Count == 0)
        {
            return result with { Reason = "rescue_target_unknown" };
        }
        var rows = f.Population.Custody;
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
                if (map.Extraction is Pb.QuestSiteExtraction.Crew { Value: var extraction })
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
            foreach (var prisoner in f.Population.Prisoners)
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
