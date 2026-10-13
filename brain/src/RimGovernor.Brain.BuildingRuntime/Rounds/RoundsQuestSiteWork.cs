namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Works a quest site on the current map: survey relief, mining, help, recruiting or the return trip.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitSiteWork(ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
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
                List<PawnId> crew = [];
                var kind = "";
                var target = site.Id;
                var returning = false;
                if (offer.ScriptDef == "SurveySite")
                {
                    var work = PolicyFunctions.SurveyWork(offer, site, f, f.AnimalUpkeep.Food, reviewer.Policy);
                    if (work.Reason != "")
                    {
                        reviewer.LogQuestSkip(new QuestSkip(offer.Quest, "", work.Reason, ""), call);
                    }

                    if (work.Departure is not null)
                    {
                        action = DomainAction.NewCaravanDepartureAction(actionId, work.Departure);
                        crew = work.Departure.Crew.ToPolicyIds();
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
                        reviewer.LogQuestSkip(new QuestSkip(offer.Quest, "", new QuestSkipReason(work.Reason), ""), call);
                    }

                    switch (work.Kind)
                    {
                        case "mine":
                            var mine = work.Mine ?? throw new ControlException("questSiteWork: mine work without an acquisition");
                            action = DomainAction.NewMineAcquisitionAction(actionId, mine);
                            kind = "mine";
                            target = mine.Thing;
                            break;
                        case "help":
                            var help = work.Help ?? throw new ControlException("questSiteWork: help work without a service");
                            action = DomainAction.NewRecoveryServiceAction(actionId, help);
                            crew = [(PawnId)help.Pawn];
                            kind = "help";
                            target = help.Thing;
                            break;
                        case "recruit":
                            var recruit = work.Recruit ?? throw new ControlException("questSiteWork: recruit work without an interaction");
                            action = DomainAction.NewPrisonerInteractionAction(actionId, recruit);
                            kind = "recruit";
                            target = recruit.Pawn.Value;
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
                            reviewer.LogQuestSkip(new QuestSkip(offer.Quest, "", work.Reason, ""), call);
                        }

                        continue;
                    }

                    action = DomainAction.NewCaravanDepartureAction(actionId, work.Departure);
                    crew = work.Departure.Crew.ToPolicyIds();
                    kind = "return";
                }

                if (crew.Count > 0 && !arbiter.TryClaim(crew, "quest:" + offer.Quest))
                {
                    return (new RoundsPopulationJoinerResult { Verdict = Verdict.WaitFor(Cause.Claim, "quest_site") }, true);
                }

                var plan = new PlanSpec(id, new PlanRevision(1), [action!]);
                var prefix = $"quest-site-{offer.Quest}-{site.Id}-{kind}-{target}-";
                var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(reviewer.Player.Journal, goal, prefix, state.Snapshot, call);
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

                reviewer.Player.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
                return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
            }
        }

        return (default, false);
    }
}
