package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
)

// rotations maps a native cardinal rotation onto the domain's; any other
// value maps to the empty rotation, which domain validation refuses.
var rotations = map[p.Rotation]domain.Rotation{p.Rotation_ROTATION_NORTH: domain.North, p.Rotation_ROTATION_EAST: domain.East, p.Rotation_ROTATION_SOUTH: domain.South, p.Rotation_ROTATION_WEST: domain.West}

// siteStages maps the blueprint and frame stages onto policy's.
var siteStages = map[o.BuildingStatus]string{o.BuildingStatus_BUILDING_STATUS_BLUEPRINT: "blueprint", o.BuildingStatus_BUILDING_STATUS_FRAME: "frame"}

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
		b, err := domain.NewBuilding(row.Building.GetDefName(), domain.Cell{X: row.Building.Position.GetX(), Z: row.Building.Position.GetZ()}, rotations[row.GetRotation()], stuff)
		if err != nil {
			return unknown, err
		}
		cells := bridge.RectCells(row.Occupied)
		if row.Occupied != nil && cells == nil {
			return unknown, ErrContract
		}
		r.Buildings = append(r.Buildings, policy.CurrentBuilding{ID: row.Building.GetId(), Building: b, Cells: cells})
	}
	return domain.Known(r), nil
}

// WithSites adds the player's standing blueprints and frames (the
// unfiltered buildings read) to a colony census: an applied building
// intent with a site standing as its building is still open (#1355).
func WithSites(census domain.Fact[policy.CurrentConstruction], v *o.BuildingsSnapshot) (domain.Fact[policy.CurrentConstruction], error) {
	r, known := census.Value()
	if !known || !r.Colony {
		return census, nil
	}
	r.Sites = []policy.ConstructionSite{}
	for _, row := range v.GetBuildings() {
		stage, ok := siteStages[row.GetStatus()]
		if !ok || row.GetBuildDefName() == "" {
			continue
		}
		b, err := domain.NewBuilding(row.GetBuildDefName(), domain.Cell{X: row.Building.GetPosition().GetX(), Z: row.Building.GetPosition().GetZ()}, rotations[row.GetRotation()], row.GetStuff())
		if err != nil {
			return domain.Unknown[policy.CurrentConstruction](), err
		}
		r.Sites = append(r.Sites, policy.ConstructionSite{Building: b, Stage: stage})
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
		case o.BuildingStatus_BUILDING_STATUS_BLUEPRINT, o.BuildingStatus_BUILDING_STATUS_FRAME:
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

// BillReservations reads every bill row's live bill-job reservations.
// Unknown without the bill census or when any row is malformed.
func BillReservations(v *o.BillsSnapshot) domain.Fact[[]policy.IngredientReservation] {
	unknown := domain.Unknown[[]policy.IngredientReservation]()
	if v == nil {
		return unknown
	}
	out := []policy.IngredientReservation{}
	for _, bench := range v.Benches {
		for _, bill := range bench.GetBills() {
			for _, r := range bill.GetReservations() {
				if r.GetPawnId() == "" {
					return unknown
				}
				row := policy.IngredientReservation{Pawn: policy.PawnID(r.GetPawnId())}
				for _, item := range r.GetItems() {
					if item.GetDefName() == "" || item.Units == nil || item.GetUnits() < 0 {
						return unknown
					}
					row.Items = append(row.Items, policy.Amount{Resource: policy.Resource(item.GetDefName()), Count: item.GetUnits()})
				}
				out = append(out, row)
			}
		}
	}
	return domain.Known(out)
}
