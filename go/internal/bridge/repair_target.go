package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// RepairTarget refreshes one exact player-owned structure's CAS snapshot
// token. The general upkeep census deliberately strips snapshot tokens from
// its rows, so a fresh scoped read is required before dispatch, mirroring
// ReadHaulTargets for loose things.
type RepairTarget struct {
	Context                 *c.ObservationContext
	Structure               string
	Token                   string
	HitPoints, MaxHitPoints int64
}

// ReadRepairTarget observes one exact structure by ID via the existing
// building lookup, which already validates identity, geometry and any
// CAS snapshot ref present on the row.
func (client *Client) ReadRepairTarget(ctx context.Context, identity *c.Identity, structure string) (RepairTarget, Result, error) {
	if validID(structure) != nil {
		return RepairTarget{}, Result{}, contract("invalid repair target identity")
	}
	reply, raw, err := client.ReadConstructionBuildings(ctx, identity, []string{structure})
	if err != nil {
		return RepairTarget{}, raw, err
	}
	v := reply.GetObserved()
	if v == nil || len(v.Buildings) != 1 {
		return RepairTarget{}, raw, contract("repair target missing or ambiguous")
	}
	row := v.Buildings[0]
	e := row.GetBuilding()
	if e == nil || e.GetId() != structure {
		return RepairTarget{}, raw, contract("repair target identity mismatch")
	}
	if e.Snapshot == nil {
		return RepairTarget{}, raw, contract("repair target CAS token unavailable")
	}
	if row.HitPoints == nil || row.MaxHitPoints == nil {
		return RepairTarget{}, raw, contract("repair target hit points unknown")
	}
	return RepairTarget{Context: v.Context, Structure: structure, Token: e.Snapshot.GetToken(), HitPoints: int64(row.GetHitPoints()), MaxHitPoints: int64(row.GetMaxHitPoints())}, raw, nil
}
