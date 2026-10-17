namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>One combat decision over a combat read: the wire orders to apply, the pawns it drafts and the memory it leaves.</summary>
/// <param name="View">The view the policy decided over; hand it back to <see cref="CombatReads.CombatStepResults"/>.</param>
/// <param name="Orders">The policy's orders.</param>
/// <param name="Drafts">The roles the memory names that are not orderable yet; drafted ahead of the orders.</param>
/// <param name="Batch">The wire batch (drafts first, then orders), or null when there is nothing to send.</param>
/// <param name="Memory">The memory after the decision.</param>
/// <param name="Calls">The permit and psycast calls, each its own wire Ability action; they are not in <paramref name="Orders"/> or the batch.</param>
internal sealed record CombatStep(CombatView View, IReadOnlyList<CombatOrder> Orders, IReadOnlyList<PawnId> Drafts, PbOperations.CombatOrders? Batch, CombatMemory Memory, IReadOnlyList<PbOperations.Action> Calls);

/// <summary>
/// The reads the defense round adds to a combat view after the frame: the remembered
/// royalty facts and the catalog's combat drugs. Unknown royalty is what the round holds without them.
/// </summary>
/// <param name="Drugs">The catalog's combat drug definitions in preference order.</param>
/// <param name="Royalty">The royalty read; unknown holds every permit and psycast call.</param>
internal sealed partial record CombatReads(IReadOnlyList<string> Drugs, Fact<RoyaltyFacts> Royalty)
{
    /// <summary>No reads: no drugs, unknown royalty.</summary>
    internal static CombatReads None { get; } = new([], default);

    internal CombatView Complete(CombatView view, CombatMemory memory) => view with { Royalty = Royalty, Drugs = [.. Drugs] };
}

partial record CombatReads
{
    /// <summary>
    /// The combat step the defense round takes, without its plan store: the frame inputs, the view, the newest stop,
    /// <c>DecideCombat</c>, and the wire batch of its orders. The offline real-engine host drives a fight with it (#3142),
    /// so a fight there is decided by the code that decides one live. A geometry ask is answered from the game through <paramref name="read"/>; the layout and the reads are the
    /// caller's (unknown when it holds none). Null when there is no fight to decide.
    /// </summary>
    internal static CombatStep? DecideCombatStep(BridgeCombat combat, IReadOnlyList<PawnId> orderable, CombatMemory memory, string key, PbCommon.Identity identity, Func<M.CombatGeometryRequest, M.CombatGeometry> read, Fact<CombatLayout> layout, CombatReads reads)
    {
        var (inputs, _) = RoundsDefensePlanner.CombatFrameInputs(combat, [], []);
        if (inputs is null)
        {
            return null;
        }
        var stop = RoundsDefensePlanner.CombatStop(combat, memory.Tick);
        // A geometry ask is answered from the game and the question asked again with the answer, as the defense round does.
        (List<CombatOrder> Orders, CombatMemory Next) Decide(CombatView v)
        {
            var (o, ask, n) = CombatDecideRules.DecideCombat(v, new GeometryReply(), stop, memory);
            if (ask is null)
            {
                return (o, n);
            }
            var reply = RoundsDefensePlanner.AnswerGeometry(read, (_, _, _) => { }, identity, ask);
            var (o2, _, n2) = CombatDecideRules.DecideCombat(v, reply, stop, memory);
            return (o2, n2);
        }
        var view = reads.Complete(RoundsDefensePlanner.CombatView(combat, inputs, orderable, layout), memory);
        var (orders, next) = Decide(view);
        var drafts = RoundsDefensePlanner.UndraftedRoles(next, orderable, view);
        if (drafts.Count > 0)
        {
            var all = orderable.Concat(drafts).OrderBy(d => d.Value, StringComparer.Ordinal).ToList();
            view = reads.Complete(RoundsDefensePlanner.CombatView(combat, inputs, all, layout), memory);
            (orders, next) = Decide(view);
        }
        // The permit and psycast calls leave the batch as Ability actions, as the defense round sends them.
        var (rest, calls) = RoundsDefensePlanner.SplitPermitCalls(orders);
        orders = rest;
        var callActions = RoundsDefensePlanner.PermitCallActions(new PlanId(key), calls).Select((a, i) => BridgeApi.IntentAction($"{key}-c{i}", a)).ToList();
        var commands = RoundsDefensePlanner.CombatBatchCommands(drafts, orders);
        PbOperations.CombatOrders? batch = null;
        if (commands.Count > 0)
        {
            var action = DomainAction.NewCombatBatchAction(new ActionId(key), new CombatBatch(new PlanId(key), key, commands));
            batch = BridgeApi.IntentAction(key, action).Intent is PbOperations.ActionIntent.CombatOrdersCase { Value: var wire } ? wire : null;
        }
        return new CombatStep(view, orders, drafts, batch, next, callActions);
    }

    /// <summary>The memory after the batch's results: a refused order is remembered as the round remembers it.</summary>
    internal static CombatMemory CombatStepResults(CombatStep step, IReadOnlyList<PbReceipts.CombatOrderResult> results)
    {
        var shaped = new List<BridgeCombatOrderResult>();
        foreach (var r in results.Skip(step.Drafts.Count))
        {
            shaped.Add(new BridgeCombatOrderResult { Index = (int)(r.Index ?? 0), PawnId = r.PawnId ?? "", Applied = r.Applied == true, Refusal = r.Refusal ?? "" });
        }
        return RoundsDefensePlanner.CombatStopMemory(step.View, step.Orders, shaped, step.Memory);
    }
}
