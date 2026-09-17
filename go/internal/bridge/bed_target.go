package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// BedTarget refreshes one exact bed's CAS snapshot token. Beds are player
// buildings like any repaired structure, so this reuses the existing exact-ID
// ReadConstructionBuildings lookup, the same way ReadRepairTarget refreshes a
// structure's token; the general upkeep census deliberately strips snapshot
// tokens from its rows (see bridge/colony_upkeep.go), so a fresh scoped read
// is required before dispatch.
type BedTarget struct {
	Context *c.ObservationContext
	Bed     string
	Token   string
}

// ReadBedTarget observes one exact bed by ID via the existing building
// lookup, which already validates identity, geometry and any CAS snapshot
// ref present on the row.
func (client *Client) ReadBedTarget(ctx context.Context, identity *c.Identity, bed string) (BedTarget, Result, error) {
	if validID(bed) != nil {
		return BedTarget{}, Result{}, contract("invalid bed target identity")
	}
	reply, raw, err := client.ReadConstructionBuildings(ctx, identity, []string{bed})
	if err != nil {
		return BedTarget{}, raw, err
	}
	v := reply.GetObserved()
	if v == nil || len(v.Buildings) != 1 {
		return BedTarget{}, raw, contract("bed target missing or ambiguous")
	}
	row := v.Buildings[0]
	e := row.GetBuilding()
	if e == nil || e.GetId() != bed {
		return BedTarget{}, raw, contract("bed target identity mismatch")
	}
	if e.Snapshot == nil {
		return BedTarget{}, raw, contract("bed target CAS token unavailable")
	}
	return BedTarget{Context: v.Context, Bed: bed, Token: e.Snapshot.GetToken()}, raw, nil
}
