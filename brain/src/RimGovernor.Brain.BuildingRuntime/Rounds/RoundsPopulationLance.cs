namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>The able colonists wearing a lance item, with the item they would use.</summary>
    internal static IReadOnlyList<LanceUser> LanceUsers(IReadOnlyList<Observations.PawnState> rows, Things things)
    {
        var output = new List<LanceUser>();
        foreach (var row in rows)
        {
            var facts = SquadDefenderFacts(row, null, Unknown<bool>());
            var dk = facts.Dead.TryGetValue(out var dead);
            var wk = facts.Downed.TryGetValue(out var downed);
            var mk = facts.MentalState.TryGetValue(out var mental);
            var vk = facts.ViolenceCapable.TryGetValue(out var violent);
            if (!dk || !wk || !mk || !vk || dead || downed || mental || !violent)
            {
                continue;
            }

            foreach (var item in row.Equipment?.Apparel ?? [])
            {
                if (PolicyFunctions.LanceDefs.Contains(new Resource(GearDef(things, item.Thing))) && item.Thing.Id != "")
                {
                    output.Add(new LanceUser { Pawn = new PawnId(row.Pawn.Id), Item = item.Thing.Id });
                    break;
                }
            }
        }

        return output;
    }
}

public sealed partial class RoundsPopulationCustodyPlanner
{
    /// <summary>Uses a lance on the target when a colonist wears one; not handled when no colonist can.</summary>
    internal (RoundsPopulationCustodyResult Result, bool Handled) StepLance(CancellationToken call, CancellationToken epoch, Player p, ControlState state, DateTimeOffset started, StandardState goal, Tick reviewTick, PawnId target, StepArbiter arbiter)
    {
        var identityCtx = BoundaryPackage.Identity(state.Snapshot);
        var (emergency, _) = native.ReadEmergency(call, identityCtx);
        try
        {
            BoundaryPackage.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("stepLance: emergency context", e);
        }

        if (emergency.Context.Tick < reviewTick.Value)
        {
            throw new ControlException("stepLance: emergency context");
        }

        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return (default, false);
        }

        var ids = emergency.Facts.Colonists.Where(pawn => new PawnId(pawn.Id.Value) != target).Select(pawn => pawn.Id.Value).ToList();
        if (ids.Count == 0)
        {
            return (default, false);
        }

        var (reply, _) = native.ReadCombatPawns(call, identityCtx, ids);
        var observed = reply.Observed ?? throw new ControlException("stepLance: observed == nil");
        try
        {
            BoundaryPackage.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("stepLance: combat context", e);
        }

        foreach (var row in observed.Pawns)
        {
            if (row is null || row.Pawn is null)
            {
                throw new ControlException("stepLance: row or its pawn is missing");
            }
        }

        var things = BuildingRuntimePackage.FrameThings(call, native, identityCtx);
        var (choice, ok) = PolicyFunctions.SelectLanceUse(target, BuildingRuntimePackage.LanceUsers(observed.Pawns, things));
        if (!ok || !arbiter.TryClaim([choice.User, choice.Target]))
        {
            return (default, false);
        }

        var prefix = $"population-lance-{choice.Target}-";
        var (method, _, admitted) = BuildingRuntimePackage.AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
        if (!admitted)
        {
            return (default, false);
        }

        var use = UseItem.NewUseItem(choice.User, choice.Item, choice.Target);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewUseItemAction(new ActionId($"{id}-0"), use);
        return (Commit(call, epoch, p, state, started, goal, method, id, action), true);
    }
}
