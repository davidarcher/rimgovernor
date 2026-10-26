using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed partial class FieldPlanner
{
    /// <summary>Gives up one surplus zone's bare cells with remove-cells, committed directly like a grow. Returns null when nothing was shrunk (no surplus, no bare cell, the method already committed).</summary>
    internal async Task<ReviewFieldResult?> ShrinkAsync(ControlState state, NeedOwner goal, ColonyReading projection, ReviewReading read, FieldRequest field, CancellationToken call, CancellationToken epoch)
    {
        if (LayoutFieldShrink.PlanFieldShrink(projection.Window is { } window ? window.Value.Cells : null, projection.Tables.Zones, LayoutFieldShrink.FieldTargets(field, Hay.HayTarget(projection), SocialFields.SocialCrops(projection))) is not { } plan)
        {
            return null;
        }
        var cells = LayoutFieldShrink.ShrinkCells(plan, LayoutFieldShrink.BareCells(projection.PlanningWindow.Cells, plan.Order));
        if (cells.Count == 0)
        {
            return null;
        }
        var p = _reviewer.Player;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"shrink/{plan.Id}/[{string.Join(" ", cells.Select(c => $"{{{c.X} {c.Z}}}"))}]")));
        var method = new MethodId("fields-" + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
        if (p.Journal.OwnerMethodPlan(goal, method, call) is not null)
        {
            return null;
        }
        var id = PlanIds.MintPlanId();
        var value = new ZoneCellEdit(plan.Id, CellEditMode.Remove, cells);
        var action = DomainAction.NewZoneCellEditAction(new ActionId($"{id.Value}-0"), value);
        var committed = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var now = _reviewer.Clock.Now();
        if (p.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new ControlException("shrink: stale state or read");
        }
        var commit = p.Journal.CommitOwnerWork(goal, method, "", committed, call);
        if (commit.Outcome == ClaimOutcome.Admitted)
        {
            ClockEvents.Event("layout", "fields", "field block shrunk", ("zone", plan.Id), ("crop", plan.Crop), ("cells", cells.Count), ("plan", id.Value));
        }
        return new ReviewFieldResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit), Plan = commit.Outcome == ClaimOutcome.Admitted ? id : default };
    }
}
