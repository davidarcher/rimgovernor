package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The training range is a template of the mod's own buildings
// (integrations/rimgovernor-native/Defs/ThingDefs/TrainingRange.xml) in open
// air: one row of stands side by side with no gaps and, RangeDistance cells
// away, one row of dummies directly facing them. The room's Interior is the
// bounding rectangle of the two rows, its Facing the direction from the stands
// to the dummies (layout_range.go sites it and sizes it by stand count).

// RangeDistance is the cells from a stand to the dummy facing it. It keeps the
// shot inside the bow's short-accuracy band.
const RangeDistance = 8

// RangePieceKind is one building of the range template.
type RangePieceKind string

const (
	RangeStand RangePieceKind = "stand"
	RangeDummy RangePieceKind = "dummy"
)

// RangeDefNames maps each piece to the mod's building def.
var RangeDefNames = map[RangePieceKind]string{
	RangeStand: "RimGovernor_TrainingBowStand",
	RangeDummy: "RimGovernor_TrainingDummy",
}

// RangeWeaponDef is the lane bow the training job issues; no recipe makes it.
const RangeWeaponDef = "Bow_Training"

// RangePiece is one building and the cell it stands on.
type RangePiece struct {
	Kind RangePieceKind
	Cell domain.Cell
	// Column is the zero-based position along the row of a stand or dummy; a
	// stand and the dummy facing it share one.
	Column int
}

// RangeLayout places the range template inside room's Interior: stands side by
// side on the row at the stand end and a dummy directly across from each,
// RangeDistance cells away at the dummy end (the room's Facing). Pieces come
// column by column (stand, dummy). A room whose Facing is not a direction has
// none.
func RangeLayout(room PlannedRoom) []RangePiece {
	in := room.Interior
	n := int(RangeStandCount(room))
	pieces := make([]RangePiece, 0, 2*n)
	for i := 0; i < n; i++ {
		var stand, dummy domain.Cell
		switch room.Facing {
		case domain.North:
			stand = domain.Cell{X: in.X + int32(i), Z: in.Z}
			dummy = domain.Cell{X: stand.X, Z: in.Z + RangeDistance}
		case domain.South:
			stand = domain.Cell{X: in.X + int32(i), Z: in.Z + RangeDistance}
			dummy = domain.Cell{X: stand.X, Z: in.Z}
		case domain.East:
			stand = domain.Cell{X: in.X, Z: in.Z + int32(i)}
			dummy = domain.Cell{X: in.X + RangeDistance, Z: stand.Z}
		case domain.West:
			stand = domain.Cell{X: in.X + RangeDistance, Z: in.Z + int32(i)}
			dummy = domain.Cell{X: in.X, Z: stand.Z}
		default:
			return nil
		}
		pieces = append(pieces,
			RangePiece{Kind: RangeStand, Cell: stand, Column: i},
			RangePiece{Kind: RangeDummy, Cell: dummy, Column: i})
	}
	return pieces
}
