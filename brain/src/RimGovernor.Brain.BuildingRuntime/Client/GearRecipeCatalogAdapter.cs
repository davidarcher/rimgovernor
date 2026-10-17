using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The recipe facts of the gear bench census (<see cref="FacadeNativeClient.ReadGearBenchesAsync(PbCommon.Identity, Func{CancellationToken, Task{GearRecipeCatalogAdapter}}, CancellationToken)"/>) read from the definition catalog; the
/// catalog's <c>Recipe*</c> methods carry the same names and answer the same questions.
/// </summary>
internal sealed class GearRecipeCatalogAdapter(BridgeCatalog catalog)
{
    /// <inheritdoc />
    public RecipeRole RecipeRoleOf(string name) => catalog.RecipeRoleOf(name);

    /// <inheritdoc />
    public IReadOnlyList<P.Resource> RecipeProducts(string name) => catalog.RecipeProducts(name);

    /// <inheritdoc />
    public Fact<EquatableArray<EquatableArray<P.Amount>>> RecipeIngredients(string name) => catalog.RecipeIngredients(name);

    /// <inheritdoc />
    public Fact<EquatableArray<P.WorkRequirement>> RecipeWork(string name, string bench) => catalog.RecipeWork(name, bench);

    /// <inheritdoc />
    public Fact<double> RecipeWorkAmount(string name) => catalog.RecipeWorkAmount(name);

    /// <inheritdoc />
    public string RecipeMechKind(string name) => catalog.RecipeMechKind(name);

    /// <inheritdoc />
    public P.LedgerBillKind RecipeLedgerKind(string name, string mechKind) => catalog.RecipeLedgerKind(name, mechKind);

    /// <inheritdoc />
    public P.ArmoryTier RecipeArmoryTier(string name, string bench) => catalog.RecipeArmoryTier(name, bench);
}
