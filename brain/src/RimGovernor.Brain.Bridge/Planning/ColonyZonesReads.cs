using Identity = RimGovernor.Brain.Bridge.Contracts.ObservationIdentity;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge.Contracts;
using O = RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Bridge;

public static class ColonyZonesReads
{
    /// <summary>Projects the total same-frame zones before crop and gear decisions price their outputs.</summary>
    internal static ColonyReading ApplyZones(ColonyReading p, O.ZonesSnapshot? read)
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
        p = p with { Tables = p.Tables with { Zones = read }, Zones = new Held<O.ZonesSnapshot> { Value = read, AsOf = read.Context.Tick, Complete = true, Source = "rimgovernor/snapshot_frame" } };
        List<O.ZoneState.Growing> farms = [.. read.Zones.OfType<O.ZoneState.Growing>()];
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
