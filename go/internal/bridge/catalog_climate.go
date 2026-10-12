package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// climateModel is the heat the climate options move (policy.ClimateModel),
// read from the defs and the game's constants: the vent's equalize interval
// (GenTicks.TickRareInterval, Building_Vent.TickRare) and rate
// (TemperatureTuning.Vent_TempEqualizeRate, the 14f in TickRare), the door's
// intervals and rate from its BuildingProperties, the cheapest heater's and the
// most efficient cooler's heat per second and power draw from their temperature
// control and power comps, and the cheapest cost list of the vent, door and wall.
// stations is the catalog being built, whose coolers it reads.
func (catalog *DefinitionCatalog) climateModel(stations policy.StationCatalog) (policy.ClimateModel, error) {
	// A catalog without the defs or constants (a hand-built one) has an unknown
	// model, not an error: the advice that reads it is left off.
	constants, err := catalog.GameConstants()
	if err != nil {
		return policy.ClimateModel{}, nil
	}
	table := policy.ClimateDefs()
	for _, def := range []string{table.VentDef, table.DoorDef, table.WallDef} {
		if _, ok := catalog.ThingDefs[def]; !ok {
			return policy.ClimateModel{}, nil
		}
	}
	building := catalog.ThingDefs[table.DoorDef].GetBuilding()
	rare := int(constants.GetGenTicks().GetTickRareInterval())
	tuning := constants.GetTemperatureTuning()
	m := policy.ClimateModel{
		Vent:           policy.Exchange{IntervalTicks: rare, Rate: float64(tuning.GetVent_TempEqualizeRate())},
		DoorOpen:       policy.Exchange{IntervalTicks: int(building.GetDoorTempEqualizeIntervalOpen()), Rate: float64(building.GetDoorTempEqualizeRate())},
		DoorClosed:     policy.Exchange{IntervalTicks: int(building.GetDoorTempEqualizeIntervalClosed()), Rate: float64(building.GetDoorTempEqualizeRate())},
		StepTicks:      rare,
		TicksPerSecond: float64(constants.GetGenTicks().GetTicksPerRealSecond()),
		SwingC:         float64(tuning.GetDailyTempVariationAmplitude()),
	}
	heater, err := catalog.cheapestHeater()
	if err != nil {
		return policy.ClimateModel{}, nil
	}
	control := compOf(catalog.ThingDefs[heater], (*d.CompPropertiesAny).GetCompProperties_TempControl)
	power := compOf(catalog.ThingDefs[heater], (*d.CompPropertiesAny).GetCompProperties_Power)
	m.Heater = policy.ClimatePlant{HeatPerSecond: float64(control.GetEnergyPerSecond()), PowerW: float64(power.GetBasePowerConsumption())}
	if cooler, found := stations.BestCooler(); found {
		cooling := stations.Stations[cooler].Cooling
		m.Cooler = policy.ClimatePlant{HeatPerSecond: cooling.HeatPerSecond, PowerW: cooling.PowerDraw}
	}
	for def, into := range map[string]*float64{table.VentDef: &m.Build.Vent, table.DoorDef: &m.Build.Door, table.WallDef: &m.Build.Wall} {
		if *into, err = catalog.CheapestCostValue(def); err != nil {
			return policy.ClimateModel{}, err
		}
	}
	return m, nil
}

// lightModel is the lamp the plan places (policy.LightDefs): the power it draws
// and the market value of its cheapest cost list. A catalog without the def (a
// hand-built one) has an unknown model, not an error: the lamps go unpriced.
func (catalog *DefinitionCatalog) lightModel() (policy.LightModel, error) {
	def := policy.LightDefs().LampDef
	row, ok := catalog.ThingDefs[def]
	if !ok {
		return policy.LightModel{}, nil
	}
	watts, powered, err := catalog.PowerDraw(row)
	if err != nil || !powered {
		return policy.LightModel{}, err
	}
	build, err := catalog.CheapestCostValue(def)
	if err != nil {
		return policy.LightModel{}, err
	}
	return policy.LightModel{LampPowerW: watts, LampBuild: build}, nil
}
