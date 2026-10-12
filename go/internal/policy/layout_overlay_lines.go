package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// OverlayLine is a straight segment between two map cells, drawn centre to
// centre; Arrow puts a head on To.
type OverlayLine struct {
	From, To domain.Cell
	Arrow    bool
}

// roomCentres is each room's label cell by room id.
func (p LayoutPlan) roomCentres() map[int32]domain.Cell {
	centres := map[int32]domain.Cell{}
	for _, r := range p.AllRooms() {
		centres[r.ID] = labelCell(r.Region)
	}
	return centres
}

// lineLayer is one layer of the lines with both ends in bounds; false when
// none remain.
func lineLayer(style overlayStyle, bounds Bounds, lines []OverlayLine) (OverlayLayer, bool) {
	inside := func(c domain.Cell) bool { return c.X >= 0 && c.Z >= 0 && c.X < bounds.Width && c.Z < bounds.Height }
	var kept []OverlayLine
	for _, l := range lines {
		if inside(l.From) && inside(l.To) {
			kept = append(kept, l)
		}
	}
	return OverlayLayer{Style: OverlayOutline, Label: style.label, Color: style.hue.outline(), Lines: kept}, len(kept) > 0
}

// linesOverlay adds the plan's line layers to out: the door graph (each link
// as two segments through its door cell), a successor arrow from every room
// to the room that replaces it, and the room trips as affinity edges. A line
// at an absent room or outside bounds is dropped.
func (p LayoutPlan) linesOverlay(bounds Bounds, out *LayoutOverlay) {
	centres := p.roomCentres()
	var links, successors, trips []OverlayLine
	for _, l := range p.Links {
		a, okA := centres[l.A]
		b, okB := centres[l.B]
		if okA && okB {
			links = append(links, OverlayLine{From: a, To: l.Cell}, OverlayLine{From: l.Cell, To: b})
		}
	}
	for _, r := range p.AllRooms() {
		if old, ok := centres[r.Replaces]; ok && r.Replaces > 0 {
			successors = append(successors, OverlayLine{From: old, To: centres[r.ID], Arrow: true})
		}
	}
	for _, t := range p.Trips {
		a, okA := centres[t.A]
		b, okB := centres[t.B]
		if okA && okB {
			trips = append(trips, OverlayLine{From: a, To: b})
		}
	}
	for _, set := range []struct {
		style overlayStyle
		lines []OverlayLine
	}{{overlayStyle{planWhite, "link"}, links}, {overlayStyle{planAmber, "successor arrow"}, successors}, {overlayStyle{planCyan, "trip"}, trips}} {
		if layer, ok := lineLayer(set.style, bounds, set.lines); ok {
			out.Layers = append(out.Layers, layer)
		}
	}
}

// RequirementOverlay adds the station advice's required edges (a link the
// environment needs, cold storage beside the stove) to o as red lines between
// the first matched built room of each group.
func (p LayoutPlan) RequirementOverlay(advice StationAdvice, bounds Bounds, o *LayoutOverlay) {
	centres := p.roomCentres()
	groupOf := map[string]int{} // station node -> index into advice.Groups
	for i, g := range advice.Groups {
		for _, s := range g.Group.Stations {
			groupOf[s.Node] = i
		}
	}
	room := func(node string) (int, domain.Cell, bool) {
		i, ok := groupOf[node]
		if !ok || len(advice.Groups[i].Rooms) == 0 {
			return 0, domain.Cell{}, false
		}
		c, ok := centres[advice.Groups[i].Rooms[0]]
		return i, c, ok
	}
	var lines []OverlayLine
	for _, e := range advice.Graph.Edges {
		if !e.Required {
			continue
		}
		ia, a, okA := room(e.A)
		ib, b, okB := room(e.B)
		if okA && okB && ia != ib {
			lines = append(lines, OverlayLine{From: a, To: b})
		}
	}
	if layer, ok := lineLayer(overlayStyle{planRed, "requirement"}, bounds, lines); ok {
		o.Layers = append(o.Layers, layer)
	}
}
