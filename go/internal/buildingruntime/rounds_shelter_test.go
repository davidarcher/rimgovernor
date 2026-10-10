package buildingruntime

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// shelterSiteFixture is the open 9x9 site before any sleeping work: the
// first Step places the sleeping spots.
func shelterSiteFixture(t *testing.T) (*RoundsBuildingPlanner, *store.Store, *sleepingNative) {
	t.Helper()
	r, db, _, _, n := sleepingFixture(t)
	// The ring reconciles against the construction census: empty, not unknown.
	n.built = map[domain.ActionID]*o.BuildingState{}
	// A wooded start: the shell builds from wood unless the map is short of it.
	for _, row := range n.reply.GetObserved().GetResources() {
		if row.GetDefName() == "WoodLog" {
			row.Units = proto.Int64(policy.ShellWallBudget * 5)
		}
	}
	for _, name := range []string{"Wall", "Door"} {
		n.buildable(name, 0, 1, 1)
		n.onlyStuff(name, "WoodLog")
	}
	n.buildable("Bed", 0, 1, 2)
	n.onlyStuff("Bed", "WoodLog")
	// The row below the 9x9 site is the planned door's threshold: a cell
	// the census must show open or the room reads as owing a dig.
	n.cells.Region = policy.Rectangle{Z: -1, Width: 9, Height: 10}
	n.cells.Cells = nil
	for x := int32(0); x < 9; x++ {
		for z := int32(-1); z < 9; z++ {
			n.cells.Cells = append(n.cells.Cells, openCell(x, z))
		}
	}
	n.onPreview = func(_ context.Context, v *bridge.BuildingPreview) {
		b, _ := v.Preview.Action.Building()
		if b.Definition() == "SleepingSpot" {
			return
		}
		if b.Definition() == "Bed" || b.Definition() == policy.SleepingBedrollDefinition {
			// A wooden bed on the default 1x2 footprint; its stock is not
			// charged so the ring's budget tests count the ring alone.
			v.Preview.MadeFromStuff = domain.Known(true)
			return
		}
		cost := int64(5)
		if b.Definition() == "Door" {
			cost = 25
		}
		v.Preview.MadeFromStuff = domain.Known(true)
		v.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
		v.Preview.Costs = domain.Known([]policy.Amount{{Resource: "WoodLog", Count: cost}})
		v.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(180))}}
	}
	planner, err := NewRoundsShelterPlanner(r.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	recordStoreroom(t, planner, db, policy.Rectangle{X: 1, Z: 1, Width: 7, Height: 7})
	return planner, db, n
}

// recordStoreroom records a layout plan whose only room is a storeroom
// with the given interior and a south door mid-wall: the initial shelter's
// planned room.
func recordStoreroom(t *testing.T, r *RoundsBuildingPlanner, db *store.Store, interior policy.Rectangle) policy.PlannedRoom {
	t.Helper()
	room := policy.PlannedRoom{Role: policy.PlannedShelter, Interior: interior, Door: domain.Cell{X: interior.X + interior.Width/2, Z: interior.Z - 1}, DoorRot: domain.South}
	recordLayout(t, r, db, policy.LayoutPlan{Rooms: []policy.PlannedRoom{room}})
	return room
}

// recordLayout records plan and serves it to the planner's next read: the
// fixture's review already ran without one.
func recordLayout(t *testing.T, r *RoundsBuildingPlanner, db *store.Store, plan policy.LayoutPlan) {
	t.Helper()
	if err := db.RecordLayoutPlan(context.Background(), r.reviewer.player.session.State().Snapshot, 0, plan); err != nil {
		t.Fatal(err)
	}
	census := &r.reviewer.census
	census.mu.Lock()
	if census.latest != nil {
		census.latest.reading.Projection.LayoutPlan = domain.Known(plan)
		census.latest.reading.Sections.Colony.Value.LayoutPlan = domain.Known(plan)
	}
	census.layout = domain.Known(plan)
	census.mu.Unlock()
}

// shelterFixture is the site with its spots and beds already staged, so the
// next Step sites the ring around them; the tests of the ring itself start
// here.
func shelterFixture(t *testing.T) (*RoundsBuildingPlanner, *store.Store, *sleepingNative) {
	t.Helper()
	planner, db, n := shelterSiteFixture(t)
	stageShelterBunks(t, planner, db, n)
	return planner, db, n
}

// shellMethod is the method the ring was admitted under: the one that
// is not a bunk rung, or the last bound when only bunks are.
func shellMethod(goal store.StandardState) domain.Method {
	for _, m := range goal.Methods {
		if !isShelterBunkMethod(m.Method) {
			return m
		}
	}
	return goal.Methods[len(goal.Methods)-1]
}

// The shell is one wave: the door leads the dispatch order and no wall is
// gated on it completing, since a door blueprint seals nothing.
func TestRoundsShelterAdmitsWholeShellInOneWave(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterFixture(t)
	result, err := r.Step(context.Background())
	if err != nil || result.Verdict != BuildingReasonAdmitted || len(result.Decision.Refused) != 0 || n.previews != 32 {
		t.Fatal(result, err, n.previews)
	}
	plan, err := db.LoadPlan(context.Background(), shellMethod(result.Decision.Standard).Plan)
	if err != nil || len(plan.Progress) != 32 || len(plan.Spec.Dependencies()) != 0 {
		t.Fatal(plan, err)
	}
	actions := plan.Spec.Actions()
	snapshot := result.Decision.Standard.Standard.Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	door, _ := actions[0].Building()
	if door.Definition() != "Door" || door.Cell() != (domain.Cell{X: 4, Z: 0}) {
		t.Fatal(door)
	}
	for i, action := range actions {
		b, _ := action.Building()
		if b.Stuff() != "WoodLog" || i > 0 && b.Definition() != "Wall" || plan.Progress[i].View().Attempt != 0 {
			t.Fatal(action, plan.Progress[i])
		}
		// Every wall is dispatchable alongside the unbuilt door.
		if err := plan.Spec.CheckDependencies(action.ID(), plan.Progress, snapshot, 7); err != nil {
			t.Fatal("wall waits for the door", action, err)
		}
	}
	if again, err := r.Step(context.Background()); err != nil || again.Verdict != BuildingReasonExistingWork || n.previews != 32 {
		t.Fatal(again, err)
	}
}

// A shell is admitted without a stock check: RimWorld places its blueprints
// regardless and the frames hold natively for materials.
func TestRoundsShelterAdmitsShellWithoutStockCheck(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"stock-short", "stock-zero", "stock-unknown"} {
		t.Run(change, func(t *testing.T) {
			t.Parallel()
			r, db, n := shelterFixture(t)
			base := n.onPreview
			n.onPreview = func(ctx context.Context, v *bridge.BuildingPreview) {
				base(ctx, v)
				switch change {
				case "stock-short":
					v.Stock.Values[0].Available = domain.Known(int64(179))
				case "stock-zero":
					v.Stock.Values[0].Available = domain.Known(int64(0))
				case "stock-unknown":
					v.Stock.Values[0].Available = domain.Unknown[int64]()
				}
			}
			result, err := r.Step(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			plans, err := db.LoadPlans(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			if result.Verdict != BuildingReasonAdmitted || len(plans) != 5 {
				t.Fatal("shell not admitted", result, len(plans))
			}
			plan, err := db.LoadPlan(context.Background(), shellMethod(result.Decision.Standard).Plan)
			if err != nil || len(plan.Progress) != 32 {
				t.Fatal(plan, err)
			}
			if len(result.Decision.Refused) != 0 {
				t.Fatal("shell not admitted whole", len(plan.Progress), result.Decision.Refused)
			}
			if again, err := r.Step(context.Background()); err != nil || again.Verdict != BuildingReasonExistingWork {
				t.Fatal(again, err)
			}
		})
	}
}

// An adopted shell holds through a wood shortage: once the ring is
// on record, a step that reads no wood (or none at all) keeps the plan,
// previews nothing and sites no second shell; the frames wait natively for
// the wood MaintainResource chops (TestReplayWoodShortageKeepsTheShelterOwed).
func TestRoundsShelterHoldsThroughWoodShortage(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterFixture(t)
	result, err := r.Step(context.Background())
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	shell := shellMethod(result.Decision.Standard).Plan
	base, previews := n.onPreview, n.previews
	n.onPreview = func(ctx context.Context, v *bridge.BuildingPreview) {
		base(ctx, v)
		v.Stock.Values[0].Available = domain.Known(int64(0))
	}
	for range 3 {
		again, err := r.Step(context.Background())
		if err != nil || again.Verdict != BuildingReasonExistingWork || n.previews != previews {
			t.Fatal("shortage replanned the shell", again, err, n.previews)
		}
	}
	plans, err := db.LoadPlans(context.Background())
	if err != nil || len(plans) != 5 {
		t.Fatal("second shell or order under the shortage", len(plans), err)
	}
	plan, err := db.LoadPlan(context.Background(), shell)
	if err != nil || len(plan.Progress) != 32 || !store.PlanOpen(plan) {
		t.Fatal("adopted shell dropped", plan, err)
	}
}

// A stock count that moves between the ring's previews is not a refusal: the
// bundle is funded from the lowest value (b93181dca,
// TestMergeRoundsStockTakesTheLowestAvailable), so no stock-conflict case.
func TestRoundsShelterNeverCommitsPartialOrUnknownShell(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	for _, change := range []string{"late-refusal", "definition"} {
		t.Run(change, func(t *testing.T) {
			r, db, n := shelterFixture(t)
			base := n.onPreview
			n.onPreview = func(ctx context.Context, v *bridge.BuildingPreview) {
				base(ctx, v)
				switch change {
				case "late-refusal":
					if n.previews == 32 {
						v.Preview.SafeToPlace = domain.Known(false)
					}
				}
			}
			switch change {
			case "definition":
				// Any shell piece wider than one cell is refused; the wall, since
				// the Core door row now states its own 1x1 size.
				n.def("Wall").Size.X = 2
			}
			result, err := r.Step(context.Background())
			if err == nil && result.Verdict == BuildingReasonAdmitted {
				t.Fatal("invalid shell admitted", change)
			}
			// The load plan and the two bunk plans are all the journal holds.
			plans, err := db.LoadPlans(context.Background())
			if err != nil || len(plans) != 4 {
				t.Fatal("partial shell committed", plans, err)
			}
		})
	}
}

func TestRoundsShelterPrefersExistingRoom(t *testing.T) {
	t.Parallel()
	r, db, _, _, n := bedroomFixture(t)
	planner, err := NewRoundsShelterPlanner(r.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), shellMethod(result.Decision.Standard).Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, action := range plan.Spec.Actions() {
		b, _ := action.Building()
		if b.Definition() != "SleepingSpot" {
			t.Fatal("unnecessary shell", b)
		}
	}
}

func TestShelterRoofingBudgetCountsFromTheApplyReceiptAndDoesNotRenew(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, _ := shelterFixture(t)
	result, err := r.Step(context.Background())
	if err != nil || !result.Decision.Admitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), shellMethod(result.Decision.Standard).Plan)
	if err != nil {
		t.Fatal(err)
	}
	current := result.Decision.Standard.Standard.Snapshot
	snapshot := current
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	if shelterNativeWorkTicks(plan, current, 7) != 0 {
		t.Fatal("pending shell granted roofing time")
	}
	for i, p := range plan.Progress {
		p, err = p.Prepare(snapshot, 100)
		if err != nil {
			t.Fatal(err)
		}
		p, err = p.MarkDispatched(snapshot, 100)
		if err != nil {
			t.Fatal(err)
		}
		p, err = p.RecordReceipt(1, domain.ReceiptAccepted)
		if err != nil {
			t.Fatal(err)
		}
		plan.Progress[i] = p
	}
	for _, test := range []struct {
		tick domain.Tick
		want uint32
	}{{99, 0}, {100, 10000}, {101, 9999}, {10099, 1}, {10100, 0}, {11000, 0}} {
		if got := shelterNativeWorkTicks(plan, current, test.tick); got != test.want {
			t.Fatal(test, got)
		}
	}
	// A native order generation moved on by an authority re-acquisition
	// leaves the standing walls and their roofing budget alone; a
	// different world does not.
	current.Native++
	if shelterNativeWorkTicks(plan, current, 100) != 10000 {
		t.Fatal("a new native generation dropped the roofing budget")
	}
	current.Load = "other-load"
	if shelterNativeWorkTicks(plan, current, 100) != 0 {
		t.Fatal("another world kept the roofing budget")
	}
}

func TestRoundsShelterManualCancelsWholePendingShell(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterFixture(t)
	ctx := context.Background()
	result, err := r.Step(ctx)
	if err != nil || !result.Decision.Admitted {
		t.Fatal(result, err)
	}
	request := store.ControlRequest{RequestID: "manual-shell", Kind: store.PauseControl, World: playerWorld(result.Decision.Standard.Standard.Snapshot)}
	if _, err := r.reviewer.player.Pause(ctx, request); err != nil {
		t.Fatal(err)
	}
	plan, err := db.LoadPlan(ctx, shellMethod(result.Decision.Standard).Plan)
	if err != nil || len(plan.Progress) != 32 {
		t.Fatal(plan, err)
	}
	for _, p := range plan.Progress {
		if p.View().Stage != domain.Pending || p.View().Attempt != 0 {
			t.Fatal(p)
		}
	}
	reads := n.reads
	again, err := r.Step(ctx)
	if err != nil || again.Verdict != BuildingReasonDisabled || again.NativeWorkTicks != 0 || n.reads != reads {
		t.Fatal(again, err, n.reads)
	}
}

func completeRoundsBuildingMethod(t *testing.T, db *store.Store, result RoundsBuildingResult) {
	t.Helper()
	ctx := context.Background()
	var plan store.PlanState
	for _, method := range result.Decision.Owner().OwnerMethods() {
		candidate, err := db.LoadPlan(ctx, method.Plan)
		if err != nil {
			t.Fatal(err)
		}
		if domain.StandardWorkOpen(candidate.Progress) {
			if len(plan.Progress) != 0 {
				t.Fatal("multiple open methods")
			}
			plan = candidate
		}
	}
	if len(plan.Progress) == 0 {
		t.Fatal("no pending method")
	}
	summary, _ := store.SummarizeOwner(result.Decision.Owner())
	snapshot := summary.Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	for _, action := range plan.Spec.Actions() {
		if _, err := db.Prepare(ctx, plan.Spec.ID(), action.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err := db.Dispatch(ctx, plan.Spec.ID(), action.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err := db.RecordReceipt(ctx, plan.Spec.ID(), action.ID(), 1, domain.ReceiptAccepted); err != nil {
			t.Fatal(err)
		}
	}
}

// stageShelterBunks walks the initial shelter's bunk rungs: every
// bunk method the planner admits (the spots, then the beds) is completed in
// the journal, so the next Step sites the ring around them. It returns the
// bunk plans in the order admitted and resets the fixture's preview
// counters, so a test's shell assertions count the ring alone.
func stageShelterBunks(t *testing.T, r *RoundsBuildingPlanner, db *store.Store, n *sleepingNative, afterSettle ...func()) []store.PlanState {
	t.Helper()
	ctx := context.Background()
	var plans []store.PlanState
	upkeep := n.reply.GetObserved().Upkeep
	step := func(method domain.MethodID) store.PlanState {
		t.Helper()
		result, err := r.Step(ctx)
		if err != nil {
			t.Fatal(err)
		}
		if result.Verdict != BuildingReasonAdmitted {
			t.Fatal("bunk rung not admitted", method, result)
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
	plans = append(plans, spots)
	// The spots stand: the bed rung deletes them first, then places the
	// beds on the freed slots.
	settleBunks(t, r, db, n)
	for _, f := range afterSettle {
		f()
	}
	standBunks(n, standingBunks{"SleepingSpot", bunkAnchors(t, spots, "SleepingSpot")})
	clearing := step(shelterClearBedsMethod)
	for _, action := range clearing.Spec.Actions() {
		if cut, ok := action.Deconstruction(); !ok || cut.Definition() != "SleepingSpot" {
			t.Fatal("clearing is not a spot deletion", action)
		}
	}
	standBunks(n)
	beds := step(shelterBedsMethod)
	plans = append(plans, beds)

	// The ring tests that follow read no sleeping census, as before.
	n.reply.GetObserved().Upkeep, n.standing = upkeep, nil
	n.previews, n.calls = 0, 0
	return plans
}

// standingBunks is a definition standing at the anchors of some bunk slots.
type standingBunks struct {
	definition string
	at         []shelterBunk
}

// standBunks makes the fixture's sleeping census show the standing pieces
// (none when called bare).
func standBunks(n *sleepingNative, sets ...standingBunks) {
	var beds []*o.UpkeepBed
	n.standing = map[domain.Cell]bool{}
	for _, set := range sets {
		for _, b := range set.at {
			for _, cell := range policy.BunkCells(b.anchor, b.rot) {
				n.standing[cell] = true
			}
			ref := &o.EntityRef{Id: proto.String(fmt.Sprintf("%s-bunk%d", set.definition, len(beds))), DefName: proto.String(set.definition), MapId: proto.Int32(0), Position: &c.Cell{X: proto.Int32(b.anchor.X), Z: proto.Int32(b.anchor.Z)}}
			beds = append(beds, &o.UpkeepBed{Bed: n.head(ref), Medical: proto.Bool(false), Prisoners: proto.Bool(false), Roofed: proto.Bool(false), TemperatureC: proto.Float64(20)})
		}
	}
	n.reply.GetObserved().Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{Beds: beds,
		Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}}}}
}

// settleBunks shows the journal's applied bunks standing built in the
// census and runs the review that retires their plans, as a real colony's
// next review does once the builders finish.
func settleBunks(t *testing.T, r *RoundsBuildingPlanner, db *store.Store, n *sleepingNative) {
	t.Helper()
	markBuilt(t, db, n.roundsNative)
	if _, err := r.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestShelterRoofingContinuesAfterFurnishingUntilNativeCapacityRecovers(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterFixture(t)
	ctx := context.Background()
	shell, err := r.Step(ctx)
	if err != nil || !shell.Decision.Admitted {
		t.Fatal(shell, err)
	}
	completeRoundsBuildingMethod(t, db, shell)
	markBuilt(t, db, n.roundsNative)
	if _, err := r.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	// Some of the room is now roofed and can hold spots; the native capacity
	// census still refuses to count a room with any open roof cells.
	for i, c := range n.cells.Cells {
		n.cells.Cells[i].Indoors = domain.Known(true)
		if c.Cell.X >= 2 && c.Cell.X <= 5 && c.Cell.Z >= 2 && c.Cell.Z <= 5 {
			n.cells.Cells[i] = roofed(n.cells.Cells[i])
		}
	}
	furnish, err := r.Step(ctx)
	if err != nil || !furnish.Decision.Admitted {
		t.Fatal(furnish, err)
	}
	completeRoundsBuildingMethod(t, db, furnish)
	markBuilt(t, db, n.roundsNative)
	if _, err := r.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	remaining, err := r.Step(ctx)
	if err != nil || remaining.NativeWorkTicks != 10000 {
		t.Fatal("furnishing stopped unfinished roofing", remaining.Verdict, remaining.NativeWorkTicks, err)
	}
	if _, err := r.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	remaining, err = r.Step(ctx)
	if err != nil || remaining.NativeWorkTicks != 10000 {
		t.Fatal("retirement lost roofing budget", remaining, err)
	}
	n.reply.GetObserved().IndoorSleepingCapacity = n.reply.GetObserved().ColonistCount
	n.reply.GetObserved().BedCapacity = n.reply.GetObserved().ColonistCount
	if _, err := r.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	remaining, err = r.Step(ctx)
	if err != nil || remaining.Verdict != BuildingReasonNoDeficit || remaining.NativeWorkTicks != 0 {
		t.Fatal(remaining, err)
	}
}

// hutCells replaces the fixture's 9x9 census with a square of the given
// side, optionally limiting which cells support light, and stocks enough
// wood for a shell larger than the 9x9 rectangle.
func hutCells(n *sleepingNative, side int32, lit func(x, z int32) bool) {
	preview := n.onPreview
	n.onPreview = func(ctx context.Context, v *bridge.BuildingPreview) {
		preview(ctx, v)
		if len(v.Stock.Values) > 0 {
			v.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(600))}}
		}
	}
	n.cells.Region = policy.Rectangle{Width: side, Height: side}
	n.cells.Cells = nil
	for x := int32(0); x < side; x++ {
		for z := int32(0); z < side; z++ {
			cell := openCell(x, z)
			cell.Terrain = domain.Known("Soil")
			if !lit(x, z) {
				cell.Terrain = domain.Known("WaterDeep")
			}
			n.cells.Cells = append(n.cells.Cells, cell)
		}
	}
}

func shellCells(t *testing.T, plan store.PlanState) (domain.Building, map[domain.Cell]bool) {
	t.Helper()
	cells := map[domain.Cell]bool{}
	actions := plan.Spec.Actions()
	door, _ := actions[0].Building()
	for i, action := range actions {
		b, ok := action.Building()
		// The door takes its stuff from the door definition's own stats.
		if !ok || b.Stuff() != "WoodLog" && b.Definition() != "Door" || (i == 0) != (b.Definition() == "Door") || cells[b.Cell()] {
			t.Fatal(i, ok, b.Definition(), b.Stuff(), b.Cell(), cells[b.Cell()])
		}
		cells[b.Cell()] = true
	}
	if len(plan.Spec.Dependencies()) != 0 {
		t.Fatal("shell walls gated on the door", plan.Spec.Dependencies())
	}
	return door, cells
}

func TestRoundsShelterRaisesTheStarterRectangle(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r, db, n := shelterSiteFixture(t)
	n.reply.GetObserved().Center = &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}
	centreOn(r.reviewer, domain.Cell{X: 10, Z: 10})
	hutCells(n, 21, func(int32, int32) bool { return true })
	recordStoreroom(t, r, db, policy.Rectangle{X: 7, Z: 7, Width: 7, Height: 7})
	stageShelterBunks(t, r, db, n)
	result, err := r.Step(context.Background())
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), shellMethod(result.Decision.Standard).Plan)
	if err != nil {
		t.Fatal(err)
	}
	want, err := domain.RectangleFootprint(domain.RoomBounds{X: 6, Z: 6, Width: 9, Height: 9}, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	door, cells := shellCells(t, plan)
	if door.Cell() != want.Door() || len(cells) != len(want.Walls()) || n.previews != len(want.Walls()) {
		t.Fatal(door, len(cells), n.previews)
	}
	for _, w := range want.Walls() {
		if !cells[w] {
			t.Fatal("missing shell wall", w)
		}
	}
	if len(plan.Progress) != len(cells) || len(plan.Spec.Dependencies()) != 0 {
		t.Fatal(len(plan.Progress), len(plan.Spec.Dependencies()))
	}
	// Roofing budget accepts a shell of any size once every wall is complete.
	current := result.Decision.Standard.Standard.Snapshot
	snapshot := current
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	for i, p := range plan.Progress {
		for _, step := range []func() (domain.Progress, error){
			func() (domain.Progress, error) { return p.Prepare(snapshot, 100) },
			func() (domain.Progress, error) { return p.MarkDispatched(snapshot, 100) },
			func() (domain.Progress, error) { return p.RecordReceipt(1, domain.ReceiptAccepted) },
		} {
			if p, err = step(); err != nil {
				t.Fatal(err)
			}
		}
		plan.Progress[i] = p
	}
	if got := shelterNativeWorkTicks(plan, current, 100); got != 10000 {
		t.Fatal("shell completion granted no roofing budget", got)
	}
	if again, err := r.Step(context.Background()); err != nil || again.Verdict != BuildingReasonExistingWork {
		t.Fatal(again, err)
	}
}

// Previews of one bundle are sequential native reads: a resource whose
// Available moved between them funds the bundle at the lowest value seen.
func TestMergeRoundsStockTakesTheLowestAvailable(t *testing.T) {
	row := func(n int64) policy.StockObservation {
		return policy.StockObservation{Values: []policy.Stock{{Resource: "WoodLog", Available: domain.Known(n)}}}
	}
	var stock policy.StockObservation
	for i, n := range []int64{120, 90, 140} {
		if err := mergeRoundsStock(&stock, row(n), i == 0); err != nil {
			t.Fatal(err)
		}
	}
	if got, _ := stock.Values[0].Available.Value(); got != 90 || len(stock.Values) != 1 {
		t.Fatal(stock.Values)
	}
	unknown := policy.StockObservation{Values: []policy.Stock{{Resource: "WoodLog", Available: domain.Unknown[int64]()}}}
	if err := mergeRoundsStock(&stock, unknown, false); err != nil {
		t.Fatal(err)
	}
	if _, known := stock.Values[0].Available.Value(); known {
		t.Fatal("an unknown preview must leave the resource unknown")
	}
}
