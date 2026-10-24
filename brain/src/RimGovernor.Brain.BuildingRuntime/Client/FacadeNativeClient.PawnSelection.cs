using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class FacadeNativeClient
{
    /// <summary>The PawnDetails families one read asks for.</summary>
    internal readonly record struct PawnDetailSelection(bool Combat = false, bool Work = false, bool Care = false, bool Tend = false);
}
