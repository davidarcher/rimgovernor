package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"slices"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// bunkAnchors are the cells and rotations a bunk rung's plan places, which
// must be the shelter template's slot rotation (the head away from the door).
func bunkAnchors(t *testing.T, plan store.PlanState, definition string) []shelterBunk {
	t.Helper()
	var bunks []shelterBunk
	for _, action := range plan.Spec.Actions() {
		b, ok := action.Building()
		if !ok || b.Definition() != definition || b.Rotation() != domain.South {
			t.Fatal("not a template-rotated bunk", ok, b.Definition(), definition, b.Rotation())
		}
		bunks = append(bunks, shelterBunk{b.Cell(), b.Rotation()})
	}
	return bunks
}

// The first review places sleeping spots, the first construction is the
// beds, and only then is the ring sited around them (#612).
func TestRoundsShelterSpotsThenBedsThenShell(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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
	if err != nil || result.Verdict != BuildingReasonAdmitted || n.previews != 32 {
		t.Fatal(result, err, n.previews)
	}
	shell, err := db.LoadPlan(ctx, shellMethod(result.Decision.Standard).Plan)
	if err != nil || !IsShellMethod(shell.Method) || len(shell.Progress) != 32 {
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
	for _, bed := range beds {
		for _, p := range policy.BunkCells(bed.anchor, bed.rot) {
			if wall[p] || corner[p] || used[p] {
				t.Fatal("bed cell", p, wall[p], corner[p], used[p])
			}
			used[p] = true
		}
	}
	// The slots are shared: every spot lies under the bed that replaces it.
	for _, spot := range spots {
		if !slices.Contains(beds, spot) {
			t.Fatal("spot", spot, "has no bed on its slot", beds)
		}
	}
}

// A beds rung refused whole (the Bed is not buildable) falls through to
// the ring in the same review; the spots still lead.
func TestRoundsShelterBedsRefusedFallsThroughToShell(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterSiteFixture(t)
	ctx := context.Background()
	n.catalogRow("Bed").Research = []string{"Beds"}
	first, err := r.Step(ctx)
	if err != nil || first.Verdict != BuildingReasonAdmitted {
		t.Fatal(first, err)
	}
	if plan := methodPlan(t, first.Decision, shelterSpotsMethod); plan == "" {
		t.Fatal("spots did not lead", plan)
	}
	completeRoundsBuildingMethod(t, db, first)
	n.previews, n.calls = 0, 0
	second, err := r.Step(ctx)
	if err != nil || second.Verdict != BuildingReasonAdmitted || n.previews != 32 {
		t.Fatal(second, err, n.previews)
	}
	plan, err := db.LoadPlan(ctx, shellMethod(second.Decision.Standard).Plan)
	if err != nil || !IsShellMethod(plan.Method) {
		t.Fatal(plan.Spec.ID(), err)
	}
	for _, m := range second.Decision.Standard.Methods {
		if m.Method == shelterBedsMethod {
			t.Fatal("beds admitted without a buildable Bed", m)
		}
	}
}

// A ring begun earlier is adopted as before: no bunk rung runs on a site
// whose shell already stands, however incomplete.
func TestRoundsShelterAdoptionSkipsBunks(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, base := shelterSiteFixture(t)
	base.reply.GetObserved().Center = &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}
	centreOn(r.reviewer, domain.Cell{X: 10, Z: 10})
	hutCells(base, 21, func(int32, int32) bool { return true })
	recordStoreroom(t, r, db, policy.Rectangle{X: 7, Z: 7, Width: 7, Height: 7})
	want, err := domain.RectangleFootprint(domain.RoomBounds{X: 6, Z: 6, Width: 9, Height: 9}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	n := &adoptingNative{sleepingNative: base}
	n.standing = []bridge.Structure{{ID: "door", Definition: "Door", Cell: want.Door(), Status: o.BuildingStatus_BUILDING_STATUS_BUILT}}
	for i, w := range want.Walls() {
		if w != want.Door() && i%2 == 0 {
			n.standing = append(n.standing, bridge.Structure{ID: "frame", Definition: "Wall", Cell: w, Status: o.BuildingStatus_BUILDING_STATUS_FRAME})
		}
	}
	planner, err := NewRoundsShelterPlanner(r.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	n.last = r.reviewer.player.session.State().Snapshot
	result, err := planner.Step(context.Background())
	if err != nil || result.Verdict != BuildingReasonAdmitted || n.censuses == 0 {
		t.Fatal(result, err, n.censuses)
	}
	for _, m := range result.Decision.Standard.Methods {
		if m.Method == shelterSpotsMethod || m.Method == shelterBedsMethod {
			t.Fatal("bunk rung ran on an adopted shell", m)
		}
	}
	plan, err := db.LoadPlan(context.Background(), shellMethod(result.Decision.Standard).Plan)
	if err != nil || !IsShellMethod(plan.Method) {
		t.Fatal(plan.Spec.ID(), err)
	}
}

// A bed rung admitted and still open does not hold the ring (#641): the
// review after it admits the shell around the pending bunks, and the review
// after that adds nothing, neither a second shell nor a second bed rung.
func TestRoundsShelterStalledBedsAdmitShell(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterSiteFixture(t)
	ctx := context.Background()
	spots, err := r.Step(ctx)
	if err != nil || spots.Verdict != BuildingReasonAdmitted {
		t.Fatal(spots, err)
	}
	methodPlan(t, spots.Decision, shelterSpotsMethod)
	// The spots stay open too: an interim that has not stood yet holds
	// neither the beds nor the ring.
	beds, err := r.Step(ctx)
	if err != nil || beds.Verdict != BuildingReasonAdmitted {
		t.Fatal(beds, err)
	}
	bedPlan, err := db.LoadPlan(ctx, methodPlan(t, beds.Decision, shelterBedsMethod))
	if err != nil {
		t.Fatal(err)
	}
	n.previews, n.calls = 0, 0
	shell, err := r.Step(ctx)
	if err != nil || shell.Verdict != BuildingReasonAdmitted {
		t.Fatal("stalled beds held the ring", shell, err)
	}
	plan, err := db.LoadPlan(ctx, shellMethod(shell.Decision.Standard).Plan)
	if err != nil || !IsShellMethod(plan.Method) {
		t.Fatal(plan.Spec.ID(), err)
	}
	wall := map[domain.Cell]bool{}
	for _, action := range plan.Spec.Actions() {
		b, _ := action.Building()
		wall[b.Cell()] = true
	}
	for _, bed := range bunkAnchors(t, bedPlan, "Bed") {
		for _, p := range policy.BunkCells(bed.anchor, bed.rot) {
			if wall[p] {
				t.Fatal("the ring overlaps a pending bed", p)
			}
		}
	}
	again, err := r.Step(ctx)
	if err != nil || again.Verdict != BuildingReasonExistingWork {
		t.Fatal("repeat review", again, err)
	}
	goal, err := db.LoadStandard(ctx, shell.Decision.Standard.Standard.ID)
	if err != nil || len(goal.Methods) != 3 {
		t.Fatal("methods", goal.Methods, err)
	}
}

// A restart (a fresh planner over the same journal) with spots and beds
// placed and the beds still open neither duplicates a rung nor forgets
// their geometry: the ring is sited around the recorded bunks.
func TestRoundsShelterRestartKeepsBunks(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterSiteFixture(t)
	ctx := context.Background()
	for range 2 {
		if result, err := r.Step(ctx); err != nil || result.Verdict != BuildingReasonAdmitted {
			t.Fatal(result, err)
		}
	}
	restarted, err := NewRoundsShelterPlanner(r.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	shell, err := restarted.Step(ctx)
	if err != nil || shell.Verdict != BuildingReasonAdmitted {
		t.Fatal(shell, err)
	}
	record, err := restarted.shelterBunks(ctx, shell.Decision.Standard)
	if err != nil || len(record.spots) != 2 || len(record.beds) != 2 {
		t.Fatal(record, err)
	}
	plan, err := db.LoadPlan(ctx, shellMethod(shell.Decision.Standard).Plan)
	if err != nil {
		t.Fatal(err)
	}
	wall := map[domain.Cell]bool{}
	for _, action := range plan.Spec.Actions() {
		b, _ := action.Building()
		wall[b.Cell()] = true
	}
	for _, c := range record.cells() {
		if wall[c] {
			t.Fatal("ring on a recorded bunk", c)
		}
	}
	if again, err := restarted.Step(ctx); err != nil || again.Verdict != BuildingReasonExistingWork {
		t.Fatal("repeat review", again, err)
	}
	if goal, err := db.LoadStandard(ctx, shell.Decision.Standard.Standard.ID); err != nil || len(goal.Methods) != 3 {
		t.Fatal(goal.Methods, err)
	}
}
