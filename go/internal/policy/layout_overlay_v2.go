package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Overlay v2 (#784, B2; shapes #817) draws the layout plan over the whole
// map: zones as filled row runs, the spine, each room's outline with its
// role label, the labelled reservations (the perimeter and killbox as
// outlines) and a traffic layer for the busiest spine cells (recomputed by
// CheckRoutes at draw time, never persisted).

var zoneOverlay = map[ZoneKind]overlayStyle{
	ZoneField:  {planGreen, "field"},
	ZoneMining: {planBrown, "mining"},
	ZoneWood:   {planLightBlue, "wood lot"},
	ZoneNoGo:   {planDarkPurple, "no-go"},
}

var roomOverlay = map[ModuleRole]overlayStyle{
	ModuleBedroom:          {planBlue, "bedroom"},
	ModuleSuite:            {planBlue, "suite"},
	ModuleBarracks:         {planLightBlue, "barracks"},
	ModuleShelter:          {planLightBlue, "shelter"},
	ModuleDining:           {planAmber, "dining"},
	ModuleRec:              {planPink, "rec room"},
	ModuleLab:              {planViolet, "research"},
	ModuleBattery:          {planCyan, "battery room"},
	ModuleHospital:         {planWhite, "hospital"},
	ModulePrison:           {planDarkPurple, "jail"},
	ModuleKitchen:          {planYellow, "kitchen"},
	ModuleFreezer:          {planCyan, "freezer"},
	ModuleMealCloset:       {planCyan, "meal closet"},
	ModuleButchery:         {planBrown, "butchery"},
	ModuleStorage:          {planTan, "storage"},
	ModuleArmory:           {planTan, "armory"},
	ModuleWardrobe:         {planTan, "wardrobe"},
	ModuleWorkshop:         {planBrown, "workshop"},
	ModuleReserve:          {planGray, "reserve"},
	ModuleTomb:             {planGray, "tomb"},
	ModuleMorgue:           {planCyan, "morgue"},
	ModuleThrone:           {planAmber, "throne room"},
	ModuleNursery:          {planGreen, "nursery"},
	ModulePlayroom:         {planYellow, "playroom"},
	ModuleClassroom:        {planWhite, "classroom"},
	ModuleDeathrestChamber: {planDarkPurple, "deathrest chamber"},
	ModuleWorship:          {planAmber, "worship room"},
	ModuleContainmentCell:  {planDarkPurple, "containment cell"},
	ModuleIsolationRoom:    {planDarkPurple, "isolation room"},
}

var reservationOverlay = map[ReservationKind]overlayStyle{
	ReserveBatteryRoom:  {planCyan, "battery room"},
	ReserveTurbine:      {planCyan, "turbine"},
	ReserveTurbineLane:  {planGreen, "turbine lane"},
	ReserveSolar:        {planCyan, "solar"},
	ReserveGeothermal:   {planCyan, "geothermal"},
	ReservePen:          {planGreen, "animal pen"},
	ReserveBarn:         {planBrown, "barn"},
	ReserveIncinerator:  {planRed, "incinerator"},
	ReserveVetRoom:      {planWhite, "vet room"},
	ReservePerimeter:    {planRed, "perimeter"},
	ReserveGate:         {planYellow, "gate"},
	ReserveOuterWall:    {planRed, "outer wall"},
	ReserveOuterGate:    {planYellow, "outer gate"},
	ReserveBridge:       {planBrown, "bridge"},
	ReservePerimeterGap: {planYellow, "open gap"},
	ReserveMoisturePump: {planCyan, "moisture pump"},
	ReserveKillbox:      {planRed, "killbox"},
	ReserveMortar:       {planRed, "mortar"},
	ReserveCoverClear:   {planGray, "clear cover"},
	ReservePocketWall:   {planRed, "pocket wall"},
}

// Overlay draws p inside bounds. Layer order is zones, reservations,
// spine, traffic, rooms, so the smaller shapes draw last.
func (p LayoutPlan) Overlay(bounds Bounds) LayoutOverlay {
	var out LayoutOverlay
	label := func(text string, at domain.Cell) {
		if at.X >= 0 && at.Z >= 0 && at.X < bounds.Width && at.Z < bounds.Height {
			out.Labels = append(out.Labels, OverlayLabel{Text: text, Cell: at})
		}
	}
	add := func(style overlayStyle, shape OverlayStyle, rects []Rectangle, runs []RowRun) bool {
		layer := OverlayLayer{Style: shape, Label: style.label, Color: style.hue.fill()}
		if shape == OverlayOutline {
			layer.Color = style.hue.outline()
		}
		for _, r := range rects {
			if c, ok := clip(r, bounds); ok {
				layer.Rects = append(layer.Rects, c)
			}
		}
		for _, r := range runs {
			if c, ok := clipRun(r, bounds); ok {
				layer.Runs = append(layer.Runs, c)
			}
		}
		if len(layer.Rects)+len(layer.Runs) == 0 {
			return false
		}
		out.Layers = append(out.Layers, layer)
		return true
	}
	centre := func(r Rectangle) domain.Cell { return domain.Cell{X: r.X + r.Width/2, Z: r.Z + r.Height/2} }
	for _, z := range p.Zones {
		if style, ok := zoneOverlay[z.Kind]; ok {
			add(style, OverlayFill, nil, z.Runs)
		}
	}
	// The cover band is many rectangles: one label, on the largest.
	var cover Rectangle
	for _, r := range p.Reservations {
		style, ok := reservationOverlay[r.Kind]
		if !ok {
			continue
		}
		if r.Kind == ReserveCoverClear && r.Area.Width*r.Area.Height > cover.Width*cover.Height {
			cover = r.Area
		}
		shape := OverlayFill
		if r.Kind == ReservePerimeter || r.Kind == ReserveOuterWall || r.Kind == ReserveKillbox {
			shape = OverlayOutline
		}
		if add(style, shape, []Rectangle{r.Area}, nil) && r.Kind != ReservePerimeter && r.Kind != ReserveOuterWall && r.Kind != ReserveCoverClear {
			label(style.label, centre(r.Area))
		}
	}
	if cover.Width > 0 {
		label(reservationOverlay[ReserveCoverClear].label, centre(cover))
	}
	spine := spineRects(p.Hallways())
	add(overlayStyle{planGray, "hallway"}, OverlayFill, spine, nil)
	add(overlayStyle{planYellow, "traffic"}, OverlayFill, nil, cellRuns(busiestSpineCells(p, spine)))
	for _, r := range p.AllRooms() {
		style, ok := roomOverlay[r.Role]
		if !ok {
			continue
		}
		walls := Rectangle{X: r.Interior.X - 1, Z: r.Interior.Z - 1, Width: r.Interior.Width + 2, Height: r.Interior.Height + 2}
		if add(style, OverlayOutline, []Rectangle{walls}, nil) {
			label(style.label, centre(r.Interior))
		}
	}
	var doors []domain.Cell
	for _, r := range p.AllRooms() {
		doors = append(doors, r.Door)
		for _, d := range r.Doors {
			doors = append(doors, d.Cell)
		}
	}
	add(overlayStyle{planWhite, "door"}, OverlayFill, nil, cellRuns(doors))
	return out
}

// spineRects are the hallways' SpineWidth-wide rectangles.
func spineRects(segments []SpineSegment) []Rectangle {
	out := make([]Rectangle, 0, len(segments))
	for _, s := range segments {
		loX, hiX := min(s.From.X, s.To.X), max(s.From.X, s.To.X)
		loZ, hiZ := min(s.From.Z, s.To.Z), max(s.From.Z, s.To.Z)
		if loZ == hiZ {
			out = append(out, Rectangle{X: loX, Z: loZ - SpineWidth/2, Width: hiX - loX + 1, Height: SpineWidth})
		} else {
			out = append(out, Rectangle{X: loX - SpineWidth/2, Z: loZ, Width: SpineWidth, Height: hiZ - loZ + 1})
		}
	}
	return out
}

// busiestSpineCells are the spine cells carrying at least half the busiest
// spine cell's routes; none when the routes fail.
func busiestSpineCells(p LayoutPlan, spine []Rectangle) []domain.Cell {
	counts, err := CheckRoutes(p)
	if err != nil {
		return nil
	}
	onSpine := func(c domain.Cell) bool {
		for _, r := range spine {
			if c.X >= r.X && c.X < r.X+r.Width && c.Z >= r.Z && c.Z < r.Z+r.Height {
				return true
			}
		}
		return false
	}
	top := 0
	for c, n := range counts {
		if onSpine(c) {
			top = max(top, n)
		}
	}
	var out []domain.Cell
	for c, n := range counts {
		if top > 0 && 2*n >= top && onSpine(c) {
			out = append(out, c)
		}
	}
	return out
}

// SplitFields moves o's field zone layers into their own overlay, so the
// native can hide the busy field-block lattice on its own.
func SplitFields(o LayoutOverlay) (rest, fields LayoutOverlay) {
	rest.Labels = o.Labels
	for _, l := range o.Layers {
		if l.Label == zoneOverlay[ZoneField].label {
			fields.Layers = append(fields.Layers, l)
		} else {
			rest.Layers = append(rest.Layers, l)
		}
	}
	return rest, fields
}
