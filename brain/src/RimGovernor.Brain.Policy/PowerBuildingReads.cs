
namespace RimGovernor.Brain.Policy;

/// <summary>Policy views of the single observed building row; no service values are copied.</summary>
public static class PowerBuildingReads
{
    extension(RimGovernor.Brain.GameFacts.Observations.BuildingState building)
    {
        public string Id => building.Building.Id;
        public string Definition => building.Building.DefName;
        public Cell Cell => building.Building.Place is RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap map
            ? map.Cell
            : throw new DomainException($"Power building {building.Building.Id} is not on a map.");
        public IReadOnlyList<Cell> OccupiedCells => CellsRules.RectCells(building.Occupied);
        public double BaseW => building.Service.Power switch
        {
            RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader trader => trader.RatedW,
            RimGovernor.Brain.GameFacts.Observations.BuildingPower.Battery => 0,
            var invalid => throw new DomainException($"Building {building.Building.Id} is not a power member."),
        };
        public double OutputW => building.Service.Power switch
        {
            RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader trader => trader.OutputW,
            RimGovernor.Brain.GameFacts.Observations.BuildingPower.Battery => 0,
            var invalid => throw new DomainException($"Building {building.Building.Id} is not a power member."),
        };
        public string Network => building.Service.Power switch
        {
            RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader trader => trader.Network ?? "",
            RimGovernor.Brain.GameFacts.Observations.BuildingPower.Battery battery => battery.Network ?? "",
            var invalid => throw new DomainException($"Building {building.Building.Id} is not a power member."),
        };
        public bool Connected => building.Network.Length > 0;
        public bool Powered => building.Service.Power switch
        {
            RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader trader => trader.PowerOn,
            RimGovernor.Brain.GameFacts.Observations.BuildingPower.Battery battery => battery.Network is not null,
            var invalid => throw new DomainException($"Building {building.Building.Id} is not a power member."),
        };
        public double StorageWD => building.Service.Power switch
        {
            RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader => 0,
            RimGovernor.Brain.GameFacts.Observations.BuildingPower.Battery battery => battery.StorageWD,
            var invalid => throw new DomainException($"Building {building.Building.Id} is not a power member."),
        };
        public bool Forbidden => building.Settings.Forbidden;
        public bool SwitchedOn => building.Service.SwitchedOn;
        public RimGovernor.Brain.GameFacts.Observations.BuildingFuel Fuel => building.Service.Fuel;
        public bool BrokenDown => building.Service.BrokenDown;
        public RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable Refuelable => building.Service.Fuel as RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable
            ?? throw new DomainException($"Building {building.Building.Id} is not refuelable.");
        public double Dps => building.Turret?.Dps ?? throw new DomainException($"Building {building.Building.Id} has no turret gun.");
        public double FuelAmount => building.Refuelable.Fuel;
        public double Target => building.Refuelable.TargetFuel;
        public bool OutOfFuel => building.Unfueled;
        public RimGovernor.Brain.GameFacts.Observations.FuelBurn Burn => building.Refuelable.Burn;
        public bool Unfueled => building.Service.Fuel is RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { OutOfFuel: true };
    }
}
