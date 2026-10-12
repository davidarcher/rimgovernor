package policy

// PlannedSuite is a suite's plan role.
const PlannedSuite PlannedRole = "suite"

// RoomRoleSuite is the room role a suite is built for. It is a plan role
// only: RimWorld has no Suite RoomRoleDef, so the native census reports a
// furnished suite as a Bedroom.
const RoomRoleSuite RoomRole = "Suite"

// Snapped suite bounds: width along the corridor, depth away from it.
const (
	suiteMinWidth, suiteMaxWidth int32 = 3, 7
	suiteMinDepth, suiteMaxDepth int32 = 4, 8
)

// suiteFurnitureTiles is the bedroom template's footprint (bed 2, end
// table 1, dresser 2, lamp 1), each tile costing the Space stat 0.9.
const suiteFurnitureTiles = 6

// SuiteSize is the suite interior (width along the corridor, depth away
// from it) whose space meets an impressiveness target on its own: the
// Space stat normalised by 125 (room_quality_upgrade.go) reaching
// target/100, at 1.4 per open tile less 0.9 per furniture tile. The
// smallest area within 3-7 x 4-8 wins, then the squarer, then the
// narrower; a target no suite reaches gets the largest.
func SuiteSize(target float64) (int32, int32) {
	cells := suiteCells(target)
	bw, bd := suiteMaxWidth, suiteMaxDepth
	for w := suiteMinWidth; w <= suiteMaxWidth; w++ {
		for d := suiteMinDepth; d <= suiteMaxDepth; d++ {
			a, b := w*d, bw*bd
			if a < cells || a > b {
				continue
			}
			if a < b || abs32(w-d) < abs32(bw-bd) {
				bw, bd = w, d
			}
		}
	}
	return bw, bd
}
