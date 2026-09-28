package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
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
