using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Domain;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The recipe facts of the gear bench census (<see cref="GameClient.ReadGearBenchesAsync(RimGovernor.Brain.GameFacts.Common.Identity, Func{CancellationToken, Task{GearRecipeCatalogAdapter}}, CancellationToken)"/>) read from the definition catalog; the
/// catalog's <c>Recipe*</c> methods carry the same names and answer the same questions.
/// </summary>
internal sealed class GearRecipeCatalogAdapter(BridgeCatalog catalog)
{
    /// <inheritdoc />
    public RecipeRole RecipeRoleOf(string name) => catalog.RecipeRoleOf(name);

    /// <inheritdoc />
    public IReadOnlyList<P.Resource> RecipeProducts(string name) => catalog.RecipeProducts(name);

    /// <inheritdoc />
    public EquatableArray<EquatableArray<P.Amount>> RecipeIngredients(string name) => catalog.RecipeIngredients(name);

    /// <inheritdoc />
    public bool RecipeFillable(string name) => catalog.RecipeFillable(name);

    /// <inheritdoc />
    public EquatableArray<P.WorkRequirement> RecipeWork(string name, string bench) => catalog.RecipeWork(name, bench);

    /// <inheritdoc />
    public bool RecipeWorkedAt(string name, string bench) => catalog.RecipeWorkedAt(name, bench);

    /// <inheritdoc />
    public double RecipeWorkAmount(string name) => catalog.RecipeWorkAmount(name);
    public string RecipeWorkSpeedStat(string name) => catalog.RecipeWorkSpeedStat(name);

    /// <inheritdoc />
    public string RecipeMechKind(string name) => catalog.RecipeMechKind(name);

    /// <inheritdoc />
    public P.LedgerBillKind RecipeLedgerKind(string name, string mechKind) => catalog.RecipeLedgerKind(name, mechKind);

    /// <inheritdoc />
    public P.ArmoryRung RecipeArmoryTier(string name, string bench) => catalog.RecipeArmoryTier(name, bench);
}
