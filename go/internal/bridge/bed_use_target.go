package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// BedUseTarget refreshes one exact bed's use flags and owners
// from the rimgovernor/observations_list_buildings row.
type BedUseTarget struct {
	Context *c.ObservationContext
	Thing   string
	Medical bool
	// Prisoners is the bed set for prisoners (#880).
	Prisoners bool
	Owners    []string
}

// ReadBedUseTarget observes one exact bed's medical flag, owners and
// CAS token via the existing building lookup.
func (client *Client) ReadBedUseTarget(ctx context.Context, identity *c.Identity, thing string) (BedUseTarget, Result, error) {
	if validID(thing) != nil {
		return BedUseTarget{}, Result{}, contract("invalid bed medical target identity")
	}
	reply, raw, err := client.ReadConstructionBuildings(ctx, identity, []string{thing})
	if err != nil {
		return BedUseTarget{}, raw, err
	}
	v := reply.GetObserved()
	if v == nil || len(v.Buildings) != 1 {
		return BedUseTarget{}, raw, contract("bed medical target missing or ambiguous")
	}
	row := v.Buildings[0]
	e := row.GetBuilding()
	if e == nil || e.GetId() != thing {
		return BedUseTarget{}, raw, contract("bed medical target identity mismatch")
	}
	settings := row.GetSettings()
	if settings == nil || settings.Snapshot == nil || settings.Snapshot.GetEntityId() != thing || settings.Medical == nil || settings.TargetTemperatureC != nil {
		return BedUseTarget{}, raw, contract("building is not a bed")
	}
	return BedUseTarget{Context: v.Context, Thing: thing, Medical: settings.GetMedical(), Prisoners: settings.GetForPrisoners(), Owners: RefIDs(settings.AssignedPawns)}, raw, nil
}
