package observation

import (
	"context"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type ConstructionSource interface {
	ReadConstructionBuildings(context.Context, *c.Identity, []string) (*o.ListBuildingsReply, bridge.Result, error)
}

func (s *routineBracket) readConstruction(ctx context.Context, id *c.Identity) error {
	source, available := s.RoutineSource.(ConstructionSource)
	if !available {
		return nil
	}
	var ids []string
	reply, _, err := source.ReadConstructionBuildings(ctx, id, ids)
	if reply != nil && reply.GetUnavailable() != nil {
		return nil
	}
	if err != nil {
		return err
	}
	if reply == nil || reply.GetObserved() == nil {
		return ErrContract
	}
	snapshot := reply.GetObserved()
	if err := bridge.ValidateConstructionBuildings(snapshot, id, ids); err != nil {
		return err
	}
	observed, err := contextIdentity(snapshot.Context)
	if err != nil {
		return err
	}
	observed.Paused = s.expected.Paused
	if !cachedColonyBoundary(observed, s.expected, bridge.FactColony) {
		return ErrChanged
	}
	s.construction, err = ConstructionBuildings(snapshot, ids)
	return err
}

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
		r.Buildings = append(r.Buildings, policy.CurrentBuilding{ID: row.Building.GetId(), Building: b, Cells: cells})
	}
	return domain.Known(r), nil
}
