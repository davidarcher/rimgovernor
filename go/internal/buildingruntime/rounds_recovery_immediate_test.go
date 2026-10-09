package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestImmediateAreaProtectionBypassesPendingService(t *testing.T) {
	r, journal, _, _, native := roundsFixture(t)
	composedRoundsFacts(t, native)
	r.native = &healthyWorkNative{roundsMedicalNative: &roundsMedicalNative{roundsNative: native}}
	r.methods = domain.Known([]policy.ConcernID{policy.RecoverDisasterServices})
	v := native.reply.GetObserved()
	entity := &o.EntityRef{Id: proto.String("patient"), DefName: proto.String("Human"), MapId: v.Context.Identity.MapId, Position: proto.Clone(v.Center).(*c.Cell)}
	v.Recovery = &o.RecoveryReply{Outcome: &o.RecoveryReply_Observed{Observed: &o.RecoverySnapshot{
		Context:      v.Context,
		Restrictions: []*o.RecoveryRestriction{{Pawn: bridge.NewRef(entity.GetId()), AreaId: proto.String("manual")}},
	}}}
	// "manual" is the Safe area and no sheltering trigger holds (#1326).
	v.Policies = &o.PolicySection{Outcome: &o.PolicySection_Observed{Observed: &o.PolicyFacts{AllowedAreas: []*o.AllowedAreaEntry{{Id: proto.String("manual"), Label: proto.String(policy.SafeAreaLabel)}}}}}
	ctx := context.Background()
	if _, err := r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	reviewBefore, err := journal.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	binding, ok := reviewBefore.Incident(policy.RecoverDisasterServices)
	if !ok {
		t.Fatal("missing recovery incident")
	}
	service, _ := domain.NewRecoveryService("patient", "damaged-building", domain.RecoveryServiceRepair)
	action, _ := domain.NewRecoveryServiceAction("ordinary-service-action", service)
	servicePlan, _ := domain.NewPlan("ordinary-service-plan", 1, []domain.Action{action})
	if _, err := journal.CommitIncidentMethod(ctx, binding.Incident, "ordinary-service-method", "", servicePlan); err != nil {
		t.Fatal(err)
	}
	call, epoch, done, err := r.player.enter(ctx, "rounds_review", false)
	if err != nil {
		t.Fatal(err)
	}
	_, err = r.immediateStep(call, epoch)
	done()
	if err != nil {
		t.Fatal(err)
	}
	planner, _ := NewRoundsRecoveryPlanner(r)
	first, err := planner.Step(ctx)
	if err != nil || first.Verdict != BuildingReasonAdmitted {
		t.Fatal(first, err)
	}
	plan, err := journal.LoadPlan(ctx, first.Plan)
	if err != nil {
		t.Fatal(err)
	}
	w, ok := plan.Spec.Actions()[0].WorkAssignment()
	if !ok || !w.AreaClear() {
		t.Fatal(w)
	}
	// The correction is the RecoverDisasterServices incident's method (#1078).
	review, err := journal.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	b, bound := review.Incident(policy.RecoverDisasterServices)
	incident, err := journal.LoadIncident(ctx, b.Incident)
	if !bound || err != nil || len(incident.Methods) != 2 || incident.Methods[0].Plan != first.Plan {
		t.Fatal("area correction not bound to the recovery incident", incident, err)
	}
	serviceState, err := journal.LoadPlan(ctx, servicePlan.ID())
	if err != nil || serviceState.Progress[0].View().Stage != domain.Pending {
		t.Fatal("protective admission changed pending service", serviceState, err)
	}
	duplicateAction, _ := domain.NewWorkAssignmentAction("duplicate-area-action", w)
	duplicate, _ := domain.NewPlan("duplicate-area-plan", 1, []domain.Action{duplicateAction})
	if _, err := journal.CommitIncidentMethod(ctx, b.Incident, "duplicate-area-method", "", duplicate); !errors.Is(err, store.ErrOpenMethod) {
		t.Fatal("open protective area method did not block duplicate", err)
	}
}
