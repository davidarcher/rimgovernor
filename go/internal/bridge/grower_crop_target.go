package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// GrowerCropTarget refreshes one exact plant grower's crop CAS snapshot
// token, the same scoped-refresh shape BedMedicalTarget uses. The token
// (NativeGrowerCrop.Snapshot, on the same
// rimgovernor/observations_list_buildings row) covers the grower's current
// crop; Crop is empty when the grower has none.
type GrowerCropTarget struct {
	Context *c.ObservationContext
	Thing   string
	Token   string
	Crop    string
}

// ReadGrowerCropTarget observes one exact grower's crop and CAS token via
// the existing building lookup.
func (client *Client) ReadGrowerCropTarget(ctx context.Context, identity *c.Identity, thing string) (GrowerCropTarget, Result, error) {
	if validID(thing) != nil {
		return GrowerCropTarget{}, Result{}, contract("invalid grower crop target identity")
	}
	reply, raw, err := client.ReadConstructionBuildings(ctx, identity, []string{thing})
	if err != nil {
		return GrowerCropTarget{}, raw, err
	}
	v := reply.GetObserved()
	if v == nil || len(v.Buildings) != 1 {
		return GrowerCropTarget{}, raw, contract("grower crop target missing or ambiguous")
	}
	row := v.Buildings[0]
	e := row.GetBuilding()
	if e == nil || e.GetId() != thing {
		return GrowerCropTarget{}, raw, contract("grower crop target identity mismatch")
	}
	settings := row.GetSettings()
	if settings == nil || settings.Snapshot == nil || settings.Snapshot.GetEntityId() != thing || settings.Medical != nil || settings.TargetTemperatureC != nil || settings.CropDefName != nil && validID(settings.GetCropDefName()) != nil {
		return GrowerCropTarget{}, raw, contract("building is not a plant grower")
	}
	return GrowerCropTarget{Context: v.Context, Thing: thing, Token: settings.Snapshot.GetToken(), Crop: settings.GetCropDefName()}, raw, nil
}
