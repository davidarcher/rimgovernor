using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
namespace RimGovernor.Brain.Bridge;

/// <summary>The game-Def catalog as the surgery facts' part-catalog seam (<see cref="IPartCatalog"/>): Bridge does not reference the contracts, so Observation adapts it.</summary>
public sealed class DefinitionPartCatalog(DefinitionCatalog catalog) : IPartCatalog
{
    public Resource InstallItem(string recipe) => catalog.InstallItem(recipe);

    public double PartTier(string recipe) => catalog.PartTier(recipe);
}
