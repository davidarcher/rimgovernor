namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>One combat decision over a combat read: the wire orders to apply, the pawns it drafts and the memory it leaves.</summary>
/// <param name="View">The view the policy decided over; hand it back to <see cref="BuildingRuntimePackage.CombatStepResults"/>.</param>
/// <param name="Orders">The policy's orders.</param>
/// <param name="Drafts">The roles the memory names that are not orderable yet; drafted ahead of the orders.</param>
/// <param name="Batch">The wire batch (drafts first, then orders), or null when there is nothing to send.</param>
/// <param name="Memory">The memory after the decision.</param>
internal sealed record CombatStep(CombatView View, IReadOnlyList<CombatOrder> Orders, IReadOnlyList<PawnId> Drafts, PbOperations.CombatOrders? Batch, CombatMemory Memory);

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The combat step the defense round takes, without its plan store: the frame inputs, the view, the newest stop,
    /// <c>DecideCombat</c>, and the wire batch of its orders. The offline real-engine host drives a fight with it (#3142),
    /// so a fight there is decided by the code that decides one live. A geometry ask is answered with an empty answered reply (no
    /// standable cells, proposals or sight lines), so the policy forms without terrain knowledge. Null when there is no fight to decide.
    /// </summary>
    internal static CombatStep? DecideCombatStep(BridgeCombat combat, IReadOnlyList<PawnId> orderable, CombatMemory memory, string key)
    {
        var (inputs, _) = CombatFrameInputs(combat, []);
        if (inputs is null)
        {
            return null;
        }
        var view = CombatView(combat, inputs, orderable, Unknown<CombatLayout>());
        var stop = CombatStop(combat, memory.Tick);
        var (orders, ask, next) = PolicyApi.DecideCombat(view, new GeometryReply(), stop, memory);
        var reply = new GeometryReply { Answered = true };
        if (ask is not null)
        {
            (orders, _, next) = PolicyApi.DecideCombat(view, reply, stop, memory);
        }
        var drafts = UndraftedRoles(next, orderable, view);
        if (drafts.Count > 0)
        {
            var all = orderable.Concat(drafts).OrderBy(d => d.Value, StringComparer.Ordinal).ToList();
            view = CombatView(combat, inputs, all, Unknown<CombatLayout>());
            (orders, ask, next) = PolicyApi.DecideCombat(view, reply, stop, memory);
            if (ask is not null)
            {
                (orders, _, next) = PolicyApi.DecideCombat(view, reply, stop, memory);
            }
        }
        var commands = CombatBatchCommands(drafts, orders);
        PbOperations.CombatOrders? batch = null;
        if (commands.Count > 0)
        {
            var action = DomainAction.NewCombatBatchAction(new ActionId(key), new CombatBatch(new PlanId(key), key, commands));
            batch = BridgeApi.IntentAction(key, action).Intent is PbOperations.ActionIntent.CombatOrdersCase { Value: var wire } ? wire : null;
        }
        return new CombatStep(view, orders, drafts, batch, next);
    }

    /// <summary>The memory after the batch's results: a refused order is remembered as the round remembers it.</summary>
    internal static CombatMemory CombatStepResults(CombatStep step, IReadOnlyList<PbReceipts.CombatOrderResult> results)
    {
        var shaped = new List<BridgeCombatOrderResult>();
        foreach (var r in results.Skip(step.Drafts.Count))
        {
            shaped.Add(new BridgeCombatOrderResult { Index = (int)(r.Index ?? 0), PawnId = r.PawnId ?? "", Applied = r.Applied == true, Refusal = r.Refusal ?? "" });
        }
        return CombatStopRecord(step.View, step.Orders, shaped, step.Memory).Memory;
    }
}
