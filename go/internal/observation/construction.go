package observation

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// ConstructionBuildings projects a validated player-only building read. Empty IDs
// identifies a complete colony census, independent of controller action history.
func ConstructionBuildings(v *o.BuildingsSnapshot, ids []string) (domain.Fact[policy.CurrentConstruction], error) {
	unknown := domain.Unknown[policy.CurrentConstruction]()
	r := policy.CurrentConstruction{Colony: len(ids) == 0, Requested: append([]string{}, ids...), Buildings: []policy.CurrentBuilding{}}
	for _, row := range v.Buildings {
		stuff := row.GetStuff()
		if row.Stuff == nil {
			notApplicable := false
			for _, issue := range row.Issues {
				notApplicable = notApplicable || issue.GetField() == "stuff" && issue.GetUnavailable().GetReason() == c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE
			}
			if !notApplicable {
				return unknown, nil
			}
		}
		b, err := domain.NewBuilding(row.Building.GetDefName(), domain.Cell{X: row.Building.Position.GetX(), Z: row.Building.Position.GetZ()}, domain.Rotation(strings.ToLower(row.GetRotation())), stuff)
		if err != nil {
			return unknown, err
		}
		cells := make([]domain.Cell, 0, len(row.OccupiedCells))
		for _, cell := range row.OccupiedCells {
			if cell == nil || cell.X == nil || cell.Z == nil {
				return unknown, ErrContract
			}
			cells = append(cells, domain.Cell{X: cell.GetX(), Z: cell.GetZ()})
		}
		r.Buildings = append(r.Buildings, policy.CurrentBuilding{ID: row.Building.GetId(), Building: b, Cells: cells, IntentKey: row.GetIntentKey()})
	}
	for _, in := range v.Intents {
		if in.GetKey() == "" {
			return unknown, ErrContract
		}
		r.Intents = append(r.Intents, policy.ConstructionIntent{Key: in.GetKey(), Stage: in.GetStage()})
	}
	return domain.Known(r), nil
}

// ConstructionDeficit sums, per material, what the player's standing
// blueprints and frames are still owed (ConstructionState.resources[]
// still_needed). Unknown without the read or when any row's need is.
func ConstructionDeficit(v *o.BuildingsSnapshot) domain.Fact[map[policy.Resource]int64] {
	unknown := domain.Unknown[map[policy.Resource]int64]()
	if v == nil {
		return unknown
	}
	out := map[policy.Resource]int64{}
	for _, row := range v.Buildings {
		switch row.GetStatus() {
		case "blueprint", "frame":
		default:
			continue
		}
		for _, need := range row.GetConstruction().GetResources() {
			if need == nil || need.GetDefName() == "" || need.StillNeeded == nil || need.GetStillNeeded() < 0 {
				return unknown
			}
			if need.GetStillNeeded() > 0 {
				out[policy.Resource(need.GetDefName())] += need.GetStillNeeded()
			}
		}
	}
	return domain.Known(out)
}
