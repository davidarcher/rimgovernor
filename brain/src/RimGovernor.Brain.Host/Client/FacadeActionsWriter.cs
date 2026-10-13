using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.NativeFacade;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Go <c>bridge.ActionsWriter</c> (actions.go): sends intent batches. Apply checks the reply carries one result per
/// action, in order, under the same key. A batch failure throws <see cref="NativeFailureException"/>: nothing applied.
/// </summary>
internal sealed class FacadeActionsWriter(INativeFacade facade) : IActionsWriter
{
    private readonly INativeFacade _facade = facade ?? throw new ArgumentNullException(nameof(facade));

    /// <summary>Go <c>ActionsWriter.Apply</c>.</summary>
    public async Task<PbOperations.ApplyReply> ApplyAsync(PbCommon.Identity identity, IReadOnlyList<PbOperations.Action> actions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (!IdentityIsValid(identity) || actions.Count == 0)
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
        BridgeFunctions.BuildingUnknown(reply);
        if (reply.BatchFailure is not null)
        {
            throw FacadeNativeClient.Failure(reply.BatchFailure);
        }
        if (reply.Results.Count != actions.Count)
        {
            throw BridgeFunctions.Contract("actions reply result count mismatch");
        }
        for (var i = 0; i < reply.Results.Count; i++)
        {
            var result = reply.Results[i];
            if ((result.Key ?? "") != (actions[i].Key ?? ""))
            {
                throw BridgeFunctions.Contract("actions reply key mismatch");
            }
            switch (result.Outcome)
            {
                case PbOperations.ActionResultOutcome.AppliedCase { Value: var receipt }:
                    ActionReceipt(receipt, identity);
                    break;
                case PbOperations.ActionResultOutcome.RefusedCase { Value: var refused }:
                    if (!BridgeFunctions.Diagnostic(refused.Reason is not null, refused.Reason ?? "")
                        || (int)refused.RefusalClass.GetValueOrDefault() < (int)PbCommon.RefusalClass.Unspecified
                        || (int)refused.RefusalClass.GetValueOrDefault() > (int)PbCommon.RefusalClass.Unknown)
                    {
                        throw BridgeFunctions.Contract("action refusal invalid");
                    }
                    break;
                case PbOperations.ActionResultOutcome.FailedCase { Value: var failed }:
                    if (failed.Code is null)
                    {
                        throw BridgeFunctions.Contract("action failure invalid");
                    }
                    break;
                default:
                    throw BridgeFunctions.Contract("action outcome missing");
            }
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

    /// <summary>Go <c>actionReceipt</c>.</summary>
    private static void ActionReceipt(PbReceipts.Receipt? receipt, PbCommon.Identity identity)
    {
        if (receipt is null)
        {
            throw BridgeFunctions.Contract("action receipt missing");
        }
        FacadeWriteChecks.WriteContext(receipt.AdmittedContext, identity, 0, false);
        switch (receipt.Outcome)
        {
            case PbReceipts.ReceiptOutcome.AppliedCase { Value: var applied }:
                if (applied.Observed is null)
                {
                    throw BridgeFunctions.Contract("action applied evidence missing");
                }
                break;
            case PbReceipts.ReceiptOutcome.UncertainCase:
                break;
            default:
                throw BridgeFunctions.Contract("unsupported action receipt");
        }
    }

    private static bool IdentityIsValid(PbCommon.Identity? identity)
    {
        try
        {
            BridgeFunctions.ValidateIdentity(identity);
            return true;
        }
        catch (BridgeContractException)
        {
            return false;
        }
    }
}
