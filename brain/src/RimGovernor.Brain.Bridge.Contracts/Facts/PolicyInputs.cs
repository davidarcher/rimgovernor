using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using WirePolicyInputs = RimGovernor.Contracts.Wire.Observations.PawnPolicyInputs;

namespace RimGovernor.Brain.Bridge.Contracts;

public static class PolicyInputs
{
    /// <summary>Lifts a settings row's policy inputs; unknown when the producer carried none.</summary>
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
                IdeoCertainty = FactHelpers.OptionalFact(inputs.IdeoCertainty is not null, inputs.IdeoCertainty.GetValueOrDefault()),
                GuestStatus = inputs.GuestStatus,
                InventoryStock = [.. inputs.InventoryStock.Select(stock => new InventoryStock((stock.Group ?? ""), stock.Count.GetValueOrDefault()))],
            }
        );
    }
}
