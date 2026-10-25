using System;

namespace RimGovernor.Native;

internal static class NativeConstructionWork
{
    // Vanilla adds the final work increment before calling CompleteConstruction.
    // A completion guard can leave that frame spawned with WorkLeft below zero.
    // Its labor is finished; completion permission remains the guard's decision.
    internal static float Remaining(float workLeft) =>
        float.IsNaN(workLeft) || float.IsInfinity(workLeft)
            ? throw new InvalidOperationException("Invalid native construction work remaining.")
            : Math.Max(0f, workLeft);
}
