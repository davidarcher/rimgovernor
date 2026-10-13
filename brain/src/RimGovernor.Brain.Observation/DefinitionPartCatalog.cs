namespace RimGovernor.Brain.Observation;

/// <summary>The game-Def catalog as the surgery facts' part-catalog seam (<see cref="IPartCatalog"/>): Bridge does not reference the contracts, so Observation adapts it.</summary>
public sealed class DefinitionPartCatalog(DefinitionCatalog catalog) : IPartCatalog
{
    public Resource InstallItem(string recipe) => catalog.InstallItem(recipe);

    public double PartTier(string recipe) => catalog.PartTier(recipe);

    public double HediffPartTier(string hediff) => catalog.HediffPartTier(hediff);

    public Fact<Resource> HediffSpawnOnRemoved(string hediff) => catalog.HediffSpawnOnRemoved(hediff);
}
