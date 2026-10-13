namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Sends a caravan to a quest site, or brings a stopped one home; the result carries wait ticks even when not handled.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitExpedition(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started)
    {
        var work = PolicyFunctions.SelectExpedition(read.Projection.Facts, reviewer.Policy);
        var tripsKnown = read.Projection.Facts.QuestExpeditionTrips.TryGetValue(out var trips);
        var sitesKnown = read.Projection.Facts.QuestSites.TryGetValue(out var sites);
        if (tripsKnown && sitesKnown)
        {
            foreach (var method0 in goal.History)
            {
                var previous = reviewer.Player.Journal.LoadPlan(call, method0.Plan);
                foreach (var action0 in previous.Spec.Actions)
                {
                    if (!action0.TryCaravanDeparture(out var departed))
                    {
                        continue;
                    }

                    foreach (var site in sites!)
                    {
                        if (!site.Tile.TryGetValue(out var tile) || tile != departed.DestinationTile || site.QuestIds.Count == 0)
                        {
                            continue;
                        }

                        var crew = departed.Crew.ToList();
                        foreach (var (pawn, quest) in PolicyFunctions.QuestRefugeeIds(read.Projection.Facts.QuestOffers))
                        {
                            if (site.QuestIds.Contains(quest))
                            {
                                crew.Add(pawn);
                            }
                        }

                        foreach (var trip in trips!)
                        {
                            var home = PolicyFunctions.PlanStoppedExpeditionReturn(trip, crew);
                            if (home is not null)
                            {
                                work = new ExpeditionPlan { Quest = site.QuestIds[0], Site = site.Id, Departure = home };
                                break;
                            }
                        }
                    }
                }
            }
        }

        if (work.Quest == "")
        {
            return (default, false);
        }

        if (work.Reason != "")
        {
            reviewer.LogQuestSkip(call, new QuestSkip { Quest = work.Quest, Reason = work.Reason });
        }

        if (work.Departure is null)
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, false);
        }

        if (!arbiter.TryClaim(work.Departure.Crew, $"quest:{work.Quest}"))
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.Claim, "quest_expedition") }, true);
        }

        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewCaravanDepartureAction(new ActionId(id.Value + "-0"), work.Departure);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        var prefix = $"quest-expedition-{work.Quest}-{work.Site}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(call, reviewer.Player.Journal, goal, prefix, state.Snapshot);
        if (!admitted)
        {
            return (new RoundsPopulationJoinerResult { Verdict = verdict }, true);
        }

        reviewer.Player.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (reviewer.Player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("admitExpedition: session state changed or the read took too long");
        }

        reviewer.Player.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
