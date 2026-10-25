using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

/// <summary>The climate-envelope step of the layout family (#3256): the ring walls and roof that hold the planned rooms' temperature.</summary>
public sealed partial class LayoutReview
{
    /// <summary>
    /// The <c>layout/climate-envelope</c> step: rereads the rooms census and reconciles each owed room's ring and roof
    /// (ring only, climate only) under the Need owner, until one wave is admitted. A room whose wave is still open
    /// continues it and does not hold the next room: every wave is admitted by its claims.
    /// </summary>
    private async Task<Verdict> EnvelopeStepAsync(NeedWork work, CancellationToken call)
    {
        if (_building is null)
        {
            return FieldUnavailable("building_source");
        }
        var p = _reviewer.Player;
        var state = work.State;
        var review = await p.Journal.LoadReviewAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("envelope: the scope is another world or older than the review");
        }
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, [ClimateRules.ClimateDefs().VentDef], call).ConfigureAwait(false);
        var (rooms, known) = EnvelopeWork(reading.Reading);
        if (!known)
        {
            return FieldUnavailable("layout_plan");
        }
        var verdict = BuildingReasonNoDeficit;
        var working = false;
        foreach (var room in rooms)
        {
            var rr = new RoomReconcile { Room = room, Name = SleepingUpkeepPlanner.RoomReconcileName("envelope", room), Reason = "envelope", RingOnly = true, ClimateOnly = true };
            var result = await _building.ReconcileRoomAsync(state, review, work.Owner, reading, null, rr, call, work.Epoch).ConfigureAwait(false);
            verdict = result.Verdict;
            if (verdict.Outcome == Outcome.Admitted)
            {
                return verdict;
            }
            working = working || verdict == BuildingReasonExistingWork;
        }
        return working ? BuildingReasonExistingWork : verdict;
    }
}

partial class LayoutReview
{
    /// <summary>The planned rooms whose ring or roof the envelope still has to build; not known without a layout plan or colony ground.</summary>
    internal static (List<PlannedRoom> Rooms, bool Known) EnvelopeWork(ColonyReading facts)
    {
        if (facts.Facts.LayoutPlan is not { } plan)
        {
            return ([], false);
        }
        var rooms = new List<PlannedRoom>();
        foreach (var room in plan.Rooms)
        {
            if (room.Parent != 0)
            {
                continue; // an airlock rides its parent's ring and roof
            }
            var input = BuildingPlanner.RoomRingInput(facts, plan, room);
            if (ClimateEnvelopeRules.EnvelopeOps(ReconcileRules.ReconcileRoom(input)).Count > 0)
            {
                rooms.Add(room);
            }
        }
        return (rooms, true);
    }
}
