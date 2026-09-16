package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func shelterFixture(t *testing.T) (*RoutineBuildingPlanner, *store.Store, *sleepingNative) {
	t.Helper()
	r, db, _, _, n := sleepingFixture(t)
	planning := n.reply.GetObserved().Planning.GetObserved()
	for _, name := range []string{"Wall", "Door"} {
		planning.Definitions = append(planning.Definitions, &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String(name)}, Available: proto.Bool(true), ConstructionSkill: proto.Int32(0), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(1)}})
	}
	planning.Completeness.Matched, planning.Completeness.Returned = proto.Uint64(3), proto.Uint64(3)
	planning.Cells.Region.Maximum = &c.Cell{X: proto.Int32(8), Z: proto.Int32(8)}
	planning.Cells.Completeness.Matched, planning.Cells.Completeness.Returned = proto.Uint64(81), proto.Uint64(81)
	planning.Cells.Cells = nil
	for x := int32(0); x < 9; x++ {
		for z := int32(0); z < 9; z++ {
			planning.Cells.Cells = append(planning.Cells.Cells, &o.CellState{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Indoors: proto.Bool(false), Fogged: proto.Bool(false), Walkable: proto.Bool(true), Occupied: proto.Bool(false), SupportsLight: proto.Bool(true), Issues: []*o.ReadIssue{
				{Field: proto.String("zone_id"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}},
				{Field: proto.String("roof"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}},
			}})
		}
	}
	n.onPreview = func(_ context.Context, v *bridge.BuildingPreview) {
		b, _ := v.Preview.Action.Building()
		if b.Definition() == "SleepingSpot" {
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
	planner, err := NewRoutineShelterPlanner(r.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	return planner, db, n
}

func TestRoutineShelterAdmitsWholeShellWithObservedDoorDependency(t *testing.T) {
	t.Parallel()
	r, db, n := shelterFixture(t)
	result, err := r.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted || n.previews != 32 {
		t.Fatal(result, err, n.previews)
	}
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 32 || len(plan.Admissions) != 32 || len(plan.Spec.Dependencies()) != 31 {
		t.Fatal(plan, err)
	}
	actions := plan.Spec.Actions()
	snapshot := result.Decision.Goal.Goal.Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	if err := plan.Spec.CheckDependencies(actions[0].ID(), plan.Progress, snapshot, 7); err != nil {
		t.Fatal(err)
	}
	door, _ := actions[0].Building()
	if door.Definition() != "Door" || door.Cell() != (domain.Cell{X: 4, Z: 0}) {
		t.Fatal(door)
	}
	for i, action := range actions {
		b, _ := action.Building()
		if b.Stuff() != "WoodLog" || i > 0 && b.Definition() != "Wall" || plan.Progress[i].View().Attempt != 0 {
			t.Fatal(action, plan.Progress[i])
		}
		if i > 0 {
			dep := plan.Spec.Dependencies()[i-1]
			if dep.Requires != actions[0].ID() {
				t.Fatal(dep)
			}
			if err := plan.Spec.CheckDependencies(action.ID(), plan.Progress, snapshot, 7); err == nil {
				t.Fatal("unbuilt door permitted enclosure")
			}
		}
	}
	if again, err := r.Step(context.Background()); err != nil || again.Reason != BuildingMethodExistingWork || n.previews != 32 {
		t.Fatal(again, err)
	}
}

func TestRoutineShelterNeverCommitsPartialOrUnknownShell(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"late-refusal", "footprint", "stock-short", "stock-conflict", "stock-unknown", "definition", "room-unknown", "terrain", "zone", "protected", "stale", "direction", "reserve"} {
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
				case "footprint":
					v.Preview.Footprint = domain.Known([]domain.Cell{{X: 1, Z: 1}})
				case "stock-short":
					v.Stock.Values[0].Available = domain.Known(int64(179))
				case "stock-conflict":
					if n.previews == 32 {
						v.Stock.Values[0].Available = domain.Known(int64(179))
					}
				case "stock-unknown":
					v.Stock.Values[0].Available = domain.Unknown[int64]()
				case "stale":
					v.Preview.Tick++
				case "direction":
					session := r.reviewer.player.session.(*playerFakeSession)
					session.mu.Lock()
					session.state.Snapshot.Native++
					session.mu.Unlock()
				}
			}
			planning := n.reply.GetObserved().Planning.GetObserved()
			switch change {
			case "definition":
				planning.Definitions[2].Size.Width = proto.Uint32(2)
			case "room-unknown":
				planning.Cells.Cells[40].Indoors = nil
			case "terrain":
				planning.Cells.Cells[40].SupportsLight = nil
			case "zone":
				planning.Cells.Cells[40].ZoneId = proto.String("player-zone")
				planning.Cells.Cells[40].Issues = planning.Cells.Cells[40].Issues[1:]
			case "protected":
				planning.Cells.Cells[40].Occupied = proto.Bool(true)
			case "reserve":
				r.reviewer.rules = []policy.ResourceRule{{Resource: "WoodLog", Reserve: 1, Spending: policy.Allow}}
			}
			result, err := r.Step(context.Background())
			if err == nil && result.Reason == BuildingMethodAdmitted {
				t.Fatal("invalid shell admitted", change)
			}
			plans, err := db.LoadPlans(context.Background(), 256)
			if err != nil || len(plans) != 2 {
				t.Fatal("partial shell committed", plans, err)
			}
		})
	}
}

func TestRoutineShelterPrefersExistingRoom(t *testing.T) {
	t.Parallel()
	r, db, _, _, n := sleepingFixture(t)
	planner, err := NewRoutineShelterPlanner(r.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
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

func TestShelterRoofingBudgetRequiresObservedCompletionAndDoesNotRenew(t *testing.T) {
	t.Parallel()
	r, db, _ := shelterFixture(t)
	result, err := r.Step(context.Background())
	if err != nil || !result.Decision.Admitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
	if err != nil {
		t.Fatal(err)
	}
	current := result.Decision.Goal.Goal.Snapshot
	snapshot := current
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	if shelterNativeWorkTicks(plan, current, 7) != 0 {
		t.Fatal("pending shell granted roofing time")
	}
	for i, p := range plan.Progress {
		p, err = p.Prepare(snapshot, 7)
		if err != nil {
			t.Fatal(err)
		}
		p, err = p.MarkDispatched(snapshot, 7)
		if err != nil {
			t.Fatal(err)
		}
		p, err = p.RecordReceipt(1, domain.ReceiptAccepted)
		if err != nil {
			t.Fatal(err)
		}
		plan.Progress[i] = p
	}
	if shelterNativeWorkTicks(plan, current, 7) != 0 {
		t.Fatal("receipts granted roofing time")
	}
	for i, p := range plan.Progress {
		p, err = p.Observe(domain.Observation{Action: p.Action().ID(), Attempt: 1, Snapshot: snapshot, Tick: 100, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot)
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
	current.Native++
	if shelterNativeWorkTicks(plan, current, 100) != 0 {
		t.Fatal("old direction renewed roofing budget")
	}
}

func TestRoutineShelterManualCancelsWholePendingShell(t *testing.T) {
	t.Parallel()
	r, db, n := shelterFixture(t)
	// This case journals cancellation of 32 dependent actions under race detection.
	r.reviewer.player.config.CallTimeout = 5 * time.Second
	r.reviewer.player.config.JournalTimeout = 5 * time.Second
	ctx := context.Background()
	result, err := r.Step(ctx)
	if err != nil || !result.Decision.Admitted {
		t.Fatal(result, err)
	}
	request := store.ControlRequest{RequestID: "manual-shell", Kind: store.PauseControl, World: playerWorld(result.Decision.Goal.Goal.Snapshot)}
	if _, err := r.reviewer.player.Pause(ctx, request); err != nil {
		t.Fatal(err)
	}
	plan, err := db.LoadPlan(ctx, result.Decision.Goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 32 {
		t.Fatal(plan, err)
	}
	for _, p := range plan.Progress {
		if p.View().Stage != domain.Cancelled || p.View().Attempt != 0 {
			t.Fatal(p)
		}
	}
	reads := n.reads
	again, err := r.Step(ctx)
	if err != nil || again.Reason != BuildingMethodDisabled || again.NativeWorkTicks != 0 || n.reads != reads {
		t.Fatal(again, err, n.reads)
	}
}

func completeRoutineBuildingMethod(t *testing.T, db *store.Store, result RoutineBuildingResult) {
	t.Helper()
	ctx := context.Background()
	var plan store.PlanState
	for _, method := range result.Decision.Goal.Methods {
		candidate, err := db.LoadPlan(ctx, method.Plan)
		if err != nil {
			t.Fatal(err)
		}
		if domain.GoalWorkOpen(candidate.Progress) {
			if len(plan.Progress) != 0 {
				t.Fatal("multiple open methods")
			}
			plan = candidate
		}
	}
	if len(plan.Progress) == 0 {
		t.Fatal("no pending method")
	}
	snapshot := result.Decision.Goal.Goal.Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	for i, action := range plan.Spec.Actions() {
		admission := plan.Admissions[i]
		if admission.Action != action.ID() {
			t.Fatal(admission)
		}
		if _, err := db.ReserveAndPrepare(ctx, plan.Spec.ID(), action.ID(), admission.Admission); err != nil {
			t.Fatal(err)
		}
		if _, err := db.Dispatch(ctx, plan.Spec.ID(), action.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err := db.Observe(ctx, plan.Spec.ID(), domain.Observation{Action: action.ID(), Attempt: 1, Snapshot: snapshot, Tick: 7, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot); err != nil {
			t.Fatal(err)
		}
	}
}

func TestShelterRoofingContinuesAfterFurnishingUntilNativeCapacityRecovers(t *testing.T) {
	t.Parallel()
	r, db, n := shelterFixture(t)
	r.reviewer.player.config.CallTimeout = 5 * time.Second
	ctx := context.Background()
	shell, err := r.Step(ctx)
	if err != nil || !shell.Decision.Admitted {
		t.Fatal(shell, err)
	}
	completeRoutineBuildingMethod(t, db, shell)
	// Some of the room is now roofed and can hold spots; the native capacity
	// census still refuses to count a room with any open roof cells.
	for _, c := range n.reply.GetObserved().Planning.GetObserved().Cells.Cells {
		c.Indoors = proto.Bool(true)
		if c.Cell.GetX() >= 2 && c.Cell.GetX() <= 5 && c.Cell.GetZ() >= 2 && c.Cell.GetZ() <= 5 {
			c.Roof = proto.String("RoofConstructed")
			c.Issues = c.Issues[:1]
		}
	}
	furnish, err := r.Step(ctx)
	if err != nil || !furnish.Decision.Admitted {
		t.Fatal(furnish, err)
	}
	completeRoutineBuildingMethod(t, db, furnish)
	remaining, err := r.Step(ctx)
	if err != nil || remaining.NativeWorkTicks != 10000 {
		t.Fatal("furnishing stopped unfinished roofing", remaining, err)
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
	if err != nil || remaining.Reason != BuildingMethodNoDeficit || remaining.NativeWorkTicks != 0 {
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
	planning := n.reply.GetObserved().Planning.GetObserved()
	planning.Cells.Region.Maximum = &c.Cell{X: proto.Int32(side - 1), Z: proto.Int32(side - 1)}
	planning.Cells.Completeness.Matched, planning.Cells.Completeness.Returned = proto.Uint64(uint64(side*side)), proto.Uint64(uint64(side*side))
	planning.Cells.Cells = nil
	for x := int32(0); x < side; x++ {
		for z := int32(0); z < side; z++ {
			planning.Cells.Cells = append(planning.Cells.Cells, &o.CellState{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Indoors: proto.Bool(false), Fogged: proto.Bool(false), Walkable: proto.Bool(true), Occupied: proto.Bool(false), SupportsLight: proto.Bool(lit(x, z)), Issues: []*o.ReadIssue{
				{Field: proto.String("zone_id"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}},
				{Field: proto.String("roof"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}},
			}})
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
		if !ok || b.Stuff() != "WoodLog" || (i == 0) != (b.Definition() == "Door") || cells[b.Cell()] {
			t.Fatal(action)
		}
		cells[b.Cell()] = true
		if i > 0 && plan.Spec.Dependencies()[i-1].Requires != actions[0].ID() {
			t.Fatal("wall does not depend on the door", action)
		}
	}
	return door, cells
}

func TestRoutineShelterRaisesOvalHutForNeolithicColony(t *testing.T) {
	t.Parallel()
	r, db, n := shelterFixture(t)
	n.reply.GetObserved().PlayerTechLevel = proto.String("Neolithic")
	n.reply.GetObserved().Center = &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}
	hutCells(n, 21, func(int32, int32) bool { return true })
	result, err := r.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
	if err != nil {
		t.Fatal(err)
	}
	want, err := domain.EllipseFootprint(domain.Cell{X: 10, Z: 10}, 4, 4, domain.EllipseNorthSouth, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	door, cells := shellCells(t, plan)
	if door.Cell() != want.Door() || door.Rotation() != domain.South || len(cells) != len(want.Walls()) || n.previews != len(want.Walls()) {
		t.Fatal(door, len(cells), n.previews)
	}
	for _, w := range want.Walls() {
		if !cells[w] {
			t.Fatal("missing hut wall", w)
		}
	}
	if len(plan.Progress) != len(cells) || len(plan.Spec.Dependencies()) != len(cells)-1 {
		t.Fatal(len(plan.Progress), len(plan.Spec.Dependencies()))
	}
	// Roofing budget accepts a shell of any size once every wall is complete.
	current := result.Decision.Goal.Goal.Snapshot
	snapshot := current
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	for i, p := range plan.Progress {
		for _, step := range []func() (domain.Progress, error){
			func() (domain.Progress, error) { return p.Prepare(snapshot, 7) },
			func() (domain.Progress, error) { return p.MarkDispatched(snapshot, 7) },
			func() (domain.Progress, error) { return p.RecordReceipt(1, domain.ReceiptAccepted) },
			func() (domain.Progress, error) {
				return p.Observe(domain.Observation{Action: p.Action().ID(), Attempt: 1, Snapshot: snapshot, Tick: 100, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot)
			},
		} {
			if p, err = step(); err != nil {
				t.Fatal(err)
			}
		}
		plan.Progress[i] = p
	}
	if got := shelterNativeWorkTicks(plan, current, 100); got != 10000 {
		t.Fatal("hut completion granted no roofing budget", got)
	}
	if again, err := r.Step(context.Background()); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
}

func TestRoutineShelterKeepsRectangleWithoutNeolithicTechLevel(t *testing.T) {
	t.Parallel()
	for _, level := range []*string{nil, proto.String("Industrial")} {
		r, db, n := shelterFixture(t)
		n.reply.GetObserved().PlayerTechLevel = level
		n.reply.GetObserved().Center = &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}
		hutCells(n, 21, func(int32, int32) bool { return true })
		result, err := r.Step(context.Background())
		if err != nil || result.Reason != BuildingMethodAdmitted || n.previews != 32 {
			t.Fatal(result, err, n.previews)
		}
		plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
		if err != nil {
			t.Fatal(err)
		}
		door, cells := shellCells(t, plan)
		if len(cells) != 32 || door.Cell() != (domain.Cell{X: 10, Z: 6}) {
			t.Fatal(door, len(cells))
		}
	}
}

func TestRoutineShelterGrowsIrregularShellOverConstrainedTerrain(t *testing.T) {
	t.Parallel()
	r, db, n := shelterFixture(t)
	n.reply.GetObserved().PlayerTechLevel = proto.String("Neolithic")
	n.reply.GetObserved().Center = &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}
	// An L-shaped lit strip five cells wide: no oval or rectangle template
	// fits, so a concave connected footprint is grown and admitted whole.
	lit := func(x, z int32) bool { return x >= 8 && x <= 12 && z >= 1 || z >= 8 && z <= 12 && x >= 8 }
	hutCells(n, 21, lit)
	result, err := r.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
	if err != nil {
		t.Fatal(err)
	}
	_, cells := shellCells(t, plan)
	if len(cells) < 20 || n.previews != len(cells) {
		t.Fatal(len(cells), n.previews)
	}
	minX, maxX, minZ, maxZ := int32(99), int32(0), int32(99), int32(0)
	for cell := range cells {
		if !lit(cell.X, cell.Z) {
			t.Fatal("wall placed on unlit ground", cell)
		}
		minX, maxX, minZ, maxZ = min(minX, cell.X), max(maxX, cell.X), min(minZ, cell.Z), max(maxZ, cell.Z)
	}
	// The room bends around the corner of the L: its bounding box spans both
	// arms yet contains unlit ground no wall was placed on.
	concave := false
	for x := minX; x <= maxX; x++ {
		for z := minZ; z <= maxZ; z++ {
			concave = concave || !lit(x, z)
		}
	}
	if maxX-minX+1 <= 5 || maxZ-minZ+1 <= 5 || !concave {
		t.Fatal("grown shell is not the concave corner room", minX, maxX, minZ, maxZ)
	}
}

// adoptingNative adds the wall-and-door census a shell planner uses to
// recognise a shell it began earlier.
type adoptingNative struct {
	*sleepingNative
	standing []bridge.Structure
	censuses int
	last     domain.GenerationSnapshot
}

func (n *adoptingNative) ReadStructures(_ context.Context, _ *c.Identity, minimum, maximum domain.Cell, definitions []string) (bridge.StructureRead, bridge.Result, error) {
	n.censuses++
	out := bridge.StructureRead{Tick: domain.Tick(n.reply.GetObserved().Context.GetTick()), Generation: uint64(n.last.Native)}
	for _, s := range n.standing {
		if s.Cell.X >= minimum.X && s.Cell.X <= maximum.X && s.Cell.Z >= minimum.Z && s.Cell.Z <= maximum.Z {
			out.Structures = append(out.Structures, s)
		}
	}
	return out, bridge.Result{}, nil
}

func (n *adoptingNative) PreviewBuilding(ctx context.Context, a domain.Action, s domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	n.last = s
	return n.sleepingNative.PreviewBuilding(ctx, a, s)
}

func TestRoutineShelterReissuesOnlyTheMissingCellsOfAnEarlierShell(t *testing.T) {
	t.Parallel()
	r, db, base := shelterFixture(t)
	base.reply.GetObserved().PlayerTechLevel = proto.String("Neolithic")
	base.reply.GetObserved().Center = &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}
	hutCells(base, 21, func(int32, int32) bool { return true })
	want, err := domain.EllipseFootprint(domain.Cell{X: 10, Z: 10}, 4, 4, domain.EllipseNorthSouth, domain.South)
	if err != nil {
		t.Fatal(err)
	}
	// A restart left the door built, two walls framed and one blueprinted;
	// the rest of the ring was never ordered.
	n := &adoptingNative{sleepingNative: base}
	walls := want.Walls()
	standing := map[domain.Cell]bool{want.Door(): true}
	n.standing = []bridge.Structure{{ID: "door", Definition: "Door", Cell: want.Door(), Status: "built"}}
	for i, status := range []string{"frame", "frame", "blueprint"} {
		w := walls[len(walls)-1-i]
		if w == want.Door() {
			t.Fatal("fixture picked the door")
		}
		standing[w] = true
		n.standing = append(n.standing, bridge.Structure{ID: status, Definition: "Wall", Cell: w, Status: status})
	}
	// The census must not be able to place on standing cells; the planner
	// must skip them without asking.
	preview := base.onPreview
	base.onPreview = func(ctx context.Context, v *bridge.BuildingPreview) {
		preview(ctx, v)
		if b, _ := v.Preview.Action.Building(); standing[b.Cell()] {
			t.Errorf("previewed a standing cell %v", b.Cell())
		}
	}
	// The planner reads the census through the same source it previews with.
	planner, err := NewRoutineShelterPlanner(r.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	// Prime the generation the fake census echoes.
	n.last = r.reviewer.player.session.State().Snapshot
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	if n.censuses == 0 {
		t.Fatal("no structure census")
	}
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
	if err != nil {
		t.Fatal(err)
	}
	got := map[domain.Cell]bool{}
	for _, action := range plan.Spec.Actions() {
		b, ok := action.Building()
		if !ok || b.Definition() != "Wall" || b.Stuff() != "WoodLog" || standing[b.Cell()] || got[b.Cell()] {
			t.Fatal("unexpected reissued action", action)
		}
		got[b.Cell()] = true
	}
	if len(got) != len(walls)-len(standing) {
		t.Fatalf("reissued %d cells, want %d", len(got), len(walls)-len(standing))
	}
	for _, w := range walls {
		if !standing[w] && !got[w] {
			t.Fatal("missing cell not reissued", w)
		}
	}
	if len(plan.Spec.Dependencies()) != 0 {
		t.Fatal("walls of an adopted shell must not wait for a door that already stands")
	}
	if base.previews != len(got) {
		t.Fatalf("previews %d, want one per missing cell %d", base.previews, len(got))
	}
}

func TestRoutineShelterIgnoresALoneDoorAndSitesAfresh(t *testing.T) {
	t.Parallel()
	r, db, base := shelterFixture(t)
	base.reply.GetObserved().PlayerTechLevel = proto.String("Neolithic")
	base.reply.GetObserved().Center = &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}
	hutCells(base, 21, func(int32, int32) bool { return true })
	n := &adoptingNative{sleepingNative: base, standing: []bridge.Structure{{ID: "door", Definition: "Door", Cell: domain.Cell{X: 4, Z: 3}, Status: "built"}}}
	planner, err := NewRoutineShelterPlanner(r.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	n.last = r.reviewer.player.session.State().Snapshot
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
	if err != nil {
		t.Fatal(err)
	}
	door, cells := shellCells(t, plan)
	want, _ := domain.EllipseFootprint(domain.Cell{X: 10, Z: 10}, 4, 4, domain.EllipseNorthSouth, domain.South)
	if door.Cell() != want.Door() || len(cells) != len(want.Walls()) {
		t.Fatal("a lone door is not a shell in progress", door, len(cells))
	}
}
