using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

public static partial class BridgeFunctions
{
    /// <summary>Go's <c>optionalID</c>: an absent id or a valid one.</summary>
    private static bool OptionalId(bool present, string value) => !present || IsValidId(value);

    /// <summary>
    /// A colony census must describe the identity it was asked for: a stale census, read for another load, is
    /// refused. The census sections carry their own availability and are read as they come.
    /// </summary>
    /// <exception cref="BridgeContractException">The census is for another identity.</exception>
    public static void ValidateColonyFacts(ColonyFactsSnapshot? v, Identity? identity)
    {
        if (v is null || v.Context is null || !SameIdentity(v.Context.Identity, identity))
        {
            throw Contract("invalid colony context");
        }
    }
}
