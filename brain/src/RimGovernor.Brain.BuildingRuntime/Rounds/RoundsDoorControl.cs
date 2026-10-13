using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>Opens or holds the doors of rooms the policy wants changed (heat passage or routes), at most one change per step; handled is false when nothing was owed.</summary>
    internal (RoundsBuildingResult Result, bool Handled) ControlRoomDoor(ControlState state, StandardState goal, ColonyProjection facts, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        if (p.Journal.OpenCombatFights(call).Count > 0)
        {
            return (new RoundsBuildingResult(), false);
        }
        foreach (var change in PolicyApi.DoorChanges(facts.Rooms, facts.Facts.Upkeep.Routes, Reviewer.Policy))
        {
            if (Concern == PolicyApi.EnsureTemperatureSafety && !change.Heat)
            {
                continue;
            }
            if (Concern == PolicyApi.MaintainRoutes && change.Heat)
            {
                continue;
            }
            change.Door.HoldOpen.TryGetValue(out var held);
            PawnId opener = default;
            if (change.Heat && change.Passage)
            {
                if (facts.WorkPawns.TryGetValue(out var known))
                {
                    var pawns = known.ToList();
                    pawns.Sort((a, b) => string.CompareOrdinal(a.Id.Value, b.Id.Value));
                    foreach (var pawn in pawns)
                    {
                        var ak = pawn.Available.TryGetValue(out var available);
                        var jk = pawn.Job.TryGetValue(out var job);
                        if (ak && available && jk && job!.Work != WorkType.Doctor && job.Work != WorkType.Firefighter && job.Def != "Rescue" && job.Def != "LayDown" && change.Occupants.Contains(new PawnId(pawn.Id.Value)))
                        {
                            opener = new PawnId(pawn.Id.Value);
                            break;
                        }
                    }
                }
                if (opener.IsEmpty)
                {
                    continue;
                }
            }
            if (held == change.HoldOpen && opener.IsEmpty)
            {
                return (new RoundsBuildingResult { Verdict = WaitFor(Cause.MethodUsed, "door_passage_pending"), NativeWorkTicks = StockWaitTicks }, true);
            }
            var id = PlanIds.MintPlanId();
            var actions = new List<DomainAction>();
            if (held != change.HoldOpen)
            {
                var control = new DoorControl(change.Door.Cell, change.HoldOpen);
                actions.Add(DomainAction.NewDoorControlAction(new ActionId($"{id}-0"), control));
            }
            if (!opener.IsEmpty)
            {
                var draft = new OwnedDraft((Domain.PawnId)opener);
                var draftAction = DomainAction.NewOwnedDraftAction(new ActionId($"{id}-1"), draft);
                var movement = new Movement((Domain.PawnId)opener, change.To, draftAction.Id);
                var moveAction = DomainAction.NewMovementAction(new ActionId($"{id}-2"), movement);
                actions.Add(draftAction);
                actions.Add(moveAction);
            }
            var plan = new PlanSpec(id, new PlanRevision(1), actions);
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("controlRoomDoor: session state changed");
            }
            var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"door-{change.Door.Cell.X}-{change.Door.Cell.Z}-{id}"));
            p.Journal.CommitOwnerMethod(goal, method, "", plan, call);
            return (new RoundsBuildingResult { Verdict = BuildingReasonAdmitted }, true);
        }
        return (new RoundsBuildingResult(), false);
    }
}
