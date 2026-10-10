package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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

// RangeJobPrefix is the name prefix of the native training job defs
// (RimGovernor_TrainShooting, RimGovernor_TrainMelee).
const RangeJobPrefix = "RimGovernor_Train"

// RangeHomeHold is the range cells kept out of the home area (#2611). Vanilla
// repair works only on buildings inside home, and a repairer walking a lane
// stands in the line of fire, so the range's buildings stay outside home while
// anyone drills. The hold lifts only when a range building is damaged and no
// colonist is on a training job; a pawn whose job is unread counts as
// training. The cells leave home again as soon as the repair window closes
// (everything whole, or a drill began). Structures unknown, or no range
// standing, hold nothing.
func RangeHomeHold(structures domain.Fact[[]UpkeepStructure], pawns domain.Fact[[]WorkPawn]) []domain.Cell {
	rows, known := structures.Value()
	if !known {
		return nil
	}
	isRange := map[string]bool{}
	for _, def := range RangeDefNames {
		isRange[def] = true
	}
	var cells []domain.Cell
	damaged := false
	for _, row := range rows {
		if !isRange[row.Definition] {
			continue
		}
		cells = append(cells, row.Cell)
		damaged = damaged || row.MaxHitPoints > 0 && row.HitPoints < row.MaxHitPoints
	}
	if len(cells) == 0 || damaged && !rangeBusy(pawns) {
		return nil
	}
	return cells
}

// rangeBusy reports whether any colonist may be drilling: a training job, or
// a job the read did not carry.
func rangeBusy(pawns domain.Fact[[]WorkPawn]) bool {
	list, known := pawns.Value()
	if !known {
		return true
	}
	for _, p := range list {
		job, known := p.Job.Value()
		if !known || strings.HasPrefix(job.Def, RangeJobPrefix) {
			return true
		}
	}
	return false
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
