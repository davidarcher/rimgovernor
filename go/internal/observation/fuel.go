package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// fuelConsumer is the power census row as a refuelable building: false when
// its def has no refuelable comp (or no catalog is read), so a building that
// cannot be refueled is not a consumer. The def's burn is the catalog's; the
// fuel, switch and power state are the census's.
func fuelConsumer(catalog *bridge.DefinitionCatalog, id, definition string, b policy.PowerBuilding) (policy.FuelConsumer, bool, error) {
	if catalog == nil || definition == "" {
		return policy.FuelConsumer{}, false, nil
	}
	burn, err := catalog.RefuelBurn(definition)
	if err != nil {
		return policy.FuelConsumer{}, false, err
	}
	if _, refuelable := burn.Value(); !refuelable {
		return policy.FuelConsumer{}, false, nil
	}
	fuels := make([]policy.Resource, 0, len(b.FuelDefinitions))
	for _, f := range b.FuelDefinitions {
		fuels = append(fuels, policy.Resource(f))
	}
	return policy.FuelConsumer{ID: id, Definition: definition, Fuel: b.Fuel, Target: b.TargetFuel, OutOfFuel: b.OutOfFuel, SwitchedOn: b.SwitchedOn, Powered: b.Powered, Burn: burn, Fuels: fuels}, true, nil
}
