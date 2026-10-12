package policy

import (
	"fmt"
	"slices"
)

// RoleMismatchReason is the stable reason a mismatch is recorded under
// (telemetry.Decide reason).
const RoleMismatchReason = "native_role_differs_from_predicted"

// RoomContents is what stands in one planned room: the stations the role
// workers see (PredictRole's input).
type RoomContents struct {
	Room   int32 // PlannedRoom.ID
	Pieces []RoomPiece
}

// RoleMismatch marks a built room whose native role is not the one
// PredictRole gives its contents; the planner re-prices or splits a marked
// room. Its reason is RoleMismatchReason.
type RoleMismatch struct {
	Room              int32
	Native, Predicted RoomRole
	Scores            map[RoomRole]float64
}

// GuardNativeRoles compares each standing room's native role (the census) with
// PredictRole for its contents and returns the rooms that differ, by room id.
// A room that does not stand enclosed on its region, or whose native role is
// unknown, is skipped: there is nothing to compare. Contents naming no
// planned room, or a piece outside the catalog, are an error.
func GuardNativeRoles(catalog StationCatalog, plan LayoutPlan, rooms RoomObservation, contents []RoomContents) ([]RoleMismatch, error) {
	planned := plan.Rooms
	var out []RoleMismatch
	for _, c := range contents {
		idx := slices.IndexFunc(planned, func(r PlannedRoom) bool { return r.ID == c.Room })
		if idx < 0 {
			return nil, fmt.Errorf("contents name room %d, which the plan does not hold", c.Room)
		}
		census, standing := CensusRoomIn(planned[idx], rooms)
		if !standing {
			continue
		}
		native, known := census.Role.Value()
		if !known {
			continue
		}
		pred, err := PredictRole(catalog, c.Pieces)
		if err != nil {
			return nil, fmt.Errorf("room %d: %w", c.Room, err)
		}
		if pred.Role != native {
			out = append(out, RoleMismatch{Room: c.Room, Native: native, Predicted: pred.Role, Scores: pred.Scores})
		}
	}
	slices.SortFunc(out, func(a, b RoleMismatch) int { return int(a.Room - b.Room) })
	return out, nil
}
