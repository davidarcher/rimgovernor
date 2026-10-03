package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func (r *RoutineCreepJoinerPlanner) Step(ctx context.Context) (RoutineCreepJoinerResult, error) {
	call, epoch, done, err := r.reviewer.player.enter(ctx, "test", false)
	if err != nil {
		return RoutineCreepJoinerResult{}, err
	}
	defer done()
	return r.step(call, epoch, newStepArbiter())
}

// creepJoinerRow makes a colonist row a creepjoiner whose downside has or
// has not fired, holding a weapon or not.
func creepJoinerRow(row *o.PawnState, triggered bool, weapon string) {
	row.Anomaly = &o.PawnAnomaly{Entity: proto.Bool(false), Creepjoiner: &o.CreepJoinerState{Form: proto.String("Form"), Benefit: proto.String("Benefit"), DownsideTriggered: proto.Bool(triggered)}}
	row.Health = &o.PawnHealth{HediffCompleteness: &o.Completeness{Filtered: proto.Uint64(0)}}
	row.Biography = &o.PawnBiography{DisabledWorkTags: nil}
	if weapon != "" {
		row.Equipment = &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String(weapon)}
	}
}

func creepJoinerFixture(t *testing.T, edit func(*o.PawnState), goal policy.GoalID) (*RoutineReviewer, *equipTestNative) {
	t.Helper()
	reviewer, _, _, _, native := routineFixture(t)
	v := native.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("naming"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
	n := &equipTestNative{routineNative: native, ids: []string{"joiner", "other"}, editPawn: func(row *o.PawnState) {
		if row.Pawn.GetId() == "joiner" {
			edit(row)
		}
	}, weapons: []bridge.EquipCandidate{
		{Thing: "bow1", Definition: "Bow_Short", Ranged: true, ByTrade: true},
		{Thing: "bow2", Definition: "Bow_Short", Ranged: true, ByTrade: true},
	}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{goal})
	return reviewer, n
}

// TestEquipPlannerLeavesAHiddenDownsideCreepJoinerUnarmed (#1740): with two
// colonists and two bows, the creepjoiner whose downside has not shown is
// given none and the other colonist is armed.
func TestEquipPlannerLeavesAHiddenDownsideCreepJoinerUnarmed(t *testing.T) {
	ctx := context.Background()
	reviewer, n := creepJoinerFixture(t, func(row *o.PawnState) { creepJoinerRow(row, false, "") }, policy.EnsureBasicDefense)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineEquipPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := reviewer.player.journal.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if len(plan.Spec.Actions()) != 1 {
		t.Fatal("expected one order", plan.Spec)
	}
	equip, ok := plan.Spec.Actions()[0].Equip()
	if !ok || equip.Pawn() != "other" {
		t.Fatal("the creepjoiner was armed", plan.Spec.Actions()[0])
	}
}

// TestEquipPlannerArmsACreepJoinerOnceTheDownsideShows: once the game has
// fired the downside the colonist is an ordinary one again.
func TestEquipPlannerArmsACreepJoinerOnceTheDownsideShows(t *testing.T) {
	ctx := context.Background()
	reviewer, n := creepJoinerFixture(t, func(row *o.PawnState) { creepJoinerRow(row, true, "") }, policy.EnsureBasicDefense)
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineEquipPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, _ := reviewer.player.journal.LoadPlan(ctx, result.Plan)
	if len(plan.Spec.Actions()) != 2 {
		t.Fatal("both colonists should be armed", plan.Spec)
	}
}

// TestCreepJoinerPlannerDropsTheHeldWeapon (#1740): the review owes a drop
// for a creepjoiner holding a weapon before its downside shows, and the
// planner orders exactly that colonist to drop exactly that weapon.
func TestCreepJoinerPlannerDropsTheHeldWeapon(t *testing.T) {
	ctx := context.Background()
	reviewer, _ := creepJoinerFixture(t, func(row *o.PawnState) { creepJoinerRow(row, false, "club") }, policy.ManageCreepJoiners)
	planner, err := NewRoutineCreepJoinerPlanner(reviewer)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := reviewer.player.journal.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if len(plan.Spec.Actions()) != 1 {
		t.Fatal("expected one drop", plan.Spec)
	}
	drop, ok := plan.Spec.Actions()[0].DropEquipment()
	if !ok || drop.Pawn() != "joiner" || drop.Thing() != "club" {
		t.Fatal(plan.Spec.Actions()[0])
	}
	if next, err := planner.Step(ctx); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal("duplicated an open drop", next, err)
	}
}

// TestCreepJoinerPlannerLeavesAShownDownsideAlone: a creepjoiner whose
// downside has fired keeps its weapon, and so does a colonist that is none.
func TestCreepJoinerPlannerLeavesAShownDownsideAlone(t *testing.T) {
	ctx := context.Background()
	reviewer, _ := creepJoinerFixture(t, func(row *o.PawnState) { creepJoinerRow(row, true, "club") }, policy.ManageCreepJoiners)
	planner, err := NewRoutineCreepJoinerPlanner(reviewer)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if result, err := planner.Step(ctx); err != nil || result.Verdict != BuildingReasonNoDeficit {
		t.Fatal(result, err)
	}
}

// inspectableRow is creepJoinerRow with the surgical inspection on offer and,
// when queued, its bill on the stack.
func inspectableRow(row *o.PawnState, queued bool) {
	creepJoinerRow(row, false, "")
	row.Health.Operations = []*o.SurgeryOperation{{Recipe: &o.DefinitionRef{DefName: proto.String("InspectX")}, PartIndex: proto.Int32(4), Kind: o.SurgeryKind_SURGERY_KIND_OTHER,
		EligibleDoctors: proto.Uint32(1), IngredientsOnMap: proto.Bool(true)}}
	if queued {
		row.Health.SurgeryBills = []*o.SurgeryBill{{Id: proto.String("bill1"), Recipe: proto.String("InspectX")}}
	}
}

// TestCreepJoinerPlannerInspectsOnceAndRecordsIt (#1740): a creepjoiner whose
// downside is hidden is queued the surgical inspection the def mirror's
// recipe names, the goal's record says it was ordered, and once the bill is
// gone the record says it was inspected; nothing is ordered twice.
func TestCreepJoinerPlannerInspectsOnceAndRecordsIt(t *testing.T) {
	ctx := context.Background()
	queued := false
	reviewer, n := creepJoinerFixture(t, func(row *o.PawnState) { inspectableRow(row, queued) }, policy.ManageCreepJoiners)
	n.recipes = []*d.RecipeDef{{DefName: "InspectX", WorkerClass: "RimWorld.Recipe_SurgicalInspection"}, {DefName: "Other", WorkerClass: "RimWorld.Recipe_RemoveBodyPart"}}
	planner, err := NewRoutineCreepJoinerPlanner(reviewer)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := reviewer.player.journal.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	if len(plan.Spec.Actions()) != 1 {
		t.Fatal("expected one inspection", plan.Spec)
	}
	surgery, ok := plan.Spec.Actions()[0].Surgery()
	if !ok || surgery.Pawn() != "joiner" || surgery.Recipe() != "InspectX" || surgery.Part() != 4 {
		t.Fatal(plan.Spec.Actions()[0])
	}
	record := func() policy.CreepJoinerRecord {
		r, err := creepJoinerRecord(ctx, reviewer.player.journal)
		if err != nil {
			t.Fatal(err)
		}
		return r
	}
	if got := record().Inspections["joiner"]; got != policy.InspectionOrdered {
		t.Fatal("record after the order:", got)
	}
	// The bill is queued: the inspection stays ordered and nothing new is owed.
	queued = true
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if got := record().Inspections["joiner"]; got != policy.InspectionOrdered {
		t.Fatal("record while the bill is queued:", got)
	}
	// The bill is gone: the colony has inspected the creepjoiner, once.
	queued = false
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if result, err = planner.Step(ctx); err != nil || result.Plan != "" && result.Verdict == BuildingReasonAdmitted {
		t.Fatal("ordered a second inspection", result, err)
	}
	if got := record().Inspections["joiner"]; got != policy.InspectionDone {
		t.Fatal("record after the bill ended:", got)
	}
}
