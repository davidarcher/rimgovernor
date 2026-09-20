package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestMedicineTierAutonomousAdmissionAndPersistence(t *testing.T) {
	hospital, db, native := hospitalFixture(t)
	ctx := context.Background()
	row := native.pawnReply.GetObserved().Pawns[0]
	row.Settings.MedicalCare = proto.String("Best")
	row.Settings.Snapshot = &o.SnapshotRef{Context: proto.Clone(native.reply.GetObserved().Context).(*c.ObservationContext), EntityId: proto.String("patient"), Token: proto.String("care-before")}
	row.Health.LifeThreatening = proto.Bool(false)
	row.Health.Hediffs = []*o.Hediff{{Definition: &o.DefinitionRef{DefName: proto.String("Flu")}, Bad: proto.Bool(true), Severity: proto.Float64(.1), Immunity: proto.Float64(.1), SeverityPerDay: proto.Float64(.1), ImmunityPerDay: proto.Float64(.2)}}
	row.Health.HediffCompleteness = hospitalCount(1)
	native.reply.GetObserved().Resources = []*o.Quantity{{DefName: proto.String("MedicineHerbal"), Units: proto.Int64(5)}, {DefName: proto.String("MedicineIndustrial"), Units: proto.Int64(5)}}
	// Drop the fixture's cached reading after replacing its clinical facts.
	hospital.reviewer.census = routineCensusStore{}
	if _, err := hospital.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	state := hospital.reviewer.player.session.State()
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	planner := &RoutineMedicalPlanner{reviewer: hospital.reviewer}
	call, epoch, done, err := hospital.reviewer.player.enter(ctx, false)
	if err != nil {
		t.Fatal(err)
	}
	defer done()
	result, err := planner.planMedicineTier(call, epoch, state, review, newStepArbiter())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	w, ok := plan.Spec.Actions()[0].WorkAssignment()
	if !ok || w.Pawn() != "patient" || w.BeforeToken() != "care-before" || w.MedicalCare() != "HerbalOrWorse" {
		t.Fatal(w, ok)
	}
	arbiter := newStepArbiter()
	result, err = planner.planMedicineTier(call, epoch, state, review, arbiter)
	if err != nil || result.Reason != BuildingMethodExistingWork {
		t.Fatal(result, err)
	}
	if arbiter.tryClaim([]domain.PawnID{"patient"}) {
		t.Fatal("tending must wait for pending care settings")
	}
}

func TestMedicineTierReassessesCare(t *testing.T) {
	pawn := policy.CarePawn{ID: "p", Dead: domain.Known(false), Care: domain.Known("NoCare"), SettingsToken: domain.Known("current"), LifeThreatening: domain.Known(false), Conditions: domain.Known([]policy.CareCondition{{DefName: domain.Known("Plague"), Severity: domain.Known(.1), Immunity: domain.Known(.1), SeverityPerDay: domain.Known(.1), ImmunityPerDay: domain.Known(.2)}})}
	facts := policy.RoutineFacts{MedicalPawns: domain.Known([]policy.CarePawn{pawn}), Resources: domain.Known([]policy.Amount{{Resource: "MedicineIndustrial", Count: 2}})}
	work := medicineTierAssignments(facts)
	if len(work) != 1 || work[0].MedicalCare() != "NormalOrWorse" {
		t.Fatal(work)
	}
	pawn.Care = domain.Known("NormalOrWorse")
	facts.MedicalPawns = domain.Known([]policy.CarePawn{pawn})
	if work = medicineTierAssignments(facts); len(work) != 0 {
		t.Fatal("already applied", work)
	}
	pawn.Care = domain.Known("Best")
	facts.MedicalPawns = domain.Known([]policy.CarePawn{pawn})
	if work = medicineTierAssignments(facts); len(work) != 1 {
		t.Fatal("Auto must reconsider native edits", work)
	}
	facts.Resources = domain.Unknown[[]policy.Amount]()
	if work = medicineTierAssignments(facts); len(work) != 0 {
		t.Fatal("unknown stock", work)
	}
}
