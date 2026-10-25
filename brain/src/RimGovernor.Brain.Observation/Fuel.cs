namespace RimGovernor.Brain.Observation;

public static class FuelReads
{
    /// <summary>
    /// The power census row as a refuelable building, absent when its def has no refuelable comp.
    /// Contradictory native and catalog comp facts fail the read. The def's burn is the
    /// catalog's; the fuel, switch and power state are the census's.
    /// </summary>
    internal static FuelConsumer? FuelConsumerOf(DefinitionCatalog catalog, string id, string definition, PowerBuilding b)
    {
        if (definition == "")
        {
            throw new ObservationContractException($"fuel consumer {id} has no definition");
        }
        if (catalog.RefuelBurn(definition) is not { } burn)
        {
            return b.Fuel is O.BuildingFuel.NotRefuelable ? null
                : throw new ObservationContractException($"fuel consumer {id} has no catalog burn");
        }
        var fuel = b.Fuel as O.BuildingFuel.Refuelable
            ?? throw new ObservationContractException($"fuel consumer {id} has no refuelable comp");
        var fuels = new List<Resource>(b.FuelDefinitions.Count);
        foreach (var f in b.FuelDefinitions)
        {
            fuels.Add(new Resource(f));
        }
        return new FuelConsumer
        {
            Id = id,
            Definition = definition,
            Fuel = fuel.Fuel,
            Target = fuel.TargetFuel,
            OutOfFuel = fuel.OutOfFuel,
            SwitchedOn = b.SwitchedOn,
            Powered = b.Powered,
            Burn = burn,
            Fuels = fuels,
        };
    }
}
