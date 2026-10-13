using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const string DefenseRetierTier = "retier";

    /// <summary>Re-tiers the standing colony buildings that the layout promotes to survive tier, once per site set.</summary>
    internal (RoundsDefenseLayoutResult Result, bool Done) Retier(StandardState goal, ControlState state, RoundsReading read, DefenseLayoutRecord record, CancellationToken call, CancellationToken epoch)
    {
        if (!DefensePromoted(read.Projection))
        {
            return (new RoundsDefenseLayoutResult(), false);
        }
        if (!read.Projection.Facts.CurrentConstruction.TryGetValue(out var census) || !census.Colony)
        {
            return (new RoundsDefenseLayoutResult(), false);
        }
        var layout = new List<Building>();
        foreach (var tier in record.Tiers)
        {
            var (_, buildings, ok) = record.Tier(tier.Name);
            if (ok && !tier.Remove)
            {
                layout.AddRange(buildings);
            }
        }
        var sites = PolicyApi.DefenseRetiers(census.Sites, layout);
        if (sites.Count == 0)
        {
            return (new RoundsDefenseLayoutResult(), false);
        }
        var p = _reviewer.Player;
        var text = new StringBuilder();
        foreach (var site in sites)
        {
            text.Append(site.Id).Append('\n');
        }
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        var method = new MethodId($"defense-{DefenseRetierTier}-{Convert.ToHexStringLower(sum.AsSpan(0, 8))}");
        try
        {
            p.Journal.LoadOwnerMethod(goal, method, call);
            return (new RoundsDefenseLayoutResult(), false);
        }
        catch (NotFoundException)
        {
            // Not yet committed: re-tier now.
        }
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>(sites.Count);
        for (var i = 0; i < sites.Count; i++)
        {
            var action = DomainAction.NewBuildingAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), sites[i].Building ?? throw new ControlException("defense tier: site carries no building"), ConstructionTier.Survive);
            actions.Add(action.WithTier(ConstructionTier.Survive, sites[i].Id));
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw DefenseControlErr(119);
        }
        p.Journal.CommitMethodReason(goal.Standard.Id, goal.Revision, method, "", plan, call);
        DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "retier", DefenseRetierTier, new Dictionary<string, object?> { ["sites"] = sites.Count, ["method"] = method.Value }, call);
        return (new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id, Tier = DefenseRetierTier }, true);
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static bool DefensePromoted(ColonyProjection projection) =>
        PolicyApi.DefensePromoted(projection.Facts.RaidPoints, projection.Facts.DefenseCapacity);
}
