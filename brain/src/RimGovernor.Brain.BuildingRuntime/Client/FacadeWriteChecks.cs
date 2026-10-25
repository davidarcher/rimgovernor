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

