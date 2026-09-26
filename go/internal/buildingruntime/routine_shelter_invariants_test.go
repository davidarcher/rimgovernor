package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The invariants every shell the planner admits must hold, whatever shape
// the site and the style lead it to (#615). The named-shape expectations
// live in routine_shelter_test.go; this check is shape-blind, so it covers
// a new template or a change to the growth path without a native run. The
// interior is flood-filled from the staged beds rather than read from the
// footprint helpers the planner used.

// shellVariant is one site the planner sites a ring on.
type shellVariant struct {
	name string
	// side is the square census the fixture observes; 0 keeps the 9x9 site.
	side int32
	// tech is the player faction's tech level, which picks the style.
	tech string
	// lit reports the cells that support light; nil means all of them.
	lit func(x, z int32) bool
	// center is the colony centre the search anchors on when side is set.
	center domain.Cell
}

func TestRoutineShelterShellInvariantsAcrossSites(t *testing.T) {
	t.Parallel()
	variants := []shellVariant{
		{name: "9x9 rectangle site"},
		{name: "neolithic hut", side: 21, tech: "Neolithic", center: domain.Cell{X: 10, Z: 10}},
		{name: "industrial rectangle", side: 21, tech: "Industrial", center: domain.Cell{X: 10, Z: 10}},
		{name: "grown shell on constrained terrain", side: 21, tech: "Neolithic", center: domain.Cell{X: 10, Z: 10},
			lit: func(x, z int32) bool { return x >= 8 && x <= 12 && z >= 1 || z >= 8 && z <= 12 && x >= 8 }},
	}
	for _, variant := range variants {
		t.Run(variant.name, func(t *testing.T) {
			t.Parallel()
			ctx := context.Background()
			planner, db, n := shelterSiteFixture(t)
			if variant.tech != "" {
				n.reply.GetObserved().PlayerTechLevel = proto.String(variant.tech)
			}
			if variant.side > 0 {
				n.reply.GetObserved().Center = &c.Cell{X: proto.Int32(variant.center.X), Z: proto.Int32(variant.center.Z)}
				lit := variant.lit
				if lit == nil {
					lit = func(int32, int32) bool { return true }
				}
				hutCells(n, variant.side, lit)
			}
			bunks := stageShelterBunks(t, planner, db, n)
			beds := bunkCellsOf(t, bunks[len(bunks)-1])
			result, err := planner.Step(ctx)
			if err != nil || result.Reason != BuildingMethodAdmitted {
				t.Fatal(result, err)
			}
			plan, err := db.LoadPlan(ctx, shellMethod(result.Decision.Goal).Plan)
			if err != nil {
				t.Fatal(err)
			}
			door, ring := shellCells(t, plan)
			if n.previews != len(ring) {
				t.Fatalf("%d previews for a ring of %d cells: the ring is admitted whole", n.previews, len(ring))
			}
			checkAdmittedShell(t, n, door, ring, beds)
		})
	}
}

// bunkCellsOf is every cell the bed rung's plan covers, from the 1x2 bunk
// footprint each bed occupies.
func bunkCellsOf(t *testing.T, plan store.PlanState) map[domain.Cell]bool {
	t.Helper()
	cells := map[domain.Cell]bool{}
	for _, action := range plan.Spec.Actions() {
		b, ok := action.Building()
		if !ok || b.Definition() != "Bed" {
			t.Fatalf("the last bunk rung places %v, want beds", action)
		}
		for _, cell := range policy.BunkFootprint(b.Cell()) {
			cells[cell] = true
		}
	}
	if len(cells) == 0 {
		t.Fatal("no beds staged")
	}
	return cells
}

// checkAdmittedShell holds one admitted ring to the invariants: it stands
// on ground the census offered, encloses the beds it was raised around, and
// its door leads in from open ground.
func checkAdmittedShell(t *testing.T, n *sleepingNative, door domain.Building, ring map[domain.Cell]bool, beds map[domain.Cell]bool) {
	t.Helper()
	offered, bounds := offeredSite(n)
	for cell := range ring {
		if cell.X < 0 || cell.Z < 0 || cell.X > bounds.X || cell.Z > bounds.Z {
			t.Fatalf("shell cell %v outside the observed map (maximum %v)", cell, bounds)
		}
		if !offered[cell] {
			t.Fatalf("shell cell %v is not open ground the census offered", cell)
		}
		if beds[cell] {
			t.Fatalf("shell cell %v stands on a staged bed", cell)
		}
	}
	if !ring[door.Cell()] {
		t.Fatalf("the door at %v is not on the ring", door.Cell())
	}
	if door.Rotation() != domain.South {
		t.Fatalf("the door faces %v, want south", door.Rotation())
	}
	// The ring encloses the beds: a flood fill from a bed cell that may not
	// cross the ring reaches every other bed and never leaves the ring's
	// bounding box.
	minimum, maximum := domain.Cell{X: bounds.X, Z: bounds.Z}, domain.Cell{}
	for cell := range ring {
		minimum.X, minimum.Z = min(minimum.X, cell.X), min(minimum.Z, cell.Z)
		maximum.X, maximum.Z = max(maximum.X, cell.X), max(maximum.Z, cell.Z)
	}
	var start domain.Cell
	for cell := range beds {
		start = cell
		break
	}
	interior := map[domain.Cell]bool{start: true}
	queue := []domain.Cell{start}
	for len(queue) > 0 {
		cell := queue[0]
		queue = queue[1:]
		if cell.X <= minimum.X || cell.X >= maximum.X || cell.Z <= minimum.Z || cell.Z >= maximum.Z {
			t.Fatalf("the beds are not enclosed: the interior reaches %v, on or outside the ring's bounds %v-%v", cell, minimum, maximum)
		}
		for _, next := range []domain.Cell{{X: cell.X + 1, Z: cell.Z}, {X: cell.X - 1, Z: cell.Z}, {X: cell.X, Z: cell.Z + 1}, {X: cell.X, Z: cell.Z - 1}} {
			if ring[next] || interior[next] {
				continue
			}
			interior[next] = true
			queue = append(queue, next)
		}
	}
	for cell := range beds {
		if !interior[cell] {
			t.Fatalf("staged bed cell %v is not inside the ring", cell)
		}
	}
	// Capacity: the enclosed room holds every staged bed with room left to
	// walk, and every interior cell is within RimWorld's roof support radius
	// of a wall, so the finished room roofs itself.
	if len(interior) <= len(beds) {
		t.Fatalf("interior of %d cells for %d bed cells leaves no aisle", len(interior), len(beds))
	}
	const support = 6
	for cell := range interior {
		supported := false
		for wall := range ring {
			dx, dz := int64(cell.X-wall.X), int64(cell.Z-wall.Z)
			supported = supported || dx*dx+dz*dz <= support*support
		}
		if !supported {
			t.Fatalf("interior cell %v has no wall within %d cells to hold its roof", cell, support)
		}
	}
	// The door leads in from ground outside the ring.
	outside := domain.Cell{X: door.Cell().X, Z: door.Cell().Z - 1}
	inside := domain.Cell{X: door.Cell().X, Z: door.Cell().Z + 1}
	if ring[outside] || interior[outside] {
		t.Fatalf("the south door at %v opens onto its own shell at %v", door.Cell(), outside)
	}
	if !interior[inside] {
		t.Fatalf("the south door at %v does not lead into the enclosed room", door.Cell())
	}
}

// offeredSite is the open ground the fixture's census reports -- outdoors,
// unroofed, walkable, unoccupied, lit and unzoned -- with the census
// region's maximum cell.
func offeredSite(n *sleepingNative) (map[domain.Cell]bool, domain.Cell) {
	cells := n.reply.GetObserved().Planning.GetObserved().Cells
	offered := map[domain.Cell]bool{}
	for _, cell := range cells.Cells {
		roofed := cell.Roof != nil
		for _, issue := range cell.Issues {
			if issue.GetField() == "roof" {
				roofed = false
			}
		}
		if cell.GetIndoors() || roofed || !cell.GetWalkable() || cell.GetOccupied() || !cell.GetSupportsLight() || cell.ZoneId != nil {
			continue
		}
		offered[domain.Cell{X: cell.Cell.GetX(), Z: cell.Cell.GetZ()}] = true
	}
	return offered, domain.Cell{X: cells.Region.Maximum.GetX(), Z: cells.Region.Maximum.GetZ()}
}

// Ground the game already roofed is no site for a shell: the planner has no
// method rather than raising a ring under an existing roof.
func TestRoutineShelterRefusesAlreadyRoofedGround(t *testing.T) {
	t.Parallel()
	planner, _, n := shelterSiteFixture(t)
	for _, cell := range n.reply.GetObserved().Planning.GetObserved().Cells.Cells {
		var issues []*o.ReadIssue
		for _, issue := range cell.Issues {
			if issue.GetField() != "roof" {
				issues = append(issues, issue)
			}
		}
		cell.Issues, cell.Roof = issues, proto.String("RoofRockThick")
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodNoSpace {
		t.Fatal("a shell was sited on roofed ground", result, err)
	}
}
