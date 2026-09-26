package policy

import (
	"fmt"
	"reflect"
	"sort"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Regularity checks for interior templates (#798). Template tests call
// assertInteriorRegular on a plan and assertInteriorRepeatable on a room
// size; both hold the #798 rules as requirements.

// interiorRegularityViolations lists every #798 rule a plan's canonical
// pieces break: rows share one line and one rotation with even gaps and are
// anchored to a corner or centred; pairs are mirror images; centred pieces
// sit on the centre line; a plan with its room keeps the doors' aisle
// (#801).
func interiorRegularityViolations(plan InteriorPlan) []string {
	f := plan.Frame
	var out []string
	if err := ValidateInteriorPieces(f, plan.Canonical); err != nil {
		out = append(out, err.Error())
	}
	if plan.Room.Interior.Width > 0 {
		if err := InteriorPlanWalkable(plan); err != nil {
			out = append(out, err.Error())
		}
	}
	rows, pairs := map[string][]InteriorPiece{}, map[string][]InteriorPiece{}
	for _, p := range plan.Canonical {
		if p.Row != "" {
			rows[p.Row] = append(rows[p.Row], p)
		}
		if p.Pair != "" {
			pairs[p.Pair] = append(pairs[p.Pair], p)
		}
		if p.Centred && 2*p.Rect.X+p.Rect.Width != f.Width && 2*p.Rect.X+p.Rect.Width != f.Width-1 {
			out = append(out, fmt.Sprintf("%s: not on the centre line", p.Slot))
		}
	}
	for name, row := range rows {
		out = append(out, rowViolations(f, name, row)...)
	}
	for name, pair := range pairs {
		if len(pair) != 2 {
			out = append(out, fmt.Sprintf("pair %s: %d pieces", name, len(pair)))
			continue
		}
		m := f.MirrorPiece(pair[0], pair[1].Slot)
		if m.Rect != pair[1].Rect || m.Rot != pair[1].Rot || pair[0].Def != pair[1].Def {
			out = append(out, fmt.Sprintf("pair %s: %s and %s are not mirrored", name, pair[0].Slot, pair[1].Slot))
		}
	}
	sort.Strings(out)
	return out
}

func rowViolations(f InteriorFrame, name string, row []InteriorPiece) []string {
	var out []string
	first := row[0]
	horizontal, vertical := true, true
	for _, p := range row {
		if p.Rot != first.Rot {
			out = append(out, fmt.Sprintf("row %s: %s faces %s, not %s", name, p.Slot, p.Rot, first.Rot))
		}
		horizontal = horizontal && p.Rect.Z == first.Rect.Z && p.Rect.Height == first.Rect.Height
		vertical = vertical && p.Rect.X == first.Rect.X && p.Rect.Width == first.Rect.Width
	}
	if len(row) > 1 && !horizontal && !vertical {
		return append(out, fmt.Sprintf("row %s: pieces off one line", name))
	}
	start := func(r Rectangle) int32 { return r.X }
	span := func(r Rectangle) int32 { return r.Width }
	length := f.Width
	if !horizontal {
		start, span, length = func(r Rectangle) int32 { return r.Z }, func(r Rectangle) int32 { return r.Height }, f.Depth
	}
	sort.Slice(row, func(i, j int) bool { return start(row[i].Rect) < start(row[j].Rect) })
	for i := 2; i < len(row); i++ {
		g1 := start(row[i-1].Rect) - start(row[i-2].Rect) - span(row[i-2].Rect)
		g2 := start(row[i].Rect) - start(row[i-1].Rect) - span(row[i-1].Rect)
		if g1 != g2 {
			out = append(out, fmt.Sprintf("row %s: uneven gaps %d and %d", name, g1, g2))
		}
	}
	last := row[len(row)-1].Rect
	lead, trail := start(row[0].Rect), length-start(last)-span(last)
	if lead != 0 && trail != 0 && lead != trail && lead != trail-1 {
		out = append(out, fmt.Sprintf("row %s: neither anchored to a corner nor centred (margins %d, %d)", name, lead, trail))
	}
	return out
}

func assertInteriorRegular(t *testing.T, plan InteriorPlan) {
	t.Helper()
	for _, v := range interiorRegularityViolations(plan) {
		t.Errorf("%s %dx%d: %s", plan.Template, plan.Frame.Width, plan.Frame.Depth, v)
	}
}

// interiorRoomsAround returns the same width x depth room with its one door
// at entrance on each of the four walls and at the mirrored position.
func interiorRoomsAround(role RoomRole, width, depth, entrance int32) []InteriorRoom {
	at := func(r Rectangle, door domain.Cell) InteriorRoom {
		return InteriorRoom{Role: role, Interior: r, Doors: []domain.Cell{door}}
	}
	ns := Rectangle{X: 10, Z: 20, Width: width, Height: depth}
	ew := Rectangle{X: 10, Z: 20, Width: depth, Height: width}
	var rooms []InteriorRoom
	for _, u := range []int32{entrance, width - 1 - entrance} {
		rooms = append(rooms,
			at(ns, domain.Cell{X: ns.X + u, Z: ns.Z - 1}),
			at(ns, domain.Cell{X: ns.X + width - 1 - u, Z: ns.Z + depth}),
			at(ew, domain.Cell{X: ew.X - 1, Z: ew.Z + width - 1 - u}),
			at(ew, domain.Cell{X: ew.X + depth, Z: ew.Z + u}),
		)
	}
	return rooms
}

// assertInteriorRepeatable plans a room of the role and size with its door
// on every wall (and mirrored) and requires one canonical layout, regular.
func assertInteriorRepeatable(t *testing.T, role RoomRole, width, depth, entrance int32) InteriorPlan {
	t.Helper()
	rooms := interiorRoomsAround(role, width, depth, entrance)
	first, ok := PlanInterior(rooms[0])
	if !ok {
		t.Fatalf("%s %dx%d door %d: no plan", role, width, depth, entrance)
	}
	assertInteriorRegular(t, first)
	for _, room := range rooms[1:] {
		plan, ok := PlanInterior(room)
		if !ok {
			t.Fatalf("%+v: no plan", room)
		}
		if !reflect.DeepEqual(plan.Canonical, first.Canonical) || !reflect.DeepEqual(plan.Frame, first.Frame) {
			t.Errorf("%+v: layout differs from the door-south room:\n%+v\n%+v", room, plan.Canonical, first.Canonical)
		}
	}
	return first
}
