package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LayoutOverlay is the master plan drawn as native plan designations
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

// moduleOverlay is a planned module's color and label.
var moduleOverlay = map[ModuleRole][2]string{
	ModulePlaza:    {planAmber, "plaza"},
	ModuleHousing:  {planBlue, "housing"},
	ModuleHospital: {planWhite, "hospital"},
	ModulePrison:   {planDarkPurple, "jail"},
	ModuleKitchen:  {planYellow, "kitchen"},
	ModuleFreezer:  {planCyan, "freezer"},
	ModuleStorage:  {planTan, "storage"},
	ModuleWorkshop: {planBrown, "workshop"},
	ModuleFields:   {planGreen, "fields"},
	ModuleKillbox:  {planRed, "killbox"},
	ModuleWall:     {planRed, "wall"},
	ModuleReserve:  {planGray, "reserve"},
}

// Overlay draws the plan inside bounds: aisles over the plan's extent,
// then each module's outline and label. Unusable modules draw nothing.
func (p MasterPlan) Overlay(bounds Bounds) LayoutOverlay {
	out := LayoutOverlay{Rooms: append([]OverlayRoomColor(nil), overlayRooms...)}
	if !p.Grid.Valid() {
		return out
	}
	extent := p.Grid.rectangle(-p.Radius*p.Grid.Pitch, -p.Radius*p.Grid.Pitch, (2*p.Radius+1)*p.Grid.Pitch, (2*p.Radius+1)*p.Grid.Pitch)
	if aisles := rowRuns(p.Grid.AislesWithin(bounds, extent)); len(aisles) > 0 {
		out.Layers = append(out.Layers, OverlayLayer{Color: planGray, Label: "aisles", Rects: aisles})
	}
	for _, m := range p.Modules {
		style, ok := moduleOverlay[m.Role]
		if !ok {
			continue
		}
		r := p.Rect(m)
		var rects []Rectangle
		for _, edge := range []Rectangle{
			{X: r.X, Z: r.Z, Width: r.Width, Height: 1},
			{X: r.X, Z: r.Z + r.Height - 1, Width: r.Width, Height: 1},
			{X: r.X, Z: r.Z + 1, Width: 1, Height: r.Height - 2},
			{X: r.X + r.Width - 1, Z: r.Z + 1, Width: 1, Height: r.Height - 2},
		} {
			if e, ok := clip(edge, bounds); ok {
				rects = append(rects, e)
			}
		}
		if len(rects) == 0 {
			continue
		}
		out.Layers = append(out.Layers, OverlayLayer{Color: style[0], Label: style[1], Rects: rects})
		centre := domain.Cell{X: r.X + r.Width/2, Z: r.Z + r.Height/2}
		if centre.X >= 0 && centre.Z >= 0 && centre.X < bounds.Width && centre.Z < bounds.Height {
			out.Labels = append(out.Labels, OverlayLabel{Text: style[1], Cell: centre})
		}
	}
	return out
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
