namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationJoinerPlanner
{
    /// <summary>Sends a caravan to a quest site, or brings a stopped one home; the result carries wait ticks even when not handled.</summary>
    internal (RoundsPopulationJoinerResult Result, bool Handled) AdmitExpedition(ControlState state, StandardState goal, RoundsReading read, StepArbiter arbiter, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var work = PolicyFunctions.SelectExpedition(read.Projection.Facts, reviewer.Policy);
        var tripsKnown = read.Projection.Facts.QuestExpeditionTrips.TryGetValue(out var trips);
        var sitesKnown = read.Projection.Facts.QuestSites.TryGetValue(out var sites);
        if (tripsKnown && sitesKnown)
        {
            foreach (var method0 in goal.History)
            {
                var previous = reviewer.Player.Journal.LoadPlan(method0.Plan, call);
                foreach (var action0 in previous.Spec.Actions)
                {
                    if (action0.CaravanDeparture is not { } departed)
                    {
                        continue;
                    }

                    foreach (var site in sites!)
                    {
                        if (!site.Tile.TryGetValue(out var tile) || tile != departed.DestinationTile || site.QuestIds.Count == 0)
                        {
                            continue;
                        }

                        var crew = departed.Crew.ToPolicyIds();
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

        if (work.Quest.IsEmpty)
        {
            return (default, false);
        }

        if (!work.Reason.IsNone)
        {
            reviewer.LogQuestSkip(new QuestSkip(work.Quest, "", work.Reason, ""), call);
        }

        if (work.Departure is null)
        {
            return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, false);
        }

        if (!arbiter.TryClaim(work.Departure.Crew.ToPolicyIds(), $"quest:{work.Quest}"))
        {
            return (new RoundsPopulationJoinerResult { Verdict = Verdict.WaitFor(Cause.Claim, "quest_expedition") }, true);
        }

        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewCaravanDepartureAction(new ActionId(id.Value + "-0"), work.Departure);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        var prefix = $"quest-expedition-{work.Quest}-{work.Site}-";
        var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(reviewer.Player.Journal, goal, prefix, state.Snapshot, call);
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

        reviewer.Player.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return (new RoundsPopulationJoinerResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id, NativeWorkTicks = BuildingRuntimePackage.StockWaitTicks }, true);
    }
}
