package policy

import (
	"fmt"
	"maps"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The player's Replan layout action (#957). Unlike ReplanLayout, which only
// grows the saved plan, ReplanFresh lays the plan out again around what the
// colony has already built; the result is a proposal the player applies or
// discards from the in-game panel.

// ReplanFresh re-derives plan over a fresh survey. A room with anything
// built on its walls or floor (built marks those cells) is kept where it
// is, with the hallways its door opens onto and the main hallway every
// crossing joins; so is a generator site something stands on. Every other
// room, hallway and reservation is dropped, and the rooms pawns colonists
// and tombs tomb rooms need re-grow around the kept ones, off every other
// built cell; utilities and the perimeter are then laid again, as
// DeriveLayoutPlan lays them, the herd sites for animals animals included. Nothing built moves or loses its room.
// Unknown when the survey holds no room for a core.
func ReplanFresh(plan LayoutPlan, s MapSurvey, built map[domain.Cell]bool, pawns, tombs int, tier BuildTier, geysers []PowerGeyser, animals int) domain.Fact[LayoutPlan] {
	var rooms []LayoutRoom
	for _, r := range plan.Rooms {
		if rectHits(roomWalls(r), built) {
			rooms = append(rooms, r)
		}
	}
	wings := keepWingRooms(plan.Wings, func(r LayoutRoom) bool { return rectHits(roomWalls(r), built) })
	var spine []SpineSegment
	main := trunk(plan.Spine)
	for i, sg := range plan.Spine {
		if i == main && len(wings) > 0 {
			spine = append(spine, sg)
			continue
		}
		for _, r := range rooms {
			if i == main || onSegment(r, sg) {
				spine = append(spine, sg)
				break
			}
		}
	}
	// Generator sites something stands on; a turbine pair stays whole.
	pairs := map[int32]bool{}
	for _, r := range plan.Reservations {
		if (r.Kind == ReserveTurbine || r.Kind == ReserveTurbineLane) && rectHits(r.Area, built) {
			pairs[r.Pair] = true
		}
	}
	want := layoutUtilities
	want.PenAnimals, want.ThickRoof = animals, ThickRoofCells(s)
	want.TurbinePairs = max(0, want.TurbinePairs-len(pairs))
	var kept []LayoutReservation
	for _, r := range plan.Reservations {
		switch {
		case r.Kind == ReserveTurbine || r.Kind == ReserveTurbineLane:
			if !pairs[r.Pair] {
				continue
			}
		case r.Kind == ReserveSolar && rectHits(r.Area, built):
			want.Solar = max(0, want.Solar-1)
		case r.Kind == ReserveGeothermal && rectHits(r.Area, built):
		default:
			continue
		}
		kept = append(kept, r)
	}
	for _, g := range geysers {
		if len(g.Cells) == 0 || geyserKept(g, kept) {
			continue
		}
		var r Rectangle
		for _, c := range g.Cells {
			r = unionRect(r, Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1})
		}
		want.Geysers = append(want.Geysers, r)
	}
	// New rooms stay off built cells, except the kept rooms' walls (a
	// neighbour shares them) and the kept hallways.
	open := map[domain.Cell]bool{}
	for _, r := range (LayoutPlan{Rooms: rooms, Wings: wings}).AllRooms() {
		for _, c := range RectangleCells(roomWalls(r)) {
			open[c] = true
		}
	}
	for _, r := range spineRects((LayoutPlan{Spine: spine, Wings: wings}).Hallways()) {
		for _, c := range RectangleCells(r) {
			open[c] = true
		}
	}
	blocked := map[domain.Cell]bool{}
	for c := range built {
		if !open[c] {
			blocked[c] = true
		}
	}
	// Nor do they cover a geyser's enclosure.
	roomless := maps.Clone(blocked)
	for c := range geothermalCells(geyserFootprints(geysers)) {
		if !open[c] {
			roomless[c] = true
		}
	}
	zones := Zone(s)
	// With nothing kept this is a fresh plan, sited as DeriveLayoutPlan sites it (#1285).
	seed := LayoutPlan{Spine: spine, Rooms: rooms, Wings: wings, Zones: coreWithout(zones, roomless), Reservations: kept}
	var next LayoutPlan
	if len(spine) > 0 {
		// Around what is kept: the same generator as a replan, every kept room pinned.
		next = newReplanner(seed, s, nil, pawns, tombs, tier, nil).site(seed, allPins(seed), 0)
	} else {
		next = SiteCore(seed, s, pawns, tombs, tier)
	}
	next.Zones = coreWithout(zones, blocked)
	if len(next.AllRooms()) == 0 {
		return domain.Unknown[LayoutPlan]()
	}
	return domain.Known(withoutCore(PlanPerimeter(PlanUtilities(next, want), s)))
}

// geyserKept reports a kept geothermal site over the geyser.
func geyserKept(g PowerGeyser, kept []LayoutReservation) bool {
	for _, r := range kept {
		if r.Kind == ReserveGeothermal && contains(r.Area, g.Cells[0]) {
			return true
		}
	}
	return false
}

// coreWithout drops cells from the core candidates.
func coreWithout(zones []LayoutZone, cells map[domain.Cell]bool) []LayoutZone {
	if len(cells) == 0 {
		return zones
	}
	out := make([]LayoutZone, 0, len(zones))
	for _, z := range zones {
		if z.Kind != ZoneCore {
			out = append(out, z)
			continue
		}
		var runs []RowRun
		for _, r := range z.Runs {
			start := r.X
			for x := r.X; x <= r.X+r.Length; x++ {
				if x < r.X+r.Length && !cells[domain.Cell{X: x, Z: r.Z}] {
					continue
				}
				if x > start {
					runs = append(runs, RowRun{Z: r.Z, X: start, Length: x - start})
				}
				start = x + 1
			}
		}
		z.Runs = runs
		out = append(out, z)
	}
	return out
}

// LayoutProposalDiff counts the rooms a proposal adds and drops against
// the current plan (rooms match by role and interior).
func LayoutProposalDiff(current, proposal LayoutPlan) (added, removed int) {
	a, r := roomDiff(current, proposal)
	return len(a), len(r)
}

func roomDiff(current, proposal LayoutPlan) (added, removed []LayoutRoom) {
	key := func(r LayoutRoom) string { return fmt.Sprint(r.Role, r.Interior) }
	have, next := map[string]bool{}, map[string]bool{}
	for _, r := range current.AllRooms() {
		have[key(r)] = true
	}
	for _, r := range proposal.AllRooms() {
		next[key(r)] = true
		if !have[key(r)] {
			added = append(added, r)
		}
	}
	for _, r := range current.AllRooms() {
		if !next[key(r)] {
			removed = append(removed, r)
		}
	}
	return added, removed
}

// ProposalOverlay draws a layout proposal against the current plan: rooms
// and hallway cells it adds tinted green, those it drops red, each room
// labelled "+role" or "-role". What both share is left to the layout
// layer.
func ProposalOverlay(current, proposal LayoutPlan, bounds Bounds) LayoutOverlay {
	var out LayoutOverlay
	added, removed := roomDiff(current, proposal)
	hallway := func(p LayoutPlan) map[domain.Cell]bool {
		cells := map[domain.Cell]bool{}
		for _, r := range spineRects(p.Hallways()) {
			for _, c := range RectangleCells(r) {
				cells[c] = true
			}
		}
		return cells
	}
	was, now := hallway(current), hallway(proposal)
	var gained, lost []domain.Cell
	for c := range now {
		if !was[c] {
			gained = append(gained, c)
		}
	}
	for c := range was {
		if !now[c] {
			lost = append(lost, c)
		}
	}
	tint := func(hue overlayHue, sign string, rooms []LayoutRoom, cells []domain.Cell) {
		layer := OverlayLayer{Style: OverlayFill, Label: sign + "proposal", Color: OverlayColor{hue.R, hue.G, hue.B, 0.45}}
		for _, r := range rooms {
			if c, ok := clip(roomWalls(r), bounds); ok {
				layer.Rects = append(layer.Rects, c)
			}
			label := string(r.Role)
			if style, ok := roomOverlay[r.Role]; ok {
				label = style.label
			}
			at := domain.Cell{X: r.Interior.X + r.Interior.Width/2, Z: r.Interior.Z + r.Interior.Height/2}
			if at.X >= 0 && at.Z >= 0 && at.X < bounds.Width && at.Z < bounds.Height {
				out.Labels = append(out.Labels, OverlayLabel{Text: sign + label, Cell: at})
			}
		}
		if len(cells) > 0 {
			for _, r := range cellRuns(cells) {
				if c, ok := clipRun(r, bounds); ok {
					layer.Runs = append(layer.Runs, c)
				}
			}
		}
		if len(layer.Rects)+len(layer.Runs) > 0 {
			out.Layers = append(out.Layers, layer)
		}
	}
	tint(planRed, "-", removed, lost)
	tint(planGreen, "+", added, gained)
	return out
}
