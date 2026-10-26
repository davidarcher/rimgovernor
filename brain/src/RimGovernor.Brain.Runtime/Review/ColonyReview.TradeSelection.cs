namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>Selected external work uses the existing Need, action journal and Hands path. The shared binding leaves later
    /// work on these participants for the fresh review after the native comms/formation effect is observed.</summary>
    private (bool Admitted, string? Failure) AdmitSelectedTrade(ForecastCoupledSelection selection, ForecastCoupledCandidates candidates, ReviewFacts facts,
        GenerationSnapshot snapshot, CancellationToken call, CancellationToken epoch)
    {
        var ready = ForecastTradeExecution.Ready(selection.Selection, candidates.Context);
        if (ready.Count == 0) return (false, null);
        var admitted = false; string? failure = null;
        void Record(Sto.NeedCommit commit)
        {
            admitted |= commit.Outcome == Sto.ClaimOutcome.Admitted;
            failure ??= commit.Conflict?.Reason ?? commit.Refusal?.Message;
        }
        Player.Current(call, epoch);
        if (Player.Session.State().Snapshot != snapshot) throw new ControlException("trade selection scope changed");
        foreach (var work in ready.DistinctBy(w => w.Root))
        {
            var id = PlanIds.MintPlanId();
            if (work.Action is CommsTradeRequest request)
            {
                if (!Needs.Contains(EconomyFamily.Caravan)) continue;
                var method = new MethodId(work.Root + "/" + request.ExpectedLastRequestTick);
                Record(Player.Journal.CommitClaimedMethod(EconomyFamily.Caravan, snapshot, method, "selected stock arrival",
                    new PlanSpec(id, new PlanRevision(1), [DomainAction.NewCommsTradeRequestAction(new ActionId(id.Value + "-request"), request)]), call));
            }
            else if (work.Mission is { } option)
            {
                if (!Needs.Contains(EconomyFamily.TradeMission)) continue;
                var record = Player.Journal.LoadTradeMissions(Player.PlayerWorld(snapshot), call);
                if (record.Missions.Count != 0) continue;
                var mission = ForecastTradeExecution.Mission(option, snapshot,
                    TradePolicyRules.TradeMissionBudget(facts.Silver(), facts.Census.ColonistCount),
                    ForecastTradeRequests.SelectedWaits(selection, candidates), candidates.Context.Stock.Medicines, RimGovernor.Brain.Bridge.GameDefinitionSemantics.Instance);
                Record(Player.Journal.CommitTradeMissionMethod(EconomyFamily.TradeMission, snapshot, new MethodId("trade-depart/" + id.Value),
                    "selected stock arrival", new PlanSpec(id, new PlanRevision(1),
                        [DomainAction.NewCaravanDepartureAction(new ActionId(id.Value + "-departure"), mission.Departure(RimGovernor.Brain.Bridge.GameDefinitionSemantics.Instance))]),
                    record.With(id.Value, mission), call));
            }
            else if (work.Action is Trade visit)
            {
                if (!Needs.Contains(EconomyFamily.Caravan)) continue;
                Record(Player.Journal.CommitClaimedMethod(EconomyFamily.Caravan, snapshot, TradePlanner.TradeMethod(TradeOperationKind.Open, visit.Participant.Key()),
                    "selected stock arrival", new PlanSpec(id, new PlanRevision(1),
                        [DomainAction.NewTradeAction(new ActionId(id.Value + "-open"), visit)]), call));
            }
        }
        return (admitted, failure);
    }
}
