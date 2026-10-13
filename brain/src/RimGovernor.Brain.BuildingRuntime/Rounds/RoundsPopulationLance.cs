namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>The able colonists wearing a lance item, with the item they would use.</summary>
    internal static IReadOnlyList<LanceUser> LanceUsers(IReadOnlyList<O.PawnState> rows, Things things)
    {
        var output = new List<LanceUser>();
        foreach (var row in rows)
        {
            var facts = SquadDefenderFacts(row, new HashSet<Domain.PawnId>(), Unknown<bool>());
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
                if (PolicyConstants.LanceDefs.ContainsKey(new Resource(GearDef(things, item.Thing))) && (item.Thing?.Id ?? "") != "")
                {
                    output.Add(new LanceUser(new PawnId(row.Pawn?.Id ?? ""), item.Thing?.Id ?? ""));
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
    internal async Task<(RoundsPopulationCustodyResult Result, bool Handled)> StepLanceAsync(Player p, ControlState state, DateTimeOffset started, StandardState goal, Tick reviewTick, PawnId target, StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var identityCtx = Boundary.Identity(state.Snapshot);
        var (emergency, _) = await native.ReadEmergencyAsync(identityCtx, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("stepLance: emergency context", e);
        }

        if (emergency.Context is not { } emergencyContext || emergencyContext.Tick.GetValueOrDefault() < reviewTick.Value)
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

        var (reply, _) = await native.ReadCombatPawnsAsync(identityCtx, ids, call).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed ?? throw new ControlException("stepLance: observed == nil");
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
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

        var things = await BuildingRuntimePackage.FrameThingsOfAsync(native, identityCtx, call).ConfigureAwait(false);
        if (PolicyFunctions.SelectLanceUse(target, BuildingRuntimePackage.LanceUsers(observed.Pawns, things)) is not { } choice || !arbiter.TryClaim([choice.User, choice.Target]))
        {
            return (default, false);
        }

        var prefix = $"population-lance-{choice.Target}-";
        var (method, _, admitted) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
        if (!admitted)
        {
            return (default, false);
        }

        var use = new UseItem(new Domain.PawnId(choice.User.Value), choice.Item, new Domain.PawnId(choice.Target.Value));
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewUseItemAction(new ActionId($"{id}-0"), use);
        return (Commit(p, state, started, goal, method, id, action, call, epoch), true);
    }
}
