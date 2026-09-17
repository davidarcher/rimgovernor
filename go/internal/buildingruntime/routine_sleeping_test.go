package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type sleepingNative struct {
	*routineNative
	previews  int
	onPreview func(context.Context, *bridge.BuildingPreview)
}

func (n *sleepingNative) PreviewBuilding(ctx context.Context, a domain.Action, s domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
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
	v := n.reply.GetObserved()
	v.Center = &c.Cell{X: proto.Int32(2), Z: proto.Int32(2)}
	planning := v.Planning.GetObserved()
	planning.Definitions = []*o.PlanningDefinition{{Definition: &o.DefinitionRef{DefName: proto.String("SleepingSpot")}, Available: proto.Bool(true), ConstructionSkill: proto.Int32(0), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(2)}}}
	cells := planning.Cells
	cells.Region.Maximum = &c.Cell{X: proto.Int32(4), Z: proto.Int32(4)}
	cells.Completeness.Matched = proto.Uint64(25)
	cells.Completeness.Returned = proto.Uint64(25)
	cells.Cells = nil
	for x := int32(0); x < 5; x++ {
		for z := int32(0); z < 5; z++ {
			cells.Cells = append(cells.Cells, &o.CellState{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Roof: proto.String("RoofConstructed"), Indoors: proto.Bool(true), Fogged: proto.Bool(false), Walkable: proto.Bool(true), Occupied: proto.Bool(false), SupportsLight: proto.Bool(true), Issues: []*o.ReadIssue{{Field: proto.String("zone_id"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}})
		}
	}
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
	if err != nil || len(p.Progress) != 2 || len(p.Admissions) != 2 {
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
	for _, change := range []string{"space", "unsafe", "stock", "direction", "tick", "age", "prerequisite", "unknown-room"} {
		t.Run(change, func(t *testing.T) {
			r, db, session, _, n := sleepingFixture(t)
			switch change {
			case "prerequisite":
				n.reply.GetObserved().Planning.GetObserved().Definitions[0].ConstructionSkill = nil
			case "unknown-room":
				for _, cell := range n.reply.GetObserved().Planning.GetObserved().Cells.Cells {
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
					case "stock":
						v.Preview.Costs = domain.Known([]policy.Amount{{Resource: "WoodLog", Count: 50}})
						v.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(50))}}
					case "direction":
						session.mu.Lock()
						session.state.Snapshot.Native++
						session.mu.Unlock()
					case "tick":
						n.reply.GetObserved().Context.Tick = proto.Int64(8)
					case "age":
						r.reviewer.clock.(*testkit.ManualClock).Advance(time.Second)
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

func TestRoutineSleepingRetainsMethodIdentityUntilObservedRecovery(t *testing.T) {
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
	if next, err := r.Step(context.Background()); err != nil || next.Reason != BuildingMethodUsed {
		t.Fatal(next, err)
	}
	n.reply.GetObserved().IndoorSleepingCapacity = proto.Uint32(3)
	n.reply.GetObserved().BedCapacity = proto.Uint32(3)
	if _, err = r.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	n.reply.GetObserved().IndoorSleepingCapacity = proto.Uint32(1)
	n.reply.GetObserved().BedCapacity = proto.Uint32(2)
	if _, err = r.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	next, err := r.Step(context.Background())
	if err != nil || next.Reason != BuildingMethodAdmitted || next.Decision.Goal.Goal.Epoch != g.Goal.Epoch+1 || next.Decision.Goal.Methods[0].Plan == p.Spec.ID() {
		t.Fatal(next, err)
	}
}

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
	goal, err := db.ReviewGoal(ctx, g.ID, 0, snapshot, 7, domain.NeedDeficit, false)
	if err != nil {
		t.Fatal(err)
	}
	b, err := domain.NewBuilding("SleepingSpot", domain.Cell{X: 2, Z: 2}, domain.North, "")
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
	footprint := []domain.Cell{{X: 2, Z: 2}, {X: 2, Z: 3}}
	preview := policy.Preview{Action: a, Snapshot: snapshot, Tick: 7, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(false), Footprint: domain.Known(footprint), Costs: domain.Known([]policy.Amount{})}
	admitted, err := db.AdmitBuildingMethod(ctx, store.BuildingMethodRequest{Goal: g.ID, Revision: goal.Revision, Method: "player-sleep", Plan: p, Current: snapshot, Tick: 7, Bounds: domain.Known(policy.Bounds{Width: 100, Height: 100}), Stock: policy.StockObservation{Snapshot: snapshot, Tick: 7}, Previews: []policy.Preview{preview}, Purpose: policy.Routine})
	if err != nil || !admitted.Admitted {
		t.Fatal(admitted, err)
	}
	result, err := r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	compiled, err := db.LoadPlan(ctx, result.Decision.Goal.Methods[0].Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, record := range compiled.Admissions {
		for _, cell := range record.Admission.Footprint {
			for _, protected := range footprint {
				if cell == protected {
					t.Fatal("claimed player footprint", cell)
				}
			}
		}
	}
}

func TestRoutineReviewDerivesCleanupFromSharedDraftJournal(t *testing.T) {
	t.Parallel()
	r, db, session, _, _ := routineFixture(t)
	draft, err := domain.NewOwnedDraft("pawn")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewOwnedDraftAction("cleanup-a", draft)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("cleanup", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreatePlan(context.Background(), p); err != nil {
		t.Fatal(err)
	}
	check := func(want domain.NeedState) {
		t.Helper()
		result, err := r.Step(context.Background())
		if err != nil {
			t.Fatal(err)
		}
		for i, b := range result.Review.Goals {
			if b.Need == policy.RestoreWorkers {
				if result.Goals[i].Goal.Need != want {
					t.Fatal(result.Goals[i])
				}
				return
			}
		}
		t.Fatal("missing cleanup need")
	}
	check(domain.NeedRecovered)
	snapshot := session.State().Snapshot
	snapshot.Plan = p.ID()
	snapshot.Revision = 1
	if _, err = db.PrepareDraft(context.Background(), p.ID(), a.ID(), store.DraftAdmission{Snapshot: snapshot, Tick: 7, Pawn: "pawn", PawnSnapshotToken: "cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = db.Dispatch(context.Background(), p.ID(), a.ID(), snapshot, 7); err != nil {
		t.Fatal(err)
	}
	check(domain.NeedDeficit)
	if _, err = db.RecordDraftReceipt(context.Background(), p.ID(), a.ID(), 1, domain.ReceiptRefused, domain.Unknown[domain.DraftClaim]()); err != nil {
		t.Fatal(err)
	}
	check(domain.NeedRecovered)
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
	for _, row := range n.reply.GetObserved().Planning.GetObserved().Cells.Cells {
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
	for _, record := range compiled.Admissions {
		for _, cell := range record.Admission.Footprint {
			if aisle[cell] {
				t.Fatal("furniture blocks the doorway aisle", cell)
			}
		}
	}
}

// Only a refusal made entirely of insufficient stock lends the stock wait:
// anything ticks cannot resolve (geometry, spending, unknown facts) gets none,
// and neither does an admission (#66).
func TestStockRefusalWaitOnlyForStock(t *testing.T) {
	stock := policy.Refusal{Action: "a", Reason: policy.InsufficientStock, Resource: "Steel"}
	for _, c := range []struct {
		decision store.BuildingMethodDecision
		want     uint32
	}{
		{store.BuildingMethodDecision{Admitted: true}, 0},
		{store.BuildingMethodDecision{}, 0},
		{store.BuildingMethodDecision{Refused: []policy.Refusal{stock}}, stockWaitTicks},
		{store.BuildingMethodDecision{Refused: []policy.Refusal{stock, {Action: "b", Reason: policy.InsufficientStock, Resource: "WoodLog"}}}, stockWaitTicks},
		{store.BuildingMethodDecision{Refused: []policy.Refusal{stock, {Action: "b", Reason: policy.GeometryBlocked}}}, 0},
		{store.BuildingMethodDecision{Refused: []policy.Refusal{{Action: "a", Reason: policy.SpendingBlocked, Resource: "Steel"}}}, 0},
	} {
		if got := stockRefusalWait(c.decision); got != c.want {
			t.Fatal(c.decision, got)
		}
	}
}
