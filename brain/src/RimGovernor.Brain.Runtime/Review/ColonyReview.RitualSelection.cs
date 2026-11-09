namespace RimGovernor.Brain.Runtime;

public sealed partial class ColonyReview
{
    /// <summary>A selected ritual root whose attendees all depart now is the <c>ideology/rituals</c> Need's Method (#4059), mirroring
    /// <see cref="AdmitSelectedTrade"/>: one Ritual begin action built by the root, the root as the Method id and the root's premise
    /// as its reason. The Method claims the precept and every attendee while the begin is open; the native lord then owns them.</summary>
    private (bool Admitted, string? Failure) AdmitSelectedRitual(ForecastCoupledSelection selection, ForecastCoupledCandidates candidates,
        GenerationSnapshot snapshot, CancellationToken call, CancellationToken epoch)
    {
        var ready = ForecastRitualExecution.Ready(selection.Selection, candidates.Context);
        if (ready.Count == 0 || !Needs.Contains(IdeologyFamily.Rituals)) return (false, null);
        var admitted = false; string? failure = null;
        Player.Current(call, epoch);
        if (Player.Session.State().Snapshot != snapshot) throw new ControlException("ritual selection scope changed");
        foreach (var work in ready.DistinctBy(w => w.Root))
        {
            var id = PlanIds.MintPlanId();
            var commit = Player.Journal.CommitClaimedMethod(IdeologyFamily.Rituals, snapshot, new MethodId(work.Root), work.Premise,
                new PlanSpec(id, new PlanRevision(1), [DomainAction.NewRitualAction(new ActionId(id.Value + "-begin"), work.Begin)]), call);
            admitted |= commit.Outcome == Sto.ClaimOutcome.Admitted;
            failure ??= commit.Conflict?.Reason ?? commit.Refusal?.Message;
        }
        return (admitted, failure);
    }
}
