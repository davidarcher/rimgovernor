namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>Selected work creation uses the existing shared Need, journal, bill claims and Hands writer.</summary>
    private (bool Admitted, string? Failure) AdmitSelectedProduction(ForecastCoupledSelection selection,
        ForecastCoupledCandidates candidates, GenerationSnapshot snapshot, CancellationToken call, CancellationToken epoch)
    {
        if (!Needs.Contains(AdminFamily.WorkLedger)) return (false, null);
        var bills = ForecastCoupledSelection.SelectedProductionBills(selection, candidates);
        if (bills.Count == 0) return (false, null);
        Player.Current(call, epoch);
        if (Player.Session.State().Snapshot != snapshot) throw new ControlException("production selection scope changed");
        var admitted = false;
        string? failure = null;
        foreach (var bill in bills)
        {
            var id = PlanIds.MintPlanId();
            var key = new OrderSpec { BenchKind = bill.Bench, Recipe = bill.Recipe, Mode = bill.Mode,
                Target = bill.Target, Ingredients = bill.Ingredients }.Key();
            var commit = Player.Journal.CommitClaimedMethod(AdminFamily.WorkLedger, snapshot,
                AdminReview.LedgerMethod("selected-native/" + key), "selected ordinary production",
                new PlanSpec(id, new PlanRevision(1), [DomainAction.NewProductionBillAction(new ActionId(id.Value + "-bill"), bill)]), call);
            admitted |= commit.Outcome == Sto.ClaimOutcome.Admitted;
            failure ??= commit.Conflict?.Reason ?? commit.Refusal?.Message;
        }
        return (admitted, failure);
    }
}
