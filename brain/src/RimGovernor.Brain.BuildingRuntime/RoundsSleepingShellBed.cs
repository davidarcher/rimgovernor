using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsSleepingUpkeepPlanner
{
    /// <summary>
    /// Feeds a new bedroom's reconcile from the starter shell's own beds instead of a newly built one: when no
    /// packed Bed is stored, a vacant Bed left in the shell is packed (uninstalled) so the reconcile's install
    /// from stock takes it on the next pass (the install itself is ReconcileRoom's). Each bed is packed once per
    /// Episode; Due is false when bed is not a Bed, one is stored already, or there is none to carry over, so the
    /// reconcile goes on.
    /// </summary>
    internal (RoundsBuildingResult Result, bool Due) PackShellBed(
        CancellationToken call,
        CancellationToken epoch,
        PackedStock stock,
        ControlState state,
        Store.StandardState goal,
        Observation.RoundsReading reading,
        string bed)
    {
        const string shellBed = "Bed";
        var facts = reading.Projection;
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var sk = facts.Facts.Sleeping.TryGetValue(out var obs);
        if (bed != shellBed || !rk || !ck || !sk || !census!.Colony)
        {
            return (new RoundsBuildingResult(), false);
        }
        var (items, _) = stock.Items(call, Pol.PackedFurniture.PackedFurnitureDefinition);
        foreach (var item in items)
        {
            if (item.InnerDef == shellBed)
            {
                return (new RoundsBuildingResult(), false);
            }
        }
        var p = Reviewer.Player;
        facts.LayoutPlan.TryGetValue(out var plan);
        var shell = Pol.SleepingBed.ShellBedIds(plan, rooms!);
        // empty beds first, then an owner's
        var vacant = obs!.Beds
            .Where(b => shell.Contains(b.Id) && b.Definition == shellBed)
            .OrderBy(b => b.Owners.Count == 0 ? 0 : 1)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var b in vacant)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(b.Id));
            var method = new MethodId("bedroom-shell-pack-" + Convert.ToHexStringLower(digest.AsSpan(0, 8)));
            Method? existing;
            try
            {
                existing = p.Journal.LoadOwnerMethod(call, goal, method);
            }
            catch (Store.NotFoundException)
            {
                existing = null;
            }
            if (existing is not null)
            {
                // Packing is under way: wait for it rather than build a second bed.
                try
                {
                    var prior = p.Journal.LoadPlan(call, existing.Plan);
                    if (Standard.WorkOpen(prior.Progress))
                    {
                        return (new RoundsBuildingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "shell_bed_pack") }, true);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // An unreadable prior plan is skipped like a closed one.
                }
                continue;
            }
            var value = new MoveBuilding(b.Id, shellBed, b.Cell, Rotation.South);
            var id = PlanIds.MintPlanId();
            var action = DomainAction.NewUninstallBuildingAction(new ActionId($"{id}-0"), value);
            return CommitCouple(call, epoch, state, goal, method, id, [action]);
        }
        return (new RoundsBuildingResult(), false);
    }
}
