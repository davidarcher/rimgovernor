namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class JoinersRounds
{
    /// <summary>Sends a caravan to a quest site, or brings a stopped one home; a wait is not handled.</summary>
    internal (Verdict Verdict, bool Handled) AdmitExpedition(NeedWork work, RoundsReading read, DateTimeOffset started, CancellationToken call)
    {
        var facts = read.Projection.Facts;
        var expedition = QuestExpedition.SelectExpedition(facts, reviewer.Policy);
        if (facts.QuestExpeditionTrips.TryGetValue(out var trips) && facts.QuestSites.TryGetValue(out var sites))
        {
            // A caravan this Need sent that stopped short of its site comes home.
            foreach (var sent in reviewer.Player.Journal.OwnerMethodHistory(work.Owner, call))
            {
                var previous = reviewer.Player.Journal.LoadPlan(sent.Plan, call);
                foreach (var action0 in previous.Spec.Actions)
                {
                    if (action0.CaravanDeparture is not { } departed)
                    {
                        continue;
                    }

                    foreach (var site in sites)
                    {
                        if (!site.Tile.TryGetValue(out var tile) || tile != departed.DestinationTile || site.QuestIds.Count == 0)
                        {
                            continue;
                        }

                        var crew = departed.Crew.ToPolicyIds();
                        foreach (var (pawn, quest) in QuestRefugees.QuestRefugeeIds(facts.QuestOffers))
                        {
                            if (site.QuestIds.Contains(quest))
                            {
                                crew.Add(pawn);
                            }
                        }

                        foreach (var trip in trips)
                        {
                            var home = QuestReturn.PlanStoppedExpeditionReturn(trip, crew);
                            if (home is not null)
                            {
                                expedition = new ExpeditionPlan { Quest = site.QuestIds[0], Site = site.Id, Departure = home };
                                break;
                            }
                        }
                    }
                }
            }
        }

        if (expedition.Quest.IsEmpty)
        {
            return (default, false);
        }

        if (!expedition.Reason.IsNone)
        {
            reviewer.LogQuestSkip(new QuestSkip(expedition.Quest, "", expedition.Reason, ""), call);
        }

        if (expedition.Departure is null)
        {
            return (Verdict.BuildingReasonExistingWork, false);
        }

        if (expedition.Departure.Mode == CaravanDepartureMode.Formation)
        {
            return (Verdict.AwaitingPlan("unsupported_forecast_caravan_formation",
                "Caravan packing and departure require shared forecast labor admission."), true);
        }

        if (!Claim(work, expedition.Departure.Crew.ToPolicyIds(), $"quest:{expedition.Quest}"))
        {
            return (Verdict.WaitFor(Cause.Claim, "quest_expedition"), true);
        }

        var id = PlanIds.MintPlanId();
        var plan = new PlanSpec(id, new PlanRevision(1), [DomainAction.NewCaravanDepartureAction(new ActionId(id.Value + "-0"), expedition.Departure)]);
        var method = new MethodId($"quest-expedition-{expedition.Quest}-{expedition.Site}");
        return (Commit(work, method, plan, started, "admitExpedition", call), true);
    }
}
