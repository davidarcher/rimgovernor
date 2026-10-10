package buildingruntime

import (
	"context"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
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
// beds, and only then is the ring sited around them.
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
	// The first-round shell stands on the planned shelter room.
	if !strings.HasPrefix(string(shell.Method), "shelter-shell-1-1-build-") {
		t.Fatal("shell method", shell.Method)
	}
	// The ring encloses every bunk, and no bunk shares a cell with another or
	// with a wall.
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
	used := map[domain.Cell]bool{}
	for _, bed := range beds {
		for _, p := range policy.BunkCells(bed.anchor, bed.rot) {
			if wall[p] || used[p] {
				t.Fatal("bed cell", p, wall[p], used[p])
			}
			used[p] = true
		}
	}
	// The slots are shared: every spot lies under the bed that replaces it,
	// after the spot was deleted: no bed was ever previewed over a standing
	// one.
	for _, spot := range spots {
		if !slices.Contains(beds, spot) {
			t.Fatal("spot", spot, "has no bed on its slot", beds)
		}
	}
	if n.overlays != 0 {
		t.Fatal("a bed was overlaid on a standing spot", n.overlays)
	}
}

// The ladder across tiers: with Bed locked and bedrolls stocked the
// spots are deleted and bedrolls placed on the freed slots; when Bed then
// becomes buildable the bedrolls are packed to storage (uninstalled, not
// deconstructed) and the beds go on the same slots. At no step is a bed or
// bedroll previewed over a standing piece.
func TestRoundsShelterLadderSpotsBedrollsBeds(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterSiteFixture(t)
	ctx := context.Background()
	n.def("Bed").ResearchPrerequisites = []string{"Beds"}
	n.buildable(policy.SleepingBedrollDefinition, 0, 1, 2)
	n.madeOf(policy.SleepingBedrollDefinition, "WoodLog")
	step := func(method domain.MethodID) store.PlanState {
		t.Helper()
		result, err := r.Step(ctx)
		if err != nil || result.Verdict != BuildingReasonAdmitted {
			t.Fatal(method, result, err)
		}
		id := methodPlan(t, result.Decision, method)
		completeRoundsBuildingMethod(t, db, result)
		plan, err := db.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(err)
		}
		return plan
	}
	spots := step(shelterSpotsMethod)
	slots := bunkAnchors(t, spots, "SleepingSpot")
	// The standing spots are first deleted, never built over.
	settleBunks(t, r, db, n)
	standBunks(n, standingBunks{"SleepingSpot", slots})
	for _, action := range step(shelterClearBedrollsMethod).Spec.Actions() {
		if cut, ok := action.Deconstruction(); !ok || cut.Definition() != "SleepingSpot" {
			t.Fatal("not a spot deletion", action)
		}
	}
	standBunks(n)
	rolls := step(shelterBedrollsMethod)
	if got := bunkAnchors(t, rolls, policy.SleepingBedrollDefinition); !slices.Equal(got, slots) {
		t.Fatal("bedrolls not on the spots' slots", got, slots)
	}
	// Bed becomes buildable: the standing bedrolls are packed, not deleted.
	settleBunks(t, r, db, n)
	standBunks(n, standingBunks{policy.SleepingBedrollDefinition, slots})
	n.def("Bed").ResearchPrerequisites = nil
	for _, action := range step(shelterClearBedsMethod).Spec.Actions() {
		if pack, ok := action.UninstallBuilding(); !ok || pack.Definition() != policy.SleepingBedrollDefinition {
			t.Fatal("not a bedroll pack", action)
		}
	}
	standBunks(n)
	beds := step(shelterBedsMethod)
	if got := bunkAnchors(t, beds, "Bed"); !slices.Equal(got, slots) {
		t.Fatal("beds not on the same slots", got, slots)
	}
	if n.overlays != 0 {
		t.Fatal("a bed or bedroll was overlaid on a standing piece", n.overlays)
	}
}

// While a spot is not yet standing, or its deletion is open, the bed rung
// waits rather than overlaying; it never previews a bed then.
func TestRoundsShelterBedRungWaitsForSpotsAndDeletion(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterSiteFixture(t)
	ctx := context.Background()
	spots, err := r.Step(ctx)
	if err != nil || spots.Verdict != BuildingReasonAdmitted {
		t.Fatal(spots, err)
	}
	if held, err := r.Step(ctx); err != nil || !held.Verdict.Is(WaitBunksOpen) {
		t.Fatal("open spots did not hold the bed rung", held, err)
	}
	completeRoundsBuildingMethod(t, db, spots)
	settleBunks(t, r, db, n)
	standBunks(n, standingBunks{"SleepingSpot", bunkAnchors(t, mustLoad(t, db, methodPlan(t, spots.Decision, shelterSpotsMethod)), "SleepingSpot")})
	clearing, err := r.Step(ctx)
	if err != nil || clearing.Verdict != BuildingReasonAdmitted {
		t.Fatal(clearing, err)
	}
	if held, err := r.Step(ctx); err != nil || !held.Verdict.Is(WaitBunksOpen) {
		t.Fatal("an open deletion did not hold the bed rung", held, err)
	}
	if n.overlays != 0 {
		t.Fatal("a bed was previewed over a standing spot", n.overlays)
	}
}

func mustLoad(t *testing.T, db *store.Store, id domain.PlanID) store.PlanState {
	t.Helper()
	plan, err := db.LoadPlan(context.Background(), id)
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

// A beds rung refused whole (the Bed is not buildable) falls through to
// the ring in the same review; the spots still lead.
func TestRoundsShelterBedsRefusedFallsThroughToShell(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterSiteFixture(t)
	ctx := context.Background()
	n.def("Bed").ResearchPrerequisites = []string{"Beds"}
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

// A bed rung admitted and still open does not hold the ring: the
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
	spotPlan := mustLoad(t, db, methodPlan(t, spots.Decision, shelterSpotsMethod))
	completeRoundsBuildingMethod(t, db, spots)
	settleBunks(t, r, db, n)
	standBunks(n, standingBunks{"SleepingSpot", bunkAnchors(t, spotPlan, "SleepingSpot")})
	clearing, err := r.Step(ctx)
	if err != nil || clearing.Verdict != BuildingReasonAdmitted {
		t.Fatal(clearing, err)
	}
	completeRoundsBuildingMethod(t, db, clearing)
	standBunks(n)
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
	stageShelterBunks(t, r, db, n)
	// The beds are open (placed, not yet built): the restart keeps them.
	restarted, err := NewRoundsShelterPlanner(r.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	shell, err := restarted.Step(ctx)
	if err != nil || shell.Verdict != BuildingReasonAdmitted {
		t.Fatal(shell, err)
	}
	record, err := restarted.shelterBunks(ctx, shell.Decision.Standard, domain.Unknown[policy.CurrentConstruction]())
	if err != nil || len(record.placed[shelterSpotsMethod]) != 2 || len(record.placed[shelterBedsMethod]) != 2 {
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
	for _, bunks := range record.placed {
		for _, bunk := range bunks {
			for _, c := range policy.BunkCells(bunk.anchor, bunk.rot) {
				if wall[c] {
					t.Fatal("ring on a recorded bunk", c)
				}
			}
		}
	}
	if again, err := restarted.Step(ctx); err != nil || again.Verdict != BuildingReasonExistingWork {
		t.Fatal("repeat review", again, err)
	}
	if goal, err := db.LoadStandard(ctx, shell.Decision.Standard.Standard.ID); err != nil || len(goal.Methods) != 3 {
		t.Fatal(goal.Methods, err)
	}
}
