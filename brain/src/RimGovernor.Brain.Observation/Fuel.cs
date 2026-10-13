namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    /// <summary>
    /// The power census row as a refuelable building: refuelable is false when its def has no refuelable comp (or
    /// no catalog is read), so a building that cannot be refueled is not a consumer. The def's burn is the
    /// catalog's; the fuel, switch and power state are the census's.
    /// </summary>
    internal static FuelConsumer FuelConsumerOf(DefinitionCatalog? catalog, string id, string definition, PowerBuilding b, out bool refuelable)
    {
        refuelable = false;
        if (catalog is null || definition == "")
        {
            return new FuelConsumer();
        }
        var burn = catalog.RefuelBurn(definition);
        if (!burn.IsKnown)
        {
            return new FuelConsumer();
        }
        var fuels = new List<Resource>(b.FuelDefinitions.Count);
        foreach (var f in b.FuelDefinitions)
        {
            fuels.Add(new Resource(f));
        }
        refuelable = true;
        return new FuelConsumer
        {
            Id = id,
            Definition = definition,
            Fuel = b.Fuel,
            Target = b.TargetFuel,
            OutOfFuel = b.OutOfFuel,
            SwitchedOn = b.SwitchedOn,
            Powered = b.Powered,
            Burn = burn,
            Fuels = fuels,
        };
    }
}
