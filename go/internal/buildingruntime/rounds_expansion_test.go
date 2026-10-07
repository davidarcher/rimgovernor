package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/slowtest"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"google.golang.org/protobuf/proto"
)

func TestExpansionSelectionReusesFurnishingAndWholeShell(t *testing.T) {
	t.Parallel()
	r := &RoundsBuildingPlanner{concern: policy.MaintainHousing, phase: policy.HousingExpansion}
	f := observation.ColonyProjection{Facts: policy.RoundsFacts{Colonists: domain.Known(int64(3)), IndoorCapacity: domain.Known(int64(3)), HousingTarget: domain.Known(int64(20))}}
	n, id, reason := r.selection(f)
	if n != 1 || id != "expansion-indoor-sleeping-4-1" || !reason.IsZero() {
		t.Fatal(n, id, reason)
	}
	// The shelter planner counts the same shortfall; its ring names its own
	// methods through the planned room's reconcile (#2277).
	r.shelter = true
	n, id, reason = r.selection(f)
	if n != 1 || id != "" || !reason.IsZero() {
		t.Fatal(n, id, reason)
	}
	f.Facts.IndoorCapacity = domain.Known(int64(4))
	if _, _, reason = r.selection(f); reason != BuildingReasonNoDeficit {
		t.Fatal(reason)
	}
	f.Facts.IndoorCapacity = domain.Unknown[int64]()
	if _, _, reason = r.selection(f); reason != fieldUnavailable("indoor_capacity") {
		t.Fatal(reason)
	}
}

func TestExpansionAdmitsSparePlaceAndManualCancels(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	base, db, _, request, n := bedroomFixture(t)
	ctx := context.Background()
	prepareExpansionReview(t, db, n)
	r, err := NewRoundsExpansionPlanner(base.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	got, err := r.Step(ctx)
	if err != nil || got.Verdict != BuildingReasonAdmitted {
		t.Fatal(got, err)
	}
	plan, err := db.LoadPlan(ctx, got.Decision.Standard.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 1 || plan.Progress[0].View().Attempt != 0 {
		t.Fatal(plan, err)
	}
	if again, err := r.Step(ctx); err != nil || again.Verdict != BuildingReasonExistingWork {
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
func expansionPolicy() policy.RoundsPolicy {
	p := policy.DefaultRoundsPolicy()
	p.Stage.Floor = policy.StageReserves
	return p
}

func prepareExpansionReview(t *testing.T, db *store.Store, n *sleepingNative) {
	t.Helper()
	ctx := context.Background()
	n.reply.GetObserved().ColonistCount = proto.Uint32(2)
	n.reply.GetObserved().IndoorSleepingCapacity = proto.Uint32(2)
	n.reply.GetObserved().BedCapacity = proto.Uint32(2)
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	facts := policy.RoundsFacts{Workers: domain.Known(3), Colonists: domain.Known(int64(2)), BedCapacity: domain.Known(int64(2)), IndoorCapacity: domain.Known(int64(2)), Hostiles: domain.Known(int64(0)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ChoiceDialog: domain.Known(false), Wood: domain.Known(int64(500))}
	_, err = db.ReviewRounds(ctx, store.RoundsRequest{Revision: review.Revision, Current: review.Snapshot, Tick: review.Tick, Enabled: true, Policy: expansionPolicy(), Facts: facts})
	if err != nil {
		t.Fatal(err)
	}
}
func TestExpansionAdmitsWholeShellWhenExistingRoomsAreFull(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	// No staged bunks: an open shelter rung would hold the housing goal's
	// later phases.
	base, db, n := shelterSiteFixture(t)
	prepareExpansionReview(t, db, n)
	r, err := NewRoundsExpansionPlanner(base.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	// The expansion builds the planned barracks (#1231).
	barracks, _ := policy.PlannedRoleFor(policy.RoomRoleShelter)
	recordLayout(t, r, db, policy.LayoutPlan{Rooms: []policy.PlannedRoom{{Role: barracks, Interior: policy.Rectangle{X: 1, Z: 1, Width: 7, Height: 7}, Door: domain.Cell{X: 4, Z: 0}, DoorRot: domain.South}}})
	got, err := r.Step(context.Background())
	if err != nil || got.Verdict != BuildingReasonAdmitted {
		t.Fatal(got, err)
	}
	plan, err := db.LoadPlan(context.Background(), got.Decision.Standard.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 32 || len(plan.Spec.Dependencies()) != 0 {
		t.Fatal(plan, err)
	}
	if again, err := r.Step(context.Background()); err != nil || again.Verdict != BuildingReasonExistingWork {
		t.Fatal(again, err)
	}
}
