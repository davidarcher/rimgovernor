using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.Host;

/// <summary>
/// Go <c>AuthorityUncertain</c> (authority.go): control may have changed. Observe current authority before another
/// control decision; a failed response is never a retry license.
/// </summary>
internal sealed class FacadeAuthorityUncertainException(Exception cause, BridgeResult receipt)
    : Exception("authority outcome uncertain: " + cause.Message, cause)
{
    public BridgeResult Receipt { get; } = receipt;
}

/// <summary>
/// Go <c>SaveUncertain</c> (lifecycle_save.go): the native checkpoint save outcome is not confirmed (identity, tick or
/// pause moved before or during the save). It is never a license to retry blindly; observe current state first.
/// </summary>
internal sealed class FacadeSaveUncertainException(PbLifecycle.SaveUncertain value, BridgeResult receipt)
    : Exception("native save outcome uncertain: " + (value.Detail ?? ""))
{
    public PbLifecycle.SaveUncertain Value { get; } = value;

    public BridgeResult Receipt { get; } = receipt;
}

/// <summary>The shared validation helpers of the write paths (actions, rules, clock, authority focus, lifecycle save).</summary>
internal static class FacadeWriteChecks
{
    /// <summary>Go <c>buildingContext(value, identity, generation, exact)</c> (write_contract.go).</summary>
    internal static void WriteContext(PbCommon.ObservationContext? value, PbCommon.Identity? identity, ulong generation, bool exact)
    {
        BridgeFunctions.ValidateContext(value);
        if (!BridgeFunctions.SameIdentity(value!.Identity, identity)
            || (exact && (value.NativeGeneration is null || value.NativeGeneration.GetValueOrDefault() != generation)))
        {
            throw BridgeFunctions.Contract("building context mismatch");
        }
    }

    /// <summary>Go <c>authorityRequest</c>: a valid identity and a positive generation.</summary>
    internal static void WriteAuthorityRequest(PbCommon.Identity? identity, ulong? generation)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        if (generation is null || generation.GetValueOrDefault() == 0)
        {
            throw BridgeFunctions.Contract("positive authority generation required");
        }
    }

    /// <summary>
    /// Go <c>clockStatusReply</c>: the status of a status reply, after the typed failure and unavailable arms are
    /// turned into the exceptions Go returned and the status itself is validated.
    /// </summary>
    internal static PbClock.Status ClockStatusReply(PbClock.StatusReply reply, PbCommon.Identity? identity)
    {
        switch (reply.Outcome)
        {
            case PbClock.StatusReplyOutcome.FailureCase { Value: var failure }:
                throw FacadeNativeClient.Failure(failure);
            case PbClock.StatusReplyOutcome.StatusCase { Value: var status }:
                BridgeFunctions.ClockStatus(status, identity);
                if (status.State is PbClock.StatusState.UnavailableCase { Value: var unavailable })
                {
                    throw FacadeNativeClient.Unavailable(unavailable);
                }
                return status;
            default:
                throw BridgeFunctions.Contract("missing clock status outcome");
        }
    }
}
