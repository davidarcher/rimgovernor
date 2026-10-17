using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The native checkpoint save outcome is not confirmed (identity, tick or
/// pause moved before or during the save). It is never a license to retry blindly; observe current state first.
/// </summary>
internal sealed class FacadeSaveUncertainException(PbLifecycle.SaveUncertain value, BridgeResult receipt)
    : Exception("native save outcome uncertain: " + (value.Detail ?? ""))
{
    public PbLifecycle.SaveUncertain Value { get; } = value;

    public BridgeResult Receipt { get; } = receipt;
}

/// <summary>The shared validation helpers of the write paths (clock status).</summary>
internal static class FacadeWriteChecks
{
    /// <summary>
    /// The status of a status reply, after the typed failure arm and an unavailable status
    /// are turned into exceptions.
    /// </summary>
    internal static PbClock.Status ClockStatusReply(PbClock.StatusReply reply)
    {
        if (reply.Outcome.Failure is { } failure)
        {
            throw FacadeNativeClient.Failure(failure);
        }
        var status = reply.Outcome.Status!;
        if (status.Unavailable is { } unavailable)
        {
            throw FacadeNativeClient.Unavailable(unavailable);
        }
        return status;
    }
}
