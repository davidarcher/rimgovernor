package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestExpansionSelectionReusesFurnishingAndWholeShell(t *testing.T) {
	t.Parallel()
	r := &RoutineBuildingPlanner{goal: policy.MaintainHousing, phase: policy.HousingExpansion}
	f := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(3)), IndoorCapacity: domain.Known(int64(3)), HousingTarget: domain.Known(int64(20))}}
	n, id, reason := r.selection(f)
	if n != 1 || id != "expansion-indoor-sleeping-4-1" || reason != "" {
		t.Fatal(n, id, reason)
	}
	r.shelter = true
	n, id, reason = r.selection(f)
	if n != 32 || id != "expansion-starter-shell" || reason != "" {
		t.Fatal(n, id, reason)
	}
	f.Facts.IndoorCapacity = domain.Known(int64(4))
	if _, _, reason = r.selection(f); reason != BuildingMethodNoDeficit {
		t.Fatal(reason)
	}
	f.Facts.IndoorCapacity = domain.Unknown[int64]()
	if _, _, reason = r.selection(f); reason != BuildingMethodUnknown {
		t.Fatal(reason)
	}
}

func TestExpansionAdmitsSparePlaceAndManualCancels(t *testing.T) {
	t.Parallel()
	base, db, _, request, n := sleepingFixture(t)
	ctx := context.Background()
	prepareExpansionReview(t, db, n)
	r, err := NewRoutineExpansionPlanner(base.reviewer, n, nil)
	if err != nil {
		t.Fatal(err)
	}
	got, err := r.Step(ctx)
	if err != nil || got.Reason != BuildingMethodAdmitted {
		t.Fatal(got, err)
	}
	plan, err := db.LoadPlan(ctx, got.Decision.Goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 1 || plan.Progress[0].View().Attempt != 0 {
		t.Fatal(plan, err)
	}
	if again, err := r.Step(ctx); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
	request.Kind, request.RequestID = store.PauseControl, "manual-expansion"
	if _, err = r.reviewer.player.Pause(ctx, request); err != nil {
		t.Fatal(err)
	}
	plan, err = db.LoadPlan(ctx, plan.Spec.ID())
	if err != nil || plan.Progress[0].View().Stage != domain.Pending {
		t.Fatal(plan, err)
	}
}

// expansionPolicy raises MaintainHousing's expansion phase, a Reserves step, on a fixture
// that has not climbed the stage ladder.
func expansionPolicy() policy.RoutinePolicy {
	p := policy.DefaultRoutinePolicy()
	p.Stage.Floor = policy.StageReserves
	return p
}

func prepareExpansionReview(t *testing.T, db *store.Store, n *sleepingNative) {
	t.Helper()
	ctx := context.Background()
	n.reply.GetObserved().ColonistCount = proto.Uint32(2)
	n.reply.GetObserved().IndoorSleepingCapacity = proto.Uint32(2)
	n.reply.GetObserved().BedCapacity = proto.Uint32(2)
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	facts := policy.RoutineFacts{Workers: domain.Known(3), Colonists: domain.Known(int64(2)), BedCapacity: domain.Known(int64(2)), IndoorCapacity: domain.Known(int64(2)), Hostiles: domain.Known(int64(0)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false), Wood: domain.Known(int64(500))}
	_, err = db.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: review.Revision, Current: review.Snapshot, Tick: review.Tick, Enabled: true, Policy: expansionPolicy(), Facts: facts})
	if err != nil {
		t.Fatal(err)
	}
}
func TestExpansionAdmitsWholeShellWhenExistingRoomsAreFull(t *testing.T) {
	t.Parallel()
	// No staged bunks: an open shelter rung would hold the housing goal's
	// later phases.
	base, db, n := shelterSiteFixture(t)
	prepareExpansionReview(t, db, n)
	r, err := NewRoutineExpansionPlanner(base.reviewer, n, nil)
	if err != nil {
		t.Fatal(err)
	}
	// The expansion builds the planned barracks (#1231).
	barracks, _ := policy.LayoutModule(policy.RoomRoleBarracks)
	recordLayout(t, r, db, policy.LayoutPlan{Rooms: []policy.LayoutRoom{{Role: barracks, Interior: policy.Rectangle{X: 1, Z: 1, Width: 7, Height: 7}, Door: domain.Cell{X: 4, Z: 0}, DoorRot: domain.South}}})
	got, err := r.Step(context.Background())
	if err != nil || got.Reason != BuildingMethodAdmitted {
		t.Fatal(got, err)
	}
	plan, err := db.LoadPlan(context.Background(), got.Decision.Goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 32 || len(plan.Spec.Dependencies()) != 0 {
		t.Fatal(plan, err)
	}
	if again, err := r.Step(context.Background()); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
}

// ruinNative offers one claimable ruin wall to a shell planner's
// clearance census and claim-token read.
type ruinNative struct {
	*sleepingNative
	rows []*o.ClearanceTarget
}

func (n *ruinNative) ReadClearanceTargets(_ context.Context, _ *c.Identity, _ bool) (*o.ClearanceTargetsReply, bridge.Result, error) {
	return &o.ClearanceTargetsReply{Outcome: &o.ClearanceTargetsReply_Observed{Observed: &o.ClearanceTargetsSnapshot{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Targets: n.rows}}}, bridge.Result{}, nil
}

func (n *ruinNative) ReadClaimBuildingTarget(_ context.Context, _ *c.Identity, thing string) (bridge.ClaimBuildingTarget, bridge.Result, error) {
	return bridge.ClaimBuildingTarget{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Thing: thing, Token: "claim-" + thing}, bridge.Result{}, nil
}

// A claimable ruin wall of the ring's kind standing on a non-shelter
// planned room's ring is claimed as wall before the ring is built, not
// deconstructed (#718, #1231).
func TestExpansionClaimsAMatchingRuinOnItsPlannedRing(t *testing.T) {
	t.Parallel()
	base, db, n := shelterSiteFixture(t)
	prepareExpansionReview(t, db, n)
	ruin := domain.Cell{X: 0, Z: 4}
	for _, cell := range n.reply.GetObserved().Planning.GetObserved().Cells.Cells {
		if cell.Cell.GetX() == ruin.X && cell.Cell.GetZ() == ruin.Z {
			cell.Walkable, cell.Ruin, cell.ClaimableRuin = proto.Bool(false), proto.Bool(true), proto.String("Wall")
		}
	}
	row := &o.ClearanceTarget{EntityId: proto.String("ruin"), DefName: proto.String("Wall"), Occupied: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(ruin.X), Z: proto.Int32(ruin.Z)}, Maximum: &c.Cell{X: proto.Int32(ruin.X), Z: proto.Int32(ruin.Z)}}, Class: o.ClearanceClass_CLEARANCE_CLASS_ANCIENT_WALL_DOOR, Deconstructible: proto.Bool(true), InHome: proto.Bool(true), AncientDanger: proto.Bool(false), Designated: proto.Bool(false)}
	native := &ruinNative{sleepingNative: n, rows: []*o.ClearanceTarget{row}}
	r, err := NewRoutineExpansionPlanner(base.reviewer, native, nil)
	if err != nil {
		t.Fatal(err)
	}
	barracks, _ := policy.LayoutModule(policy.RoomRoleBarracks)
	recordLayout(t, r, db, policy.LayoutPlan{Rooms: []policy.LayoutRoom{{Role: barracks, Interior: policy.Rectangle{X: 1, Z: 1, Width: 7, Height: 7}, Door: domain.Cell{X: 4, Z: 0}, DoorRot: domain.South}}})
	got, err := r.Step(context.Background())
	if err != nil || got.Reason != BuildingMethodAdmitted {
		t.Fatal(got, err)
	}
	var claim *store.PlanState
	for _, m := range got.Decision.Goal.Methods {
		if m.Method == shellClaimMethod(domain.Cell{}) {
			plan, err := db.LoadPlan(context.Background(), m.Plan)
			if err != nil {
				t.Fatal(err)
			}
			claim = &plan
		}
	}
	if claim == nil {
		t.Fatal("no shell-claim method admitted", got.Decision.Goal.Methods)
	}
	actions := claim.Spec.Actions()
	if len(actions) != 1 {
		t.Fatal(actions)
	}
	if target, ok := actions[0].ClaimBuilding(); !ok || target.Thing() != "ruin" {
		t.Fatal("the ruin is not claimed", actions[0])
	}
}
