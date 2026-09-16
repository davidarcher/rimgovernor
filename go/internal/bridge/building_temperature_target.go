package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// BuildingTemperatureTarget refreshes one exact player-owned building's
// target-temperature CAS snapshot token, the same scoped-refresh shape
// RepairTarget uses for repair/breakdown/refuel dispatch. Unlike
// RepairTarget's row-identity token, this one is specific to the settable
// target temperature (see NativeBuildingTemperature.Snapshot on the native
// side, wired into NativeBuildingObservationTools' existing
// rimgovernor/observations_list_buildings reply for any thing with
// CompTempControl), so it changes exactly when the setpoint does.
type BuildingTemperatureTarget struct {
	Context     *c.ObservationContext
	Thing       string
	Token       string
	Temperature float64
}

// ReadBuildingTemperatureTarget observes one exact building's target
// temperature and its CAS token via the existing building lookup.
func (client *Client) ReadBuildingTemperatureTarget(ctx context.Context, identity *c.Identity, thing string) (BuildingTemperatureTarget, Result, error) {
	if validID(thing) != nil {
		return BuildingTemperatureTarget{}, Result{}, contract("invalid building temperature target identity")
	}
	reply, raw, err := client.ReadConstructionBuildings(ctx, identity, []string{thing})
	if err != nil {
		return BuildingTemperatureTarget{}, raw, err
	}
	v := reply.GetObserved()
	if v == nil || len(v.Buildings) != 1 {
		return BuildingTemperatureTarget{}, raw, contract("building temperature target missing or ambiguous")
	}
	row := v.Buildings[0]
	e := row.GetBuilding()
	if e == nil || e.GetId() != thing {
		return BuildingTemperatureTarget{}, raw, contract("building temperature target identity mismatch")
	}
	settings := row.GetSettings()
	if settings == nil || settings.Snapshot == nil || settings.Snapshot.GetEntityId() != thing || settings.TargetTemperatureC == nil {
		return BuildingTemperatureTarget{}, raw, contract("building has no target temperature control")
	}
	return BuildingTemperatureTarget{Context: v.Context, Thing: thing, Token: settings.Snapshot.GetToken(), Temperature: settings.GetTargetTemperatureC()}, raw, nil
}
