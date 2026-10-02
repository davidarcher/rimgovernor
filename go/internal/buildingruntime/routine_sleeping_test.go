package buildingruntime

import (
	"context"
	"fmt"
	"slices"
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

type sleepingNative struct {
	*routineNative
	// previews counts placements evaluated; calls counts native hops, so a
	// batched sweep shows as many previews as cells and one call (#599).
	previews  int
	calls     int
	onPreview func(context.Context, *bridge.BuildingPreview)
}

func (n *sleepingNative) PreviewBuildings(ctx context.Context, actions []domain.Action, s domain.GenerationSnapshot) ([]bridge.BuildingPreview, bridge.Result, error) {
	n.calls++
	out := make([]bridge.BuildingPreview, 0, len(actions))
	for _, a := range actions {
		v, _, err := n.previewOne(ctx, a, s)
		if err != nil {
			return nil, bridge.Result{}, err
		}
		out = append(out, v)
	}
	return out, bridge.Result{}, nil
}

func (n *sleepingNative) PreviewBuilding(ctx context.Context, a domain.Action, s domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	n.calls++
	return n.previewOne(ctx, a, s)
}

func (n *sleepingNative) previewOne(ctx context.Context, a domain.Action, s domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	n.previews++
	b, _ := a.Building()
	anchor := b.Cell()
	tick := domain.Tick(n.reply.GetObserved().Context.GetTick())
	v := bridge.BuildingPreview{Preview: policy.Preview{Action: a, Snapshot: s, Tick: tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), Costs: domain.Known([]policy.Amount{}), Footprint: domain.Known([]domain.Cell{anchor, {X: anchor.X, Z: anchor.Z + 1}})}, Stock: policy.StockObservation{Snapshot: s, Tick: tick}}
	if n.onPreview != nil {
		n.onPreview(ctx, &v)
	}
	return v, bridge.Result{}, nil
}

func sleepingFixture(t *testing.T) (*RoutineBuildingPlanner, *store.Store, *playerFakeSession, store.ControlRequest, *sleepingNative) {
	t.Helper()
	r, db, session, request, n := routineFixture(t)
	sleepingFacts(n)
	if _, err := r.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	source := &sleepingNative{routineNative: n}
	planner, err := NewRoutineSleepingPlanner(r, source)
	if err != nil {
		t.Fatal(err)
	}
	return planner, db, session, request, source
}

// sleepingFacts seeds the colony fixture with a 5x5 roofed indoor site and
// the SleepingSpot definition, the precondition for the sleeping planner's
// first rung.
func sleepingFacts(n *routineNative) {
	v := n.reply.GetObserved()
	v.Center = &c.Cell{X: proto.Int32(2), Z: proto.Int32(2)}
	n.catalog = []*o.PlanningDefinition{{Definition: &o.DefinitionRef{DefName: proto.String("SleepingSpot")}, ConstructionSkill: proto.Int32(0), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(2)}}}
	cells := n.cells
	cells.Region.Maximum = &c.Cell{X: proto.Int32(4), Z: proto.Int32(4)}
	cells.Cells = nil
	for x := int32(0); x < 5; x++ {
		for z := int32(0); z < 5; z++ {
			cells.Cells = append(cells.Cells, &o.CellState{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Roof: proto.String("RoofConstructed"), Indoors: proto.Bool(true), Fogged: proto.Bool(false), Walkable: proto.Bool(true), Occupied: proto.Bool(false), SupportsLight: proto.Bool(true), Issues: []*o.ReadIssue{{Field: proto.String("zone_id"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}})
		}
	}
}

func TestRoutineSleepingAdmitsWholePendingMethodAndManualInvalidates(t *testing.T) {
	t.Parallel()
	r, db, session, request, n := sleepingFixture(t)
	before := session.acquires.Load()
	result, err := r.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted || !result.Decision.Admitted {
		t.Fatal(result, err)
	}
	g := result.Decision.Goal
	if len(g.Methods) != 1 || n.previews != 2 || session.acquires.Load() != before {
		t.Fatal(g, n.previews)
	}
	p, err := db.LoadPlan(context.Background(), g.Methods[0].Plan)
	if err != nil || len(p.Progress) != 2 {
		t.Fatal(p, err)
	}
	for _, progress := range p.Progress {
		if progress.View().Stage != domain.Pending || progress.View().Attempt != 0 {
			t.Fatal("compiler dispatched", progress)
		}
	}
	if next, err := r.Step(context.Background()); err != nil || next.Reason != BuildingMethodExistingWork || n.previews != 2 {
		t.Fatal(next, err)
	}
	request.Kind, request.RequestID = store.PauseControl, "manual-sleep"
	if _, err = r.reviewer.player.Pause(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	p, err = db.LoadPlan(context.Background(), p.Spec.ID())
	if err != nil {
		t.Fatal(err)
	}
	for _, progress := range p.Progress {
		if progress.View().Stage != domain.Pending {
			t.Fatal(progress)
		}
	}
	if next, err := r.Step(context.Background()); err != nil || next.Reason != BuildingMethodDisabled {
		t.Fatal(next, err)
	}
}

func TestRoutineSleepingRejectsIncompleteAndChangedEvidence(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"space", "unsafe", "direction", "prerequisite", "unknown-room"} {
		t.Run(change, func(t *testing.T) {
			r, db, session, _, n := sleepingFixture(t)
			switch change {
			case "prerequisite":
				n.catalog[0].ConstructionSkill = nil
			case "unknown-room":
				for _, cell := range n.cells.Cells {
					cell.Indoors = nil
				}
			default:
				n.onPreview = func(_ context.Context, v *bridge.BuildingPreview) {
					switch change {
					case "space":
						if n.previews > 1 {
							v.Preview.SafeToPlace = domain.Known(false)
						}
					case "unsafe":
						v.Preview.SafeToPlace = domain.Unknown[bool]()
					case "direction":
						session.mu.Lock()
						session.state.Snapshot.Native++
						session.mu.Unlock()
					}
				}
			}
			result, err := r.Step(context.Background())
			if err == nil && result.Reason == BuildingMethodAdmitted {
				t.Fatal("invalid method admitted", change)
			}
			plans, err := db.LoadPlans(context.Background(), 256)
			if err != nil || len(plans) != 2 {
				t.Fatal("partial method committed", plans, err)
			}
		})
	}
}

// Spots placed and then lost (converted to medical beds, which native
// indoor sleeping capacity excludes, or cancelled) leave the same owed
// count under the same epoch: the spent method yields to a numbered
// successor instead of holding the shelter gate at method_already_used.
func TestRoutineSleepingReproposesSpotsAfterSpentMethod(t *testing.T) {
	t.Parallel()
	r, db, _, _, n := sleepingFixture(t)
	first, err := r.Step(context.Background())
	if err != nil || first.Reason != BuildingMethodAdmitted {
		t.Fatal(first, err)
	}
	g := first.Decision.Goal
	p, err := db.LoadPlan(context.Background(), g.Methods[0].Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, a := range p.Spec.Actions() {
		if _, err = db.Cancel(context.Background(), p.Spec.ID(), a.ID()); err != nil {
			t.Fatal(err)
		}
	}
	// No census shows the spots gone: they may stand uncounted (an open
	// roof), so the method stays used.
	if held, err := r.Step(context.Background()); err != nil || held.Reason != BuildingMethodUsed {
		t.Fatal(held, err)
	}
	// Three colonists, three spots, two of them converted to medical beds.
	var beds []*o.UpkeepBed
	for i, medical := range []bool{false, true, true} {
		ref := &o.EntityRef{Id: proto.String(fmt.Sprintf("spot%d", i)), DefName: proto.String("SleepingSpot"), MapId: proto.Int32(0), Position: &c.Cell{X: proto.Int32(int32(i)), Z: proto.Int32(0)}}
		beds = append(beds, &o.UpkeepBed{Bed: ref, Slots: proto.Uint32(1), Humanlike: proto.Bool(true), Medical: proto.Bool(medical), Prisoners: proto.Bool(false), Roofed: proto.Bool(true), TemperatureC: proto.Float64(20)})
	}
	n.reply.GetObserved().Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{Beds: beds,
		Comfort: &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}}}}
	if _, err = r.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	next, err := r.Step(context.Background())
	if err != nil || next.Reason != BuildingMethodAdmitted || next.Decision.Goal.Goal.Epoch != g.Goal.Epoch {
		t.Fatal(next, err)
	}
	methods := next.Decision.Goal.Methods
	if last := methods[len(methods)-1]; last.Method != g.Methods[0].Method+"-1" || last.Plan == p.Spec.ID() {
		t.Fatal(methods)
	}
	// The successor still open is the method in use.
	if again, err := r.Step(context.Background()); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
}

// A building intent admitted on another plan but not yet applied holds its
// anchor from siting (#943): nothing on the map shows it yet.
func TestRoutineSleepingProtectsOtherAdmittedFootprints(t *testing.T) {
	t.Parallel()
	r, db, session, _, _ := sleepingFixture(t)
	ctx := context.Background()
	snapshot := session.State().Snapshot
	g, err := domain.NewGoal("player-room", domain.PlayerGoal, 3, snapshot, 7)
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreateGoal(ctx, g); err != nil {
		t.Fatal(err)
	}
	goal, err := db.ReviewGoal(ctx, g.ID, 0, snapshot, 7, domain.NeedDeficit)
	if err != nil {
		t.Fatal(err)
	}
	protected := domain.Cell{X: 2, Z: 2}
	b, err := domain.NewBuilding("SleepingSpot", protected, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("player-room-a", b)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("player-room-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	snapshot.Plan = p.ID()
	snapshot.Revision = 1
	admitted, err := db.AdmitBuildingMethod(ctx, store.BuildingMethodRequest{Goal: g.ID, Revision: goal.Revision, Method: "player-sleep", Plan: p, Current: snapshot, Tick: 7, Bounds: domain.Known(policy.Bounds{Width: 100, Height: 100}), Stock: policy.StockObservation{Snapshot: snapshot, Tick: 7}, Purpose: policy.Routine})
	if err != nil || !admitted.Admitted {
		t.Fatal(admitted, err)
	}
	anchors, err := db.PendingBuildingAnchors(ctx, snapshot, nil)
	if err != nil || !slices.Contains(anchors, protected) {
		t.Fatal("pending intent holds no anchor", anchors, err)
	}
	result, err := r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	compiled, err := db.LoadPlan(ctx, result.Decision.Goal.Methods[0].Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, action := range compiled.Spec.Actions() {
		if b, ok := action.Building(); ok && b.Cell() == protected {
			t.Fatal("claimed the pending intent's anchor", b.Cell())
		}
	}
}

func TestRoutineSleepingManualCancelsBlockedPreview(t *testing.T) {
	t.Parallel()
	r, db, session, request, n := sleepingFixture(t)
	entered := make(chan struct{})
	n.onPreview = func(ctx context.Context, _ *bridge.BuildingPreview) { close(entered); <-ctx.Done() }
	finished := make(chan error, 1)
	go func() { _, err := r.Step(context.Background()); finished <- err }()
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("preview not entered")
	}
	request.Kind, request.RequestID = store.PauseControl, "manual-preview"
	if _, err := r.reviewer.player.Pause(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	if err := <-finished; err == nil {
		t.Fatal("cancelled compilation succeeded")
	}
	plans, err := db.LoadPlans(context.Background(), 256)
	if err != nil || len(plans) != 2 || session.State().Enabled {
		t.Fatal(plans, err)
	}
}

func TestRoutineSleepingKeepsDoorwayAislesClear(t *testing.T) {
	t.Parallel()
	r, db, _, _, n := sleepingFixture(t)
	// A door on the room's south wall at (2,0): the cell just inside it and
	// the cells beside it are the entrance aisle, never furniture, even when
	// the colony centre makes the aisle the nearest candidate.
	n.reply.GetObserved().Center = &c.Cell{X: proto.Int32(2), Z: proto.Int32(1)}
	for _, row := range n.cells.Cells {
		if row.Cell.GetX() == 2 && row.Cell.GetZ() == 0 {
			row.Doorway, row.Occupied, row.Walkable = proto.Bool(true), proto.Bool(true), proto.Bool(true)
		}
	}
	result, err := r.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	compiled, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
	if err != nil {
		t.Fatal(err)
	}
	aisle := map[domain.Cell]bool{{X: 2, Z: 1}: true, {X: 1, Z: 0}: true, {X: 3, Z: 0}: true, {X: 2, Z: 0}: true}
	// Building admissions are no longer recorded (#856); the placed cells are.
	for _, action := range compiled.Spec.Actions() {
		b, _ := action.Building()
		for _, cell := range []domain.Cell{b.Cell()} {
			if aisle[cell] {
				t.Fatal("furniture blocks the doorway aisle", cell)
			}
		}
	}
}
