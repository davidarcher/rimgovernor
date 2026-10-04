package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// footprintBox is the bounding box of the plan's core footprint.
func footprintBox(plan LayoutPlan, w, h int32) Rectangle {
	var box Rectangle
	for i, v := range coreFootprint(plan, w, h) {
		if v {
			box = unionRect(box, Rectangle{X: int32(i) % w, Z: int32(i) / w, Width: 1, Height: 1})
		}
	}
	return box
}

func countCells(v []bool) int {
	n := 0
	for _, b := range v {
		if b {
			n++
		}
	}
	return n
}

func footprintRoom(role ModuleRole, x, z, w, h int32) LayoutRoom {
	return LayoutRoom{Role: role, Interior: Rectangle{X: x, Z: z, Width: w, Height: h}}
}

// A rectangular core encloses its bounding box and yard, as before.
func TestCoreFootprintRectangularCoreMatchesTheBox(t *testing.T) {
	t.Parallel()
	plan := LayoutPlan{Rooms: []LayoutRoom{footprintRoom(ModuleWorkshop, 60, 60, 10, 10), footprintRoom(ModuleStorage, 72, 60, 10, 10)}}
	fp := coreFootprint(plan, 150, 150)
	box := footprintBox(plan, 150, 150)
	if got, want := countCells(fp), int(box.Width*box.Height); got != want {
		t.Fatalf("footprint %d cells, box %d", got, want)
	}
	enc := planEnclosureCells(fp, 150, 150)
	if enc.bbox != pad(box, perimeterGap) {
		t.Fatalf("enclosure %v, want the box grown by the yard %v", enc.bbox, pad(box, perimeterGap))
	}
}

// An L-shaped core encloses no more ground than its footprint and yard, and a
// wide notch stays a notch.
func TestCoreFootprintLShapeKeepsItsNotch(t *testing.T) {
	t.Parallel()
	plan := LayoutPlan{Rooms: []LayoutRoom{
		footprintRoom(ModuleWorkshop, 40, 40, 50, 8),
		footprintRoom(ModuleStorage, 40, 50, 8, 40),
	}}
	const n = 150
	fp := coreFootprint(plan, n, n)
	enc := planEnclosureCells(fp, n, n)
	box := pad(footprintBox(plan, n, n), perimeterGap)
	if got := countCells(enc.in); got >= int(box.Width*box.Height) {
		t.Fatalf("enclosure holds %d cells of the %d-cell box", got, box.Width*box.Height)
	}
	// Nothing enclosed lies beyond the yard plus the closing from the footprint.
	for i, d := range chebyshevField(n, n, fp, perimeterGap+perimeterThick) {
		if enc.in[i] && d < 0 {
			t.Fatalf("cell %d is enclosed far from the footprint", i)
		}
	}
	if enc.inside(domain.Cell{X: 80, Z: 80}) {
		t.Fatal("the notch is enclosed")
	}
	if !enc.inside(domain.Cell{X: 44, Z: 85}) || !enc.inside(domain.Cell{X: 85, Z: 44}) {
		t.Fatal("an arm is outside the enclosure")
	}
}

// A narrow notch is closed, as the old box closed it.
func TestCoreFootprintNarrowNotchCloses(t *testing.T) {
	t.Parallel()
	plan := LayoutPlan{Rooms: []LayoutRoom{
		footprintRoom(ModuleWorkshop, 40, 40, 10, 10),
		footprintRoom(ModuleStorage, 54, 40, 10, 10),
	}}
	const n = 150
	enc := planEnclosureCells(coreFootprint(plan, n, n), n, n)
	if !enc.inside(domain.Cell{X: 52, Z: 45}) {
		t.Fatal("the gap between two rooms is not enclosed")
	}
}

// A rich patch inside the footprint is walled by the core ring only; the
// outer ring leaves it alone.
func TestOuterRingSkipsPatchInsideTheFootprint(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	const n = 150
	patch := Rectangle{X: 70, Z: 70, Width: 8, Height: 8}
	plan := LayoutPlan{Rooms: []LayoutRoom{
		footprintRoom(ModuleWorkshop, 50, 50, 40, 8),
		footprintRoom(ModuleStorage, 50, 90, 40, 8),
		footprintRoom(ModuleKitchen, 50, 58, 8, 32),
		footprintRoom(ModuleDining, 82, 58, 8, 32),
	}}
	zone := LayoutZone{Kind: ZoneField}
	for z := patch.Z; z < patch.Z+patch.Height; z++ {
		zone.Runs = append(zone.Runs, RowRun{Z: z, X: patch.X, Length: patch.Width})
	}
	plan.Zones = append(plan.Zones, zone)
	s := zoningSurvey(n, func(x, z int32) SurveyCell {
		c := SurveyCell{Walkable: true}
		if contains(patch, domain.Cell{X: x, Z: z}) {
			c.Fertility = 1.4
		}
		return c
	})
	out := PlanPerimeter(plan, s)
	if got := len(reservedCells(out, ReserveOuterWall)); got != 0 {
		t.Fatalf("%d outer wall cells around a patch the core already holds", got)
	}
	if len(reservedCells(out, ReservePerimeter)) == 0 {
		t.Fatal("no core ring")
	}
}

// The yard is never sited on farmland.
func TestYardSitesSkipFieldCells(t *testing.T) {
	t.Parallel()
	r, _ := yardField(nil)
	full := yardSite(t, r)
	field := LayoutZone{Kind: ZoneField}
	for _, c := range full.Room {
		field.Runs = append(field.Runs, RowRun{Z: c.Z, X: c.X, Length: 1})
	}
	r.Layout.Zones = append(r.Layout.Zones, field)
	for _, site := range PlanStorage(r).Sites {
		if site.Role == domain.YardRole {
			t.Fatalf("yard sited on farmland: %d room cells", len(site.Room))
		}
	}
}
