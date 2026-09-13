package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestExpansionSelectionReusesFurnishingAndWholeShell(t *testing.T) {
	t.Parallel()
	r := &RoutineBuildingPlanner{goal: policy.EnsureExpansion}
	f := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(3)), IndoorCapacity: domain.Known(int64(3)), HousingTarget: domain.Known(int64(20))}}
	n, id, reason := r.selection(f)
	if n != 1 || id != "indoor-sleeping-4-1" || reason != "" {
		t.Fatal(n, id, reason)
	}
	r.shelter = true
	n, id, reason = r.selection(f)
	if n != 32 || id != "starter-shell" || reason != "" {
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
	r, err := NewRoutineExpansionPlanner(base.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	got, err := r.Step(ctx)
	if err != nil || got.Reason != BuildingMethodAdmitted {
		t.Fatal(got, err)
	}
	plan, err := db.LoadPlan(ctx, got.Decision.Goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 1 || len(plan.Admissions) != 1 || plan.Progress[0].View().Attempt != 0 {
		t.Fatal(plan, err)
	}
	if again, err := r.Step(ctx); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
	request.Kind, request.RequestID = store.ManualControl, "manual-expansion"
	request.Plan, request.Revision = "", 0
	if _, err = r.reviewer.player.Manual(ctx, request); err != nil {
		t.Fatal(err)
	}
	plan, err = db.LoadPlan(ctx, plan.Spec.ID())
	if err != nil || plan.Progress[0].View().Stage != domain.Cancelled {
		t.Fatal(plan, err)
	}
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
	facts := policy.RoutineFacts{Workers: domain.Known(3), Colonists: domain.Known(int64(2)), BedCapacity: domain.Known(int64(2)), IndoorCapacity: domain.Known(int64(2)), Hostiles: domain.Known(int64(0)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), Wood: domain.Known(int64(500))}
	_, err = db.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: review.Revision, Current: review.Snapshot, Tick: review.Tick, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: facts})
	if err != nil {
		t.Fatal(err)
	}
}
func TestExpansionAdmitsWholeShellWhenExistingRoomsAreFull(t *testing.T) {
	t.Parallel()
	base, db, n := shelterFixture(t)
	prepareExpansionReview(t, db, n)
	r, err := NewRoutineExpansionPlanner(base.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	got, err := r.Step(context.Background())
	if err != nil || got.Reason != BuildingMethodAdmitted {
		t.Fatal(got, err)
	}
	plan, err := db.LoadPlan(context.Background(), got.Decision.Goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 32 || len(plan.Admissions) != 32 || len(plan.Spec.Dependencies()) != 31 {
		t.Fatal(plan, err)
	}
	if again, err := r.Step(context.Background()); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
}
