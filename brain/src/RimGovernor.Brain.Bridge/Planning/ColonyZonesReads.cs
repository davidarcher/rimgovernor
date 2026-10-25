using Identity = RimGovernor.Brain.GameAccess.ObservationIdentity;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
namespace RimGovernor.Brain.Bridge;

public static class ColonyZonesReads
{
    /// <summary>Projects the total same-frame zones before crop and gear decisions price their outputs.</summary>
    internal static ColonyReading ApplyZones(ColonyReading p, RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot? read)
    {
        if (read is null)
        {
            throw new BridgeContractException("missing total zones census");
        }
        var actual = new ObservationIdentity { Context = read.Context } with { Paused = p.Identity.Paused };
        if (!ColonyContexts.SameColonyContext(actual, p.Identity))
        {
            throw new ObservationChangedException();
        }
        p = p with { Tables = p.Tables with { Zones = read }, Zones = new Held<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot> { Value = read, AsOf = read.Context.Tick, Complete = true, Source = "snapshot_frame" } };
        List<RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing> farms = [.. read.Zones.OfType<RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing>()];
        p = p with { Facts = ColonyProductionReads.ZoneProduction(farms, p.Facts.Definitions, p.Facts) };
        return p with
        {
            Facts = p.Facts with
            {
            FieldCrops = ColonyProductionReads.ColonyFieldCrops(farms, p.Facts.Definitions),
            FoodFields = ColonyProductionReads.ColonyFoodFields(farms, p.Facts.Definitions, p.Facts.Calendar),
            FieldCapacityCrops = ColonyProductionReads.ColonyFieldCrops(farms, p.Facts.Definitions, true),
            },
        };
    }

}
