package buildingruntime

import (
	"context"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func bunkAnchors(t *testing.T, plan store.PlanState, definition string) []domain.Cell {
	t.Helper()
	var anchors []domain.Cell
	for _, action := range plan.Spec.Actions() {
		b, ok := action.Building()
		if !ok || b.Definition() != definition || b.Rotation() != domain.North {
			t.Fatal("not a north-facing bunk", ok, b.Definition(), definition, b.Rotation())
		}
		anchors = append(anchors, b.Cell())
	}
	return anchors
}

// The first review places sleeping spots, the first construction is the
// beds, and only then is the ring sited around them (#612).
func TestRoutineShelterSpotsThenBedsThenShell(t *testing.T) {
	t.Parallel()
	r, db, n := shelterSiteFixture(t)
	ctx := context.Background()
	plans := stageShelterBunks(t, r, db, n)
	spots := bunkAnchors(t, plans[0], "SleepingSpot")
	beds := bunkAnchors(t, plans[1], "Bed")
	// Two colonists: two spots at the first review, then two wooden beds.
	if len(spots) != 2 || len(beds) != 2 {
		t.Fatal(spots, beds)
	}
	for _, action := range plans[1].Spec.Actions() {
		b, _ := action.Building()
		if b.Stuff() != "WoodLog" {
			t.Fatal("bed not wooden", action)
		}
	}
	result, err := r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted || n.previews != 32 {
		t.Fatal(result, err, n.previews)
	}
	shell, err := db.LoadPlan(ctx, shellMethod(result.Decision.Goal).Plan)
	if err != nil || !strings.HasPrefix(string(shell.Spec.ID()), "routine-shell") || len(shell.Progress) != 32 {
		t.Fatal(shell, err)
	}
	// The ring encloses every bunk, no bed touches a ring corner, and no
	// bunk shares a cell with another or with the storage patch.
	ring, err := domain.RectangleFootprint(domain.RoomBounds{X: 0, Z: 0, Width: 9, Height: 9}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	wall := map[domain.Cell]bool{}
	for _, action := range shell.Spec.Actions() {
		b, _ := action.Building()
		wall[b.Cell()] = true
	}
	if len(wall) != 32 {
		t.Fatal("ring cells", wall)
	}
	for _, w := range ring.Walls() {
		if !wall[w] {
			t.Fatal("ring does not enclose the bunks", w)
		}
	}
	corner := map[domain.Cell]bool{}
	for _, c := range policy.ShellCornerCells(ring) {
		corner[c] = true
	}
	used := map[domain.Cell]bool{}
	for _, anchor := range beds {
		for _, p := range policy.BunkFootprint(anchor) {
			if wall[p] || corner[p] || used[p] {
				t.Fatal("bed cell", p, wall[p], corner[p], used[p])
			}
			used[p] = true
		}
	}
	for _, anchor := range spots {
		for _, p := range policy.BunkFootprint(anchor) {
			if wall[p] || used[p] {
				t.Fatal("spot cell", p, wall[p], used[p])
			}
			used[p] = true
		}
	}
}

// A beds rung refused whole (the Bed is not buildable) falls through to
// the ring in the same review; the spots still lead.
func TestRoutineShelterBedsRefusedFallsThroughToShell(t *testing.T) {
	t.Parallel()
	r, db, n := shelterSiteFixture(t)
	ctx := context.Background()
	for _, d := range n.reply.GetObserved().Planning.GetObserved().Definitions {
		if d.Definition.GetDefName() == "Bed" {
			d.Available = proto.Bool(false)
		}
	}
	first, err := r.Step(ctx)
	if err != nil || first.Reason != BuildingMethodAdmitted {
		t.Fatal(first, err)
	}
	if plan := methodPlan(t, first.Decision, shelterSpotsMethod); !strings.HasPrefix(string(plan), bunkPlanPrefix+"-") {
		t.Fatal("spots did not lead", plan)
	}
	completeRoutineBuildingMethod(t, db, first)
	n.previews, n.calls = 0, 0
	second, err := r.Step(ctx)
	if err != nil || second.Reason != BuildingMethodAdmitted || n.previews != 32 {
		t.Fatal(second, err, n.previews)
	}
	plan, err := db.LoadPlan(ctx, shellMethod(second.Decision.Goal).Plan)
	if err != nil || !strings.HasPrefix(string(plan.Spec.ID()), "routine-shell") {
		t.Fatal(plan.Spec.ID(), err)
	}
	for _, m := range second.Decision.Goal.Methods {
		if m.Method == shelterBedsMethod {
			t.Fatal("beds admitted without a buildable Bed", m)
		}
	}
}

// A ring begun earlier is adopted as before: no bunk rung runs on a site
// whose shell already stands, however incomplete.
func TestRoutineShelterAdoptionSkipsBunks(t *testing.T) {
	t.Parallel()
	r, db, base := shelterSiteFixture(t)
	base.reply.GetObserved().PlayerTechLevel = proto.String("Neolithic")
	base.reply.GetObserved().Center = &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}
	hutCells(base, 21, func(int32, int32) bool { return true })
	want, err := domain.EllipseFootprint(domain.Cell{X: 10, Z: 10}, 4, 4, domain.EllipseNorthSouth, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	n := &adoptingNative{sleepingNative: base}
	n.standing = []bridge.Structure{{ID: "door", Definition: "Door", Cell: want.Door(), Status: "built"}}
	for i, w := range want.Walls() {
		if w != want.Door() && i%2 == 0 {
			n.standing = append(n.standing, bridge.Structure{ID: "frame", Definition: "Wall", Cell: w, Status: "frame"})
		}
	}
	planner, err := NewRoutineShelterPlanner(r.reviewer, n, nil)
	if err != nil {
		t.Fatal(err)
	}
	n.last = r.reviewer.player.session.State().Snapshot
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted || n.censuses == 0 {
		t.Fatal(result, err, n.censuses)
	}
	for _, m := range result.Decision.Goal.Methods {
		if strings.HasPrefix(string(m.Plan), bunkPlanPrefix+"-") {
			t.Fatal("bunk rung ran on an adopted shell", m)
		}
	}
	plan, err := db.LoadPlan(context.Background(), shellMethod(result.Decision.Goal).Plan)
	if err != nil || !strings.HasPrefix(string(plan.Spec.ID()), "routine-shell") {
		t.Fatal(plan.Spec.ID(), err)
	}
}
