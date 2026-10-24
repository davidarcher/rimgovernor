namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class JoinersRounds
{
    /// <summary>Works a quest site on the current map: survey relief, mining, help, recruiting or the return trip.</summary>
    internal (Verdict Verdict, bool Handled) AdmitSiteWork(NeedWork work, RoundsReading read, DateTimeOffset started, CancellationToken call)
    {
        var f = read.Projection.Facts;
        if (!f.QuestSites.TryGetValue(out var siteRows) || !f.QuestOffers.TryGetValue(out var offers))
        {
            return (default, false);
        }

        var sites = siteRows.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        foreach (var site in sites)
        {
            if (site.Place is not PbObservations.SitePlace.Loaded loaded || loaded.Map.Id != read.Projection.Identity.Map)
            {
                continue;
            }

            foreach (var offer in offers)
            {
                if (!site.QuestIds.Contains(offer.Quest))
                {
                    continue;
                }

                if (!offer.Profile.TryGetValue(out var profile) || profile.NeverAct)
                {
                    continue;
                }

                var hackTargets = offer.Objectives.Any(o => o.HackTargets.Count > 0);
                if (hackTargets && offer.State != "EndedFailed" && !QuestHackPolicy.QuestHackComplete(offer))
                {
                    continue;
                }

                var id = PlanIds.MintPlanId();
                var actionId = new ActionId(id.Value + "-0");
                DomainAction? action = null;
                List<PawnId> crew = [];
                var kind = "";
                var target = site.Id;
                var returning = false;
                if (offer.ScriptDef == "SurveySite")
                {
                    var step = QuestSurvey.SurveyWork(offer, site, f, f.AnimalUpkeep.Food, reviewer.Policy);
                    if (step.Reason != "")
                    {
                        reviewer.LogQuestSkip(new QuestSkip(offer.Quest, "", step.Reason, ""), call);
                    }

                    if (step.Departure is not null)
                    {
                        action = DomainAction.NewCaravanDepartureAction(actionId, step.Departure);
                        crew = step.Departure.Crew.ToPolicyIds();
                        kind = "relief";
                    }
                    else if (step.Return)
                    {
                        returning = true;
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    var step = QuestCoreSite.PlanCoreSiteWork(offer, site, f, loaded.Map.Id);
                    if (step.Reason != "")
                    {
                        reviewer.LogQuestSkip(new QuestSkip(offer.Quest, "", new QuestSkipReason(step.Reason), ""), call);
                    }

                    switch (step.Kind)
                    {
                        case "mine":
                            var mine = step.Mine ?? throw new ControlException("questSiteWork: mine work without an acquisition");
                            action = DomainAction.NewMineAcquisitionAction(actionId, mine);
                            kind = "mine";
                            target = mine.Thing;
                            break;
                        case "help":
                            return (Verdict.AwaitingPlan("unsupported_forecast_site_help",
                                "Site help requires shared forecast worker, duration and outcome admission."), true);
                        case "recruit":
                            var recruit = step.Recruit ?? throw new ControlException("questSiteWork: recruit work without an interaction");
                            action = DomainAction.NewPrisonerInteractionAction(actionId, recruit);
                            kind = "recruit";
                            target = recruit.Pawn.Value;
                            break;
                        case "return":
                            returning = true;
                            break;
                        default:
                            if (QuestHackPolicy.QuestHackComplete(offer))
                            {
                                returning = true;
                            }
                            else
                            {
                                continue;
                            }

                            break;
                    }
                }

                if (returning)
                {
                    var step = QuestReturn.PlanSiteReturn(site);
                    if (step.Departure is null)
                    {
                        if (step.Reason != "")
                        {
                            reviewer.LogQuestSkip(new QuestSkip(offer.Quest, "", step.Reason, ""), call);
                        }

                        continue;
                    }

                    action = DomainAction.NewCaravanDepartureAction(actionId, step.Departure);
                    crew = step.Departure.Crew.ToPolicyIds();
                    kind = "return";
                }

                if (action?.CaravanDeparture is { Mode: CaravanDepartureMode.Formation })
                    return (Verdict.AwaitingPlan("unsupported_forecast_caravan_formation",
                        "Caravan packing and departure require shared forecast labor admission."), true);
                if (crew.Count > 0 && !Claim(work, crew, "quest:" + offer.Quest))
                {
                    return (Verdict.WaitFor(Cause.Claim, "quest_site"), true);
                }

                var plan = new PlanSpec(id, new PlanRevision(1), [action!]);
                var method = new MethodId($"quest-site-{offer.Quest}-{site.Id}-{kind}-{target}");
                return (Commit(work, method, plan, started, "admitSiteWork", call), true);
            }
        }

        return (default, false);
    }
}
