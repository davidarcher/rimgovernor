using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.Runtime;

/// <summary>
/// Sends intent batches, one uniquely keyed action per intent. A batch
/// failure throws <see cref="NativeFailureException"/>: nothing applied.
/// </summary>
internal sealed class FacadeActionsWriter(IGameApi facade) : IActionsWriter
{
    private readonly IGameApi _facade = facade ?? throw new ArgumentNullException(nameof(facade));

    public async Task<RimGovernor.Brain.GameFacts.Operations.ApplyReply> ApplyAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, IReadOnlyList<RimGovernor.Brain.GameOrders.Operations.Action> actions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            throw BridgeContractException.Contract("invalid actions batch");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (action is null || !seen.Add(action.Key ?? "") || action.Intent is null)
            {
                throw BridgeContractException.Contract("action requires a unique key and an intent");
            }
        }
        var request = new RimGovernor.Brain.GameOrders.Operations.ApplyRequest
        {
            Identity = identity,
            Actions = EquatableArray.From(StampPurpose(actions)),
            DeferSnapshot = DeferredSnapshotScope.Current,
        };
        var reply = await _facade.ApplyAsync(request, ct).ConfigureAwait(false);
        if (reply.BatchFailure is not null)
        {
            throw GameClient.Failure(reply.BatchFailure);
        }
        return reply;
    }

    /// <summary>The dispatch's intent text becomes the purpose of every action that has none.</summary>
    private static IReadOnlyList<RimGovernor.Brain.GameOrders.Operations.Action> StampPurpose(IReadOnlyList<RimGovernor.Brain.GameOrders.Operations.Action> actions)
    {
        var intent = OperationIntentScope.Current;
        if (intent.Length == 0)
        {
            return actions;
        }
        return [.. actions.Select(action => action.Purpose is null ? action with { Purpose = intent } : action)];
    }
}
