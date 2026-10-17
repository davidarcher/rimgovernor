using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Go <c>bridge.ActionsWriter</c> (actions.go): sends intent batches, one uniquely keyed action per intent. A batch
/// failure throws <see cref="NativeFailureException"/>: nothing applied.
/// </summary>
internal sealed class FacadeActionsWriter(INativeFacade facade) : IActionsWriter
{
    private readonly INativeFacade _facade = facade ?? throw new ArgumentNullException(nameof(facade));

    /// <summary>Go <c>ActionsWriter.Apply</c>.</summary>
    public async Task<PbOperations.ApplyReply> ApplyAsync(PbCommon.Identity identity, IReadOnlyList<PbOperations.Action> actions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            throw BridgeFunctions.Contract("invalid actions batch");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (action is null || !BridgeFunctions.IsValidId(action.Key ?? "") || !seen.Add(action.Key ?? "") || action.Intent is null)
            {
                throw BridgeFunctions.Contract("action requires a unique key and an intent");
            }
        }
        var request = new PbOperations.ApplyRequest
        {
            Identity = identity,
            Actions = EquatableArray.From(StampPurpose(actions)),
            DeferSnapshot = DeferredSnapshotScope.Current ? true : null,
        };
        var reply = await _facade.ApplyAsync(request, ct).ConfigureAwait(false);
        if (reply.BatchFailure is not null)
        {
            throw FacadeNativeClient.Failure(reply.BatchFailure);
        }
        return reply;
    }

    /// <summary>Go <c>stampPurpose</c>: the dispatch's intent text becomes the purpose of every action that has none.</summary>
    private static IReadOnlyList<PbOperations.Action> StampPurpose(IReadOnlyList<PbOperations.Action> actions)
    {
        var intent = OperationIntentScope.Current;
        if (intent.Length == 0)
        {
            return actions;
        }
        return [.. actions.Select(action => action.Purpose is null ? action with { Purpose = intent } : action)];
    }
}
