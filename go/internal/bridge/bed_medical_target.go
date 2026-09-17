package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// BedMedicalTarget refreshes one exact humanlike bed's medical-flag CAS
// snapshot token, the same scoped-refresh shape BuildingTemperatureTarget
// uses. The token (NativeBedMedical.Snapshot, on the same
// rimgovernor/observations_list_buildings row) covers the flag and the
// bed's owner set, since the game's setter drops every owner.
type BedMedicalTarget struct {
	Context *c.ObservationContext
	Thing   string
	Token   string
	Medical bool
	Owners  []string
}

// ReadBedMedicalTarget observes one exact bed's medical flag, owners and
// CAS token via the existing building lookup.
func (client *Client) ReadBedMedicalTarget(ctx context.Context, identity *c.Identity, thing string) (BedMedicalTarget, Result, error) {
	if validID(thing) != nil {
		return BedMedicalTarget{}, Result{}, contract("invalid bed medical target identity")
	}
	reply, raw, err := client.ReadConstructionBuildings(ctx, identity, []string{thing})
	if err != nil {
		return BedMedicalTarget{}, raw, err
	}
	v := reply.GetObserved()
	if v == nil || len(v.Buildings) != 1 {
		return BedMedicalTarget{}, raw, contract("bed medical target missing or ambiguous")
	}
	row := v.Buildings[0]
	e := row.GetBuilding()
	if e == nil || e.GetId() != thing {
		return BedMedicalTarget{}, raw, contract("bed medical target identity mismatch")
	}
	settings := row.GetSettings()
	if settings == nil || settings.Snapshot == nil || settings.Snapshot.GetEntityId() != thing || settings.Medical == nil || settings.TargetTemperatureC != nil {
		return BedMedicalTarget{}, raw, contract("building is not a humanlike bed")
	}
	return BedMedicalTarget{Context: v.Context, Thing: thing, Token: settings.Snapshot.GetToken(), Medical: settings.GetMedical(), Owners: append([]string{}, settings.AssignedPawnIds...)}, raw, nil
}
