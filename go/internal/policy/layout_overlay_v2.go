package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Overlay v2 (#784, B2) draws the layout plan over the whole map: zones as
// row runs, the spine, each room's wall ring with its role label, the
// labelled reservations and a traffic layer for the busiest spine cells
// (recomputed by CheckRoutes at draw time, never persisted).

var zoneOverlay = map[ZoneKind][2]string{
	ZoneField:   {planGreen, "field"},
	ZonePasture: {planTan, "pasture"},
	ZoneMining:  {planBrown, "mining"},
	ZoneWood:    {planLightBlue, "wood lot"},
	ZoneNoGo:    {planDarkPurple, "no-go"},
}

var roomOverlay = map[ModuleRole][2]string{
	ModuleBedroom:  {planBlue, "bedroom"},
	ModuleBarracks: {planLightBlue, "barracks"},
	ModuleDining:   {planAmber, "dining"},
	ModuleRec:      {planPink, "rec room"},
	ModuleLab:      {planViolet, "research"},
	ModuleBattery:  {planCyan, "battery room"},
}

var reservationOverlay = map[ReservationKind][2]string{
	ReserveBatteryRoom: {planCyan, "battery room"},
	ReserveTurbine:     {planCyan, "turbine"},
	ReserveTurbineLane: {planGreen, "turbine lane"},
	ReserveSolar:       {planCyan, "solar"},
	ReserveGeothermal:  {planCyan, "geothermal"},
	ReservePerimeter:   {planRed, "perimeter"},
	ReserveGate:        {planYellow, "gate"},
	ReserveKillbox:     {planRed, "killbox"},
	ReserveMortar:      {planRed, "mortar"},
	ReserveCoverClear:  {planGray, "clear cover"},
}

// Overlay draws p inside bounds. Layer order is zones, reservations,
// spine, traffic, rooms, so the smaller shapes draw last.
func (p LayoutPlan) Overlay(bounds Bounds) LayoutOverlay {
	out := LayoutOverlay{Rooms: append([]OverlayRoomColor(nil), overlayRooms...)}
	add := func(style [2]string, rects []Rectangle, label bool, at domain.Cell) {
		var kept []Rectangle
		for _, r := range rects {
			if c, ok := clip(r, bounds); ok {
				kept = append(kept, c)
			}
		}
		if len(kept) == 0 {
			return
		}
		out.Layers = append(out.Layers, OverlayLayer{Color: style[0], Label: style[1], Rects: kept})
		if label && at.X >= 0 && at.Z >= 0 && at.X < bounds.Width && at.Z < bounds.Height {
			out.Labels = append(out.Labels, OverlayLabel{Text: style[1], Cell: at})
		}
	}
	centre := func(r Rectangle) domain.Cell { return domain.Cell{X: r.X + r.Width/2, Z: r.Z + r.Height/2} }
	for _, z := range p.Zones {
		style, ok := zoneOverlay[z.Kind]
		if !ok {
			continue
		}
		rects := make([]Rectangle, 0, len(z.Runs))
		for _, run := range z.Runs {
			rects = append(rects, Rectangle{X: run.X, Z: run.Z, Width: run.Length, Height: 1})
		}
		add(style, rects, false, domain.Cell{})
	}
	for _, r := range p.Reservations {
		style, ok := reservationOverlay[r.Kind]
		if !ok {
			continue
		}
		add(style, []Rectangle{r.Area}, r.Kind != ReservePerimeter && r.Kind != ReserveCoverClear, centre(r.Area))
	}
	spine := spineRects(p.Spine)
	add([2]string{planGray, "hallway"}, spine, false, domain.Cell{})
	add([2]string{planYellow, "traffic"}, rowRuns(busiestSpineCells(p, spine)), false, domain.Cell{})
	for _, r := range p.Rooms {
		style, ok := roomOverlay[r.Role]
		if !ok {
			if style, ok = moduleOverlay[r.Role]; !ok {
				continue
			}
		}
		w := Rectangle{X: r.Interior.X - 1, Z: r.Interior.Z - 1, Width: r.Interior.Width + 2, Height: r.Interior.Height + 2}
		add(style, []Rectangle{
			{X: w.X, Z: w.Z, Width: w.Width, Height: 1},
			{X: w.X, Z: w.Z + w.Height - 1, Width: w.Width, Height: 1},
			{X: w.X, Z: w.Z + 1, Width: 1, Height: w.Height - 2},
			{X: w.X + w.Width - 1, Z: w.Z + 1, Width: 1, Height: w.Height - 2},
		}, true, centre(r.Interior))
	}
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
