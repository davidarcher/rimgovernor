package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// mechCatalog is the frame's Biotech mech defs; the zero value without a
// catalog.
func mechCatalog(catalog *bridge.DefinitionCatalog) policy.MechCatalog {
	if catalog == nil {
		return policy.MechCatalog{}
	}
	return catalog.Biotech.MechCatalog()
}

// roundsMechSettings are the mech control group and work mode settings
// for the routine read's mechs; none without a mechanitor. A
// living hostile pawn on the map makes a single control group escort.
func roundsMechSettings(read observation.RoundsReading) ([]domain.PawnSettings, error) {
	fleet, ok := read.Projection.Mechs.Value()
	if !ok || len(fleet.Mechanitors) == 0 {
		return nil, nil
	}
	hostile := false
	for _, t := range read.Emergency.Threats {
		dead, _ := t.Dead.Value()
		downed, _ := t.Downed.Value()
		hostile = hostile || t.Kind == policy.Hostile && !dead && !downed
	}
	chargerReady := false
	if biotech, known := read.Projection.Biotech.Value(); known {
		chargerReady = policy.MechChargerReady(biotech.MechChargerRows())
	}
	return policy.PlanMechControl(mechCatalog(read.Frame.Catalog), fleet.Mechanitors, fleet.Mechs, hostile, chargerReady)
}

// combatMechGuards are the drafts and attack orders for the combat frame's
// guard mechs at the standing hostiles; none without a mechanitor.
func combatMechGuards(combat bridge.Combat, hostileIDs []string, rows map[string]*n.PawnState) (policy.MechGuardPlan, error) {
	fleet := observation.MechFleet(combat.Detail.Values())
	if len(fleet.Mechanitors) == 0 || len(fleet.Mechs) == 0 {
		return policy.MechGuardPlan{}, nil
	}
	var hostiles []policy.MechHostile
	for _, id := range hostileIDs {
		row := rows[id]
		if row == nil || row.GetDead() || row.GetDowned() {
			continue
		}
		if cell, ok := observation.RowCell(row).Value(); ok {
			hostiles = append(hostiles, policy.MechHostile{ID: policy.PawnID(id), Cell: cell})
		}
	}
	return policy.PlanMechGuards(mechCatalog(combat.Catalog), fleet.Mechanitors, fleet.Mechs, hostiles)
}
