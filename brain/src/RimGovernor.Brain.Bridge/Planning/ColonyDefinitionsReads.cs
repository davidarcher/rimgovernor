using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge.Contracts;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
namespace RimGovernor.Brain.Bridge;

public static partial class ColonyPlanningReads
{
    /// <summary>Joins the frame's required catalog to additional planning definition requests.</summary>
    public static ColonyReading AddDefinitions(ColonyReading reading, RoundsFrame frame, DefinitionCatalog? catalog, IEnumerable<string> names)
    {
        var definitions = RoundsFrameReads.FrameDefinitionFacts(frame, RequireCatalog(catalog))
            .AppendDefinitions([.. reading.Facts.Definitions], names);
        return reading with { Facts = reading.Facts with { Definitions = definitions } };
    }
}