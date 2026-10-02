#nullable enable
using Clock = RimGovernor.Protocol.Clock;

namespace HomeBridge.BridgeTools
{
    // RimWorld-free mappings; the native contract probes link this file
    // without the rest of NativeEnums.
    internal static partial class NativeEnums
    {
        // The clock watcher records what held a force pause by name.
        internal static Clock.ForcePauseKind ForcePause(string? kind) => kind switch
        {
            "long_event" => Clock.ForcePauseKind.LongEvent,
            "transient_force_pause" => Clock.ForcePauseKind.Transient,
            _ => Clock.ForcePauseKind.Unspecified
        };
    }
}
