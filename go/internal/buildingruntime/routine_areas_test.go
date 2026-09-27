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

func TestAreaPlannerFreshRestriction(t *testing.T) {
	r, journal, _, _, native := routineFixture(t)
	composedRoutineFacts(t, native)
	r.native = &healthyWorkNative{routineMedicalNative: &routineMedicalNative{routineNative: native}}
	r.methods = domain.Known([]policy.GoalID{policy.RecoverDisasterServices})
	v := native.reply.GetObserved()
	entity := &o.EntityRef{Id: proto.String("patient"), DefName: proto.String("Human"), MapId: v.Context.Identity.MapId, Position: proto.Clone(v.Center).(*c.Cell)}
	v.Recovery = &o.RecoveryReply{Outcome: &o.RecoveryReply_Observed{Observed: &o.RecoverySnapshot{
		Context: v.Context, RoofHazard: proto.Bool(false),
		Restrictions: []*o.RecoveryRestriction{{Pawn: entity, AreaId: proto.String("manual")}},
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
}
