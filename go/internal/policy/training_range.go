package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The training range is a fixed template of the mod's own buildings
// (integrations/rimgovernor-native/Defs/ThingDefs/TrainingRange.xml), scored
// by the native TrainingRange RoomRoleDef. The template is static: lane
// count and length are constants, parametrization comes later.

// Range template geometry: RangeLanes lanes, each one cell wide and
// RangeLaneLength cells long, with a partition column between neighbours.
// The stand sits at a lane's first row and the dummy at its last, so the
// shot is RangeLaneLength-1 cells, inside a short bow's range.
const (
	RangeLanes      = 3
	RangeLaneLength = 12
	RangeWidth      = 2*RangeLanes - 1
)

// RangePieceKind is one building of the range template.
type RangePieceKind string

const (
	RangeStand     RangePieceKind = "stand"
	RangeDummy     RangePieceKind = "dummy"
	RangePartition RangePieceKind = "partition"
)

// RangeDefNames maps each piece to the mod's building def.
var RangeDefNames = map[RangePieceKind]string{
	RangeStand:     "RimGovernor_TrainingBowStand",
	RangeDummy:     "RimGovernor_TrainingDummy",
	RangePartition: "RimGovernor_TrainingPartition",
}

// RangeWeaponDef is the lane bow the training job issues; no recipe makes it.
const RangeWeaponDef = "Bow_Training"

// RangePiece is one building and the cell it stands on.
type RangePiece struct {
	Kind RangePieceKind
	Cell domain.Cell
	// Lane is the zero-based lane of a stand or dummy; partitions are -1.
	Lane int
}

// RangeLayout places the range template with its first stand's cell at
// origin: lanes run along +Z, lane i sits at origin.X+2i and the partitions
// fill the odd columns between. Pieces come in lane order (stand, dummy),
// then partitions column by column. The room it needs is RangeWidth by
// RangeLaneLength interior cells.
func RangeLayout(origin domain.Cell) []RangePiece {
	pieces := make([]RangePiece, 0, 2*RangeLanes+(RangeLanes-1)*RangeLaneLength)
	for lane := 0; lane < RangeLanes; lane++ {
		x := origin.X + int32(2*lane)
		pieces = append(pieces,
			RangePiece{Kind: RangeStand, Cell: domain.Cell{X: x, Z: origin.Z}, Lane: lane},
			RangePiece{Kind: RangeDummy, Cell: domain.Cell{X: x, Z: origin.Z + RangeLaneLength - 1}, Lane: lane})
	}
	for gap := 0; gap < RangeLanes-1; gap++ {
		for z := int32(0); z < RangeLaneLength; z++ {
			pieces = append(pieces, RangePiece{Kind: RangePartition, Cell: domain.Cell{X: origin.X + int32(2*gap+1), Z: origin.Z + z}, Lane: -1})
		}
	}
	return pieces
}
