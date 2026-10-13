using RimGovernor.Brain.Policy;
using RimGovernor.Protocol.Observations;
using ChemicalState = RimGovernor.Brain.Policy.ChemicalState;
using WirePolicyInputs = RimGovernor.Protocol.Observations.PawnPolicyInputs;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go policy_inputs.go.
public static partial class BridgeFunctions
{
    private static List<ApparelRequirement> ApparelRequirements(IEnumerable<ApparelRequirementFact> rows) =>
        [
            .. rows.Select(row => new ApparelRequirement
            {
                BodyPartGroups = [.. row.BodyPartGroups],
                RequiredDefs = [.. row.RequiredDefs],
                RequiredTags = [.. row.RequiredTags],
                AllowedTags = [.. row.AllowedTags],
            }),
        ];

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
                OutfitPolicy = inputs.OutfitPolicyId,
                DrugPolicy = inputs.DrugPolicyId,
                ReadingPolicy = inputs.ReadingPolicyId,
                DependencyChemicals = [.. inputs.DependencyChemicals],
                RoyalTitle = inputs.RoyalTitle,
                Ideo = inputs.IdeoId,
                Precepts = [.. inputs.Precepts],
                IdeoRole = inputs.IdeoRole,
                IdeoCertainty = OptionalFact(inputs.HasIdeoCertainty, inputs.IdeoCertainty),
                RoleApparel = ApparelRequirements(inputs.RoleApparel),
                PreceptApparel = [.. inputs.PreceptApparel],
                GuestStatus = inputs.GuestStatus,
                PrisonerInteraction = inputs.PrisonerInteraction,
                SlaveInteraction = inputs.SlaveInteraction,
                TendQuality = OptionalFact(inputs.HasMedicalTendQuality, inputs.MedicalTendQuality),
                InventoryStock = [.. inputs.InventoryStock.Select(stock => new InventoryStock(stock.Group, stock.ThingDef, stock.Count))],
                Chemicals =
                [
                    .. inputs.Chemicals.Select(chemical => new ChemicalState
                    {
                        Chemical = chemical.Chemical,
                        Addiction = OptionalFact(chemical.HasAddiction, chemical.Addiction),
                        Withdrawal = chemical.Withdrawal,
                        Tolerance = OptionalFact(chemical.HasTolerance, chemical.Tolerance),
                    }),
                ],
            }
        );
    }
}
