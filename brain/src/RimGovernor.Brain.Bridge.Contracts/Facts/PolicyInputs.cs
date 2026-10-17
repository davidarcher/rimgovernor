using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using WirePolicyInputs = RimGovernor.Contracts.Wire.Observations.PawnPolicyInputs;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go policy_inputs.go.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>PawnPolicyInputs</c>: lifts a settings row's policy inputs; unknown when the producer carried none.</summary>
    public static Fact<Brain.Policy.PawnPolicyInputs> PawnPolicyInputs(WirePolicyInputs? inputs)
    {
        if (inputs is null)
        {
            return default;
        }
        return Known(
            new Brain.Policy.PawnPolicyInputs
            {
                ReadingPolicy = inputs.ReadingPolicyId ?? "",
                Ideo = inputs.IdeoId ?? "",
                Precepts = [.. inputs.Precepts],
                IdeoRole = inputs.IdeoRole ?? "",
                IdeoCertainty = OptionalFact(inputs.IdeoCertainty is not null, inputs.IdeoCertainty.GetValueOrDefault()),
                GuestStatus = inputs.GuestStatus,
                InventoryStock = [.. inputs.InventoryStock.Select(stock => new InventoryStock((stock.Group ?? ""), stock.Count.GetValueOrDefault()))],
            }
        );
    }
}
