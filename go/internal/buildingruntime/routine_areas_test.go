package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestAreaAttemptsSurviveRetirementAndRepeatedRestriction(t *testing.T) {
	changes := []policy.AllowedAreaChange{{Pawn: "colonist"}, {Pawn: "pet", Animal: true}}
	seen := map[domain.MethodID]bool{}
	for admitted := range 20 {
		_, method := nextAreaChange(changes, admitted)
		if seen[method] {
			t.Fatal("renewed correction reused a retired method")
		}
		seen[method] = true
	}
}

func TestAreaPlannerManualDoesNotReadOrPlan(t *testing.T) {
	r, _, session, request, native := routineFixture(t)
	request.Kind, request.RequestID = store.PauseControl, "manual-area"
	if _, err := r.player.Pause(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	before := native.reads
	planner, err := NewRoutineRecoveryPlanner(r)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodDisabled || native.reads != before || session.State().Enabled {
		t.Fatal(result, err)
	}
}

func TestAreaPlannerFreshRestrictionAndStaleCASAfterRestart(t *testing.T) {
	r, journal, _, _, native := routineFixture(t)
	composedRoutineFacts(t, native)
	r.native = &healthyWorkNative{routineMedicalNative: &routineMedicalNative{routineNative: native}}
	r.methods = domain.Known([]policy.GoalID{policy.RecoverDisasterServices})
	v := native.reply.GetObserved()
	entity := &o.EntityRef{Id: proto.String("patient"), DefName: proto.String("Human"), MapId: v.Context.Identity.MapId, Position: proto.Clone(v.Center).(*c.Cell)}
	v.Recovery = &o.RecoveryReply{Outcome: &o.RecoveryReply_Observed{Observed: &o.RecoverySnapshot{
		Context: v.Context, RoofHazard: proto.Bool(false),
		Restrictions: []*o.RecoveryRestriction{{Pawn: entity, AreaId: proto.String("manual")}},
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)},
	}}}
	ctx := context.Background()
	if _, err := r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, _ := NewRoutineRecoveryPlanner(r)
	first, err := planner.Step(ctx)
	if err != nil || first.Reason != BuildingMethodAdmitted {
		t.Fatal(first, err)
	}
	plan, err := journal.LoadPlan(ctx, first.Plan)
	if err != nil {
		t.Fatal(err)
	}
	w, ok := plan.Spec.Actions()[0].WorkAssignment()
	if !ok || !w.AreaClear() || w.BeforeToken() != "before-work" {
		t.Fatal(w)
	}
	// A freshly constructed planner has no in-memory area ownership. A saved
	// native settings edit must cancel the old CAS and admit the correction again.
	native.pawnReply.GetObserved().Pawns[0].Settings.Snapshot.Token = proto.String("renewed-work")
	r, err = NewRoutineReviewer(r.player, r.native, r.clock, r.policy, r.maxAge)
	if err != nil {
		t.Fatal(err)
	}
	planner, _ = NewRoutineRecoveryPlanner(r)
	second, err := planner.Step(ctx)
	if err != nil || second.Reason != BuildingMethodAdmitted || second.Plan == first.Plan {
		t.Fatal(second, err)
	}
	plan, err = journal.LoadPlan(ctx, first.Plan)
	if err != nil || plan.Progress[0].View().Stage != domain.Cancelled {
		t.Fatal(plan, err)
	}
}
