using RimGovernor.Brain.GameAccess;

namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The native checkpoint save outcome is not confirmed (identity, tick or
/// pause moved before or during the save). It is never a license to retry blindly; observe current state first.
/// </summary>
internal sealed class FacadeSaveUncertainException(RimGovernor.Brain.GameFacts.Lifecycle.SaveUncertain value, BridgeResult receipt)
    : Exception("native save outcome uncertain: " + (value.Detail ?? ""))
{
    public RimGovernor.Brain.GameFacts.Lifecycle.SaveUncertain Value { get; } = value;

    public BridgeResult Receipt { get; } = receipt;
}

