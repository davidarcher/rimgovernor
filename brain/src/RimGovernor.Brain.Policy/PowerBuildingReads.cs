using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Policy views of the single observed building row; no service values are copied.</summary>
public static class PowerBuildingReads
{
    extension(O.BuildingState building)
    {
        public string Id => building.Building.Id;
        public string Definition => building.Building.DefName;
        public Cell Cell => building.Building.Place is O.EntityPlace.OnMap map
            ? map.Cell
            : throw new DomainException($"Power building {building.Building.Id} is not on a map.");
        public IReadOnlyList<Cell> OccupiedCells => CellsRules.RectCells(building.Occupied);
        public double BaseW => building.Service.Power switch
        {
            O.BuildingPower.Trader trader => trader.RatedW,
            O.BuildingPower.Battery => 0,
            var invalid => throw new DomainException($"Building {building.Building.Id} is not a power member."),
        };
        public double OutputW => building.Service.Power switch
        {
            O.BuildingPower.Trader trader => trader.OutputW,
            O.BuildingPower.Battery => 0,
            var invalid => throw new DomainException($"Building {building.Building.Id} is not a power member."),
        };
        public string Network => building.Service.Power switch
        {
            O.BuildingPower.Trader trader => trader.Network ?? "",
            O.BuildingPower.Battery battery => battery.Network ?? "",
            var invalid => throw new DomainException($"Building {building.Building.Id} is not a power member."),
        };
        public bool Connected => building.Network.Length > 0;
        public bool Powered => building.Service.Power switch
        {
            O.BuildingPower.Trader trader => trader.PowerOn,
            O.BuildingPower.Battery battery => battery.Network is not null,
            var invalid => throw new DomainException($"Building {building.Building.Id} is not a power member."),
        };
        public double StorageWD => building.Service.Power switch
        {
            O.BuildingPower.Trader => 0,
            O.BuildingPower.Battery battery => battery.StorageWD,
            var invalid => throw new DomainException($"Building {building.Building.Id} is not a power member."),
        };
        public bool Forbidden => building.Settings.Forbidden;
        public bool SwitchedOn => building.Service.SwitchedOn;
        public O.BuildingFuel Fuel => building.Service.Fuel;
        public bool BrokenDown => building.Service.BrokenDown;
        public O.BuildingFuel.Refuelable Refuelable => building.Service.Fuel as O.BuildingFuel.Refuelable
            ?? throw new DomainException($"Building {building.Building.Id} is not refuelable.");
        public double Dps => building.Turret?.Dps ?? throw new DomainException($"Building {building.Building.Id} has no turret gun.");
        public double FuelAmount => building.Refuelable.Fuel;
        public double Target => building.Refuelable.TargetFuel;
        public bool OutOfFuel => building.Unfueled;
        public O.FuelBurn Burn => building.Refuelable.Burn;
        public bool Unfueled => building.Service.Fuel is O.BuildingFuel.Refuelable { OutOfFuel: true };
    }
}
