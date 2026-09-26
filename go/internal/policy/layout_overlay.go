package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LayoutOverlay is the layout plan drawn as native plan designations
// (#726): output only, rewritten whole on every change. Existing rooms
// are painted solid natively by their RoomRoleDef (Rooms); each planned
// module is outlined in its role's color with its role name as a text
// label, and aisles are grey row runs.
type LayoutOverlay struct {
	Layers []OverlayLayer
	Labels []OverlayLabel
	Rooms  []OverlayRoomColor
}

// OverlayLayer is one native plan: a planning ColorDef, the name after
// the owned prefix, and its cells.
type OverlayLayer struct {
	Color, Label string
	Rects        []Rectangle
}

// OverlayLabel is text drawn over the map at a cell.
type OverlayLabel struct {
	Text string
	Cell domain.Cell
}

// OverlayRoomColor paints an existing room whose RoomRoleDef is Role
// ("Freezer" is a storeroom below 0 C). Label is its text.
type OverlayRoomColor struct {
	Role, Color, Label string
}

// Planning ColorDefs: the nine vanilla ones and the mod's eight
// (Defs/ColorDefs/RimGovernorPlanColors.xml).
const (
	planGray       = "PlanGray"
	planRed        = "PlanRed"
	planYellow     = "PlanYellow"
	planGreen      = "PlanGreen"
	planCyan       = "PlanCyan"
	planBlue       = "PlanBlue"
	planPink       = "PlanPink"
	planLightBlue  = "RimGovernor_PlanLightBlue"
	planAmber      = "RimGovernor_PlanAmber"
	planWhite      = "RimGovernor_PlanWhite"
	planDarkPurple = "RimGovernor_PlanDarkPurple"
	planBrown      = "RimGovernor_PlanBrown"
	planTan        = "RimGovernor_PlanTan"
	planViolet     = "RimGovernor_PlanViolet"
)

// overlayRooms is the existing-room palette by RoomRoleDef.
var overlayRooms = []OverlayRoomColor{
	{"Bedroom", planBlue, "bedroom"},
	{"Barracks", planLightBlue, "barracks"},
	{"Kitchen", planYellow, "kitchen"},
	{"Freezer", planCyan, "freezer"},
	{"DiningRoom", planAmber, "dining"},
	{"RecRoom", planPink, "rec room"},
	{"Hospital", planWhite, "hospital"},
	{"PrisonCell", planDarkPurple, "jail"},
	{"PrisonBarracks", planDarkPurple, "jail"},
	{"Workshop", planBrown, "workshop"},
	{"Storeroom", planTan, "storage"},
	{"Laboratory", planViolet, "research"},
	{"Barn", planGreen, "barn"},
}

// clip is r inside bounds, false when nothing is left.
func clip(r Rectangle, b Bounds) (Rectangle, bool) {
	minX, minZ := max(r.X, 0), max(r.Z, 0)
	maxX, maxZ := min(r.X+r.Width, b.Width), min(r.Z+r.Height, b.Height)
	if minX >= maxX || minZ >= maxZ {
		return Rectangle{}, false
	}
	return Rectangle{X: minX, Z: minZ, Width: maxX - minX, Height: maxZ - minZ}, true
}

// rowRuns packs cells into single-row runs of consecutive x, z-major.
func rowRuns(cells []domain.Cell) []Rectangle {
	rows := map[int32]map[int32]bool{}
	minZ, maxZ := int32(1<<30), int32(-1<<30)
	minX, maxX := int32(1<<30), int32(-1<<30)
	for _, c := range cells {
		if rows[c.Z] == nil {
			rows[c.Z] = map[int32]bool{}
		}
		rows[c.Z][c.X] = true
		minZ, maxZ, minX, maxX = min(minZ, c.Z), max(maxZ, c.Z), min(minX, c.X), max(maxX, c.X)
	}
	var out []Rectangle
	for z := minZ; z <= maxZ; z++ {
		row := rows[z]
		for x := minX; x <= maxX; x++ {
			if !row[x] {
				continue
			}
			start := x
			for row[x+1] {
				x++
			}
			out = append(out, Rectangle{X: start, Z: z, Width: x - start + 1, Height: 1})
		}
	}
	return out
}
