package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The invariants every starter shell must hold whatever shape the search
// picks, checked across a table of sites and both shelter styles (#615).
// The shape-by-shape expectations live in starter_layout_test.go; this
// check is deliberately shape-blind, so a new template or a change to the
// growth path is covered the day it lands. Nothing here is computed with
// the footprint helpers under test: the ring is re-derived from the plan's
// own cells, the interior is flood-filled independently, and roof support
// is measured against RimWorld's documented radius rather than
// RoomFootprint.RoofSupported.

// shellSite is one site the starter search runs against.
type shellSite struct {
	name string
	// bounds and anchor place the map and the colony centre.
	bounds Bounds
	anchor domain.Cell
	// open, when set, reports the cells offered as buildable ground;
	// everything else is observed but unlit and unwalkable.
	open func(domain.Cell) bool
	// protected are player exclusions and accepted footprints.
	protected []domain.Cell
	// minInterior is the capacity the site must still yield: the smallest
	// interior any acceptable shell on it has.
	minInterior int
	// layouts, when positive, is the exact candidate count expected; 0
	// means "at least one".
	layouts int
	// none expects the search to decline the site outright.
	none bool
}

func shellSites() []shellSite {
	strip := func(low, high int32) func(domain.Cell) bool {
		return func(c domain.Cell) bool { return c.X >= low && c.X <= high }
	}
	return []shellSite{
		{name: "open map", bounds: Bounds{40, 40}, anchor: domain.Cell{X: 20, Z: 20}, minInterior: 25},
		// The anchor two cells from the corner: every candidate must stay
		// inside the map, so the search moves the shell off the edge.
		{name: "corner anchor", bounds: Bounds{24, 24}, anchor: domain.Cell{X: 2, Z: 2}, minInterior: 25},
		{name: "far corner anchor", bounds: Bounds{24, 24}, anchor: domain.Cell{X: 21, Z: 21}, minInterior: 25},
		// A player exclusion straight over the anchor: the shell must give
		// way to it rather than build over it.
		{name: "protected anchor", bounds: Bounds{40, 40}, anchor: domain.Cell{X: 20, Z: 20}, protected: rectCells(Rectangle{18, 18, 5, 5}), minInterior: 25},
		// Nine lit columns: no circle fits, an oval or a grown footprint does.
		{name: "nine-wide strip", bounds: Bounds{40, 40}, anchor: domain.Cell{X: 20, Z: 20}, open: strip(16, 24), minInterior: 20},
		// Five lit columns: no template at all fits and the shell is grown.
		{name: "five-wide strip", bounds: Bounds{40, 40}, anchor: domain.Cell{X: 20, Z: 20}, open: strip(18, 22), minInterior: starterInterior, layouts: 1},
		// A three-wide strip holds no roofable room of useful capacity.
		{name: "three-wide strip", bounds: Bounds{40, 40}, anchor: domain.Cell{X: 20, Z: 20}, open: strip(19, 21), none: true},
		// Nothing buildable at all.
		{name: "no open ground", bounds: Bounds{40, 40}, anchor: domain.Cell{X: 20, Z: 20}, open: func(domain.Cell) bool { return false }, none: true},
	}
}

// site builds the request for one table row.
func (s shellSite) request(style ShelterStyle) StarterRequest {
	r := StarterRequest{Bounds: s.bounds, Anchor: s.anchor, Protected: s.protected, Shelter: style,
		NutritionPerDay: domain.Known(5.0), CropGrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(1.0), FertilityMin: domain.Known(.7)}
	for x := int32(0); x < s.bounds.Width; x++ {
		for z := int32(0); z < s.bounds.Height; z++ {
			cell := domain.Cell{X: x, Z: z}
			usable := s.open == nil || s.open(cell)
			r.Cells = append(r.Cells, SiteCell{Cell: cell, Walkable: domain.Known(usable), Occupied: domain.Known(false), Zone: domain.Known(false),
				Roofed: domain.Known(false), SupportsLight: domain.Known(usable), Fertility: domain.Known(1.0)})
		}
	}
	return r
}

func TestStarterShellInvariantsAcrossSitesAndStyles(t *testing.T) {
	t.Parallel()
	for _, site := range shellSites() {
		for _, style := range []ShelterStyle{ShelterHut, ShelterRectangle} {
			t.Run(fmt.Sprintf("%s/%s", site.name, style), func(t *testing.T) {
				t.Parallel()
				request := site.request(style)
				layouts, err := StarterLayouts(request)
				if err != nil {
					t.Fatal(err)
				}
				switch {
				case site.none:
					if len(layouts) != 0 {
						t.Fatalf("%d shells sited on ground that holds none: %v", len(layouts), layouts[0].Shell.Bounds())
					}
					return
				case len(layouts) == 0:
					t.Fatal("no shell sited")
				case site.layouts > 0 && len(layouts) != site.layouts:
					t.Fatalf("%d candidates, want %d", len(layouts), site.layouts)
				}
				for i, layout := range layouts {
					checkShell(t, fmt.Sprintf("candidate %d", i), request, site, layout.Shell)
				}
			})
		}
	}
}

// checkShell holds one sited shell to the invariants, independent of the
// production geometry helpers.
func checkShell(t *testing.T, label string, r StarterRequest, site shellSite, shell domain.RoomFootprint) {
	t.Helper()
	usable := map[domain.Cell]bool{}
	for _, cell := range r.Cells {
		if walkable, known := cell.Walkable.Value(); known && walkable {
			if lit, known := cell.SupportsLight.Value(); known && lit {
				usable[cell.Cell] = true
			}
		}
	}
	guarded := map[domain.Cell]bool{}
	for _, cell := range r.Protected {
		guarded[cell] = true
	}
	ring := map[domain.Cell]bool{}
	doors := 0
	for _, cell := range shell.Walls() {
		if ring[cell] {
			t.Fatalf("%s: wall %v placed twice", label, cell)
		}
		ring[cell] = true
		if cell == shell.Door() {
			doors++
		}
		if cell.X < 0 || cell.Z < 0 || cell.X >= site.bounds.Width || cell.Z >= site.bounds.Height {
			t.Fatalf("%s: wall %v outside the map %v", label, cell, site.bounds)
		}
		if !usable[cell] {
			t.Fatalf("%s: wall %v on ground the site does not offer", label, cell)
		}
		if guarded[cell] {
			t.Fatalf("%s: wall %v on a protected cell", label, cell)
		}
	}
	if doors != 1 {
		t.Fatalf("%s: %d door cells on the ring", label, doors)
	}
	interior := map[domain.Cell]bool{}
	for _, cell := range shell.Interior() {
		if interior[cell] {
			t.Fatalf("%s: interior cell %v listed twice", label, cell)
		}
		if ring[cell] {
			t.Fatalf("%s: cell %v is both wall and interior", label, cell)
		}
		interior[cell] = true
		if !usable[cell] {
			t.Fatalf("%s: interior cell %v on ground the site does not offer", label, cell)
		}
		if guarded[cell] {
			t.Fatalf("%s: interior cell %v on a protected cell", label, cell)
		}
	}
	if len(interior) < site.minInterior {
		t.Fatalf("%s: interior of %d cells, want at least %d", label, len(interior), site.minInterior)
	}
	// The ring encloses the interior: a flood fill from one interior cell
	// that may cross only interior cells reaches all of them and never
	// steps off the shell.
	start := shell.Interior()[0]
	reached := map[domain.Cell]bool{start: true}
	queue := []domain.Cell{start}
	for len(queue) > 0 {
		cell := queue[0]
		queue = queue[1:]
		for _, next := range []domain.Cell{{X: cell.X + 1, Z: cell.Z}, {X: cell.X - 1, Z: cell.Z}, {X: cell.X, Z: cell.Z + 1}, {X: cell.X, Z: cell.Z - 1}} {
			if ring[next] || reached[next] {
				continue
			}
			if !interior[next] {
				t.Fatalf("%s: the interior leaks to %v, which is neither wall nor interior", label, next)
			}
			reached[next] = true
			queue = append(queue, next)
		}
	}
	if len(reached) != len(interior) {
		t.Fatalf("%s: %d of %d interior cells are walled off from each other", label, len(reached), len(interior))
	}
	// Roof support: every interior cell lies within RimWorld's
	// RoofCollapseUtility radius of a wall, so the game roofs the finished
	// room without columns.
	const support = 6
	for cell := range interior {
		supported := false
		for wall := range ring {
			dx, dz := int64(cell.X-wall.X), int64(cell.Z-wall.Z)
			supported = supported || dx*dx+dz*dz <= support*support
		}
		if !supported {
			t.Fatalf("%s: interior cell %v has no wall within %d cells to hold its roof", label, cell, support)
		}
	}
	// The entrance is consistent with the reachability the search was given:
	// the cell straight outside the door is buildable ground off the ring,
	// and no interior cell lies on the far side of the door.
	step := map[domain.Rotation]domain.Cell{domain.North: {Z: 1}, domain.South: {Z: -1}, domain.East: {X: 1}, domain.West: {X: -1}}[shell.Entrance()]
	threshold := domain.Cell{X: shell.Door().X + step.X, Z: shell.Door().Z + step.Z}
	if ring[threshold] || interior[threshold] {
		t.Fatalf("%s: the door at %v opens onto its own shell at %v", label, shell.Door(), threshold)
	}
	if !usable[threshold] || guarded[threshold] {
		t.Fatalf("%s: the door at %v opens onto %v, which the site does not offer", label, shell.Door(), threshold)
	}
	inward := domain.Cell{X: shell.Door().X - step.X, Z: shell.Door().Z - step.Z}
	if !interior[inward] {
		t.Fatalf("%s: the door at %v does not lead into the room (%v is not interior)", label, shell.Door(), inward)
	}
}
