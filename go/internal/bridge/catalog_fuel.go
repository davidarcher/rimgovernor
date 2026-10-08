package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// RefuelBurn is the burn the named def's CompProperties_Refuelable states
// (policy.FuelBurn): an unknown fact for a def without the comp, so a
// building that cannot be refueled is not a fuel consumer.
func (catalog *DefinitionCatalog) RefuelBurn(name string) (domain.Fact[policy.FuelBurn], error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return domain.Fact[policy.FuelBurn]{}, err
	}
	fuel := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Refuelable)
	if fuel == nil {
		return domain.Fact[policy.FuelBurn]{}, nil
	}
	return domain.Known(policy.FuelBurn{
		PerDay:       float64(fuel.GetFuelConsumptionRate()),
		UnitsPerItem: float64(fuel.GetFuelMultiplier()),
		UseDriven:    fuel.GetConsumeFuelOnlyWhenUsed() || fuel.GetExternalTicking(),
		InRain:       fuel.GetFuelConsumptionPerTickInRain() > 0,
		WhenPowered:  fuel.GetConsumeFuelOnlyWhenPowered(),
		Flickable:    compOf(row, (*d.CompPropertiesAny).GetCompProperties_Flickable) != nil,
		Difficulty:   fuel.GetFactorByDifficulty(),
	}), nil
}
