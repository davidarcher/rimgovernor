namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Works a quest site on the current map: survey relief, mining, help, recruiting or the return trip.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitSiteWork(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started)
    {
        var f = read.Projection.Facts;
        if (!f.QuestSites.TryGetValue(out var siteRows) || !f.QuestOffers.TryGetValue(out var offers))
        {
            return (default, false);
        }

        var sites = siteRows.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        foreach (var site in sites)
        {
            if (!site.Map.TryGetValue(out var current) || current != read.Projection.Identity.Map)
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
                if (hackTargets && offer.State != "EndedFailed" && !PolicyFunctions.QuestHackComplete(offer))
                {
                    continue;
                }

                var id = PlanIds.MintPlanId();
                var actionId = new ActionId(id.Value + "-0");
                DomainAction? action = null;
                IReadOnlyList<PawnId> crew = [];
                var kind = "";
                var target = site.Id;
                var returning = false;
                if (offer.ScriptDef == "SurveySite")
                {
                    var work = PolicyFunctions.SurveyWork(offer, site, f, f.AnimalUpkeep.Food, reviewer.Policy);
                    if (work.Reason != "")
                    {
                        reviewer.LogQuestSkip(call, new QuestSkip { Quest = offer.Quest, Reason = work.Reason });
                    }

                    if (work.Departure is not null)
                    {
                        action = DomainAction.NewCaravanDepartureAction(actionId, work.Departure);
                        crew = work.Departure.Crew;
                        kind = "relief";
                    }
                    else if (work.Return)
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
                    var work = PolicyFunctions.PlanCoreSiteWork(offer, site, f, current);
                    if (work.Reason != "")
                    {
                        reviewer.LogQuestSkip(call, new QuestSkip { Quest = offer.Quest, Reason = new QuestSkipReason(work.Reason) });
                    }

                    switch (work.Kind)
                    {
                        case "mine":
                            action = DomainAction.NewMineAcquisitionAction(actionId, work.Mine);
                            kind = "mine";
                            target = work.Mine.Thing;
                            break;
                        case "help":
                            action = DomainAction.NewRecoveryServiceAction(actionId, work.Help);
                            crew = [work.Help.Pawn];
                            kind = "help";
                            target = work.Help.Thing;
                            break;
                        case "recruit":
                            action = DomainAction.NewPrisonerInteractionAction(actionId, work.Recruit);
                            kind = "recruit";
                            target = work.Recruit.Pawn.Value;
                            break;
                        case "return":
                            returning = true;
                            break;
                        default:
                            if (PolicyFunctions.QuestHackComplete(offer))
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
                    var work = PolicyFunctions.PlanSiteReturn(site, false);
                    if (work.Departure is null)
                    {
                        if (work.Reason != "")
                        {
                            reviewer.LogQuestSkip(call, new QuestSkip { Quest = offer.Quest, Reason = work.Reason });
                        }

                        continue;
                    }

                    action = DomainAction.NewCaravanDepartureAction(actionId, work.Departure);
                    crew = work.Departure.Crew;
                    kind = "return";
                }

                if (crew.Count > 0 && !arbiter.TryClaim(crew, "quest:" + offer.Quest))
                {
                    return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.Claim, "quest_site") }, true);
                }

                var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action!]);
                var prefix = $"quest-site-{offer.Quest}-{site.Id}-{kind}-{target}-";
                var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(call, reviewer.Player.Journal, goal, prefix, state.Snapshot);
                if (!admitted)
                {
                    return (new RoundsPopulationJoinerResult { Verdict = verdict }, true);
                }

                reviewer.Player.Current(call, epoch);
                var elapsed = reviewer.Clock.Now() - started;
                if (reviewer.Player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
                {
                    throw new ControlException("admitSiteWork: session state changed or the read took too long");
                }

                reviewer.Player.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
                return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
            }
        }

        return (default, false);
    }
}
