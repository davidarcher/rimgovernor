package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"slices"
	"testing"
)

func TestRoutineFoodPolicyReplansRepeatedSettingsAndRoundTrips(t *testing.T) {
	r, db, _, _, n := routineFixture(t)
	r.native = &healthyWorkNative{routineMedicalNative: &routineMedicalNative{routineNative: n}}
	v := n.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(1), proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	v.Issues = append(v.Issues, missing("naming"))
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true), FoodRestriction: &o.FoodRestriction{PolicyId: proto.String("diet"), EligibleDefs: []string{"MealSimple"}}}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		row.Biography.Skills = append(row.Biography.Skills, &o.Skill{Definition: &o.DefinitionRef{DefName: proto.String(skill)}, Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: proto.String("None")})
	}
	for _, work := range []string{"Construction", "Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	}
	row.Settings.Snapshot = &o.SnapshotRef{Context: proto.Clone(v.Context).(*c.ObservationContext), EntityId: proto.String("patient"), Token: proto.String("same-restrictive-token")}
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	ctx := context.Background()
	planner, err := NewRoutineWorkPlanner(r)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	var prior domain.PlanID
	for round := 0; round < 2; round++ {
		result, err := planner.Step(ctx)
		if err != nil || result.Reason != BuildingMethodAdmitted || result.Plan == prior {
			t.Fatal(result, err)
		}
		plan, err := db.LoadPlan(ctx, result.Plan)
		if err != nil || len(plan.Spec.Actions()) != 1 {
			t.Fatal(plan, err)
		}
		action := plan.Spec.Actions()[0]
		food, ok := action.WorkAssignment()
		if !ok || !slices.Equal(food.FoodAllow(), []string{"MealSimple"}) || len(food.Settings()) != 0 {
			t.Fatal(food)
		}
		if next, err := planner.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
			t.Fatal(next, err)
		}
		// Closing old work cannot suppress a later repair with the identical CAS.
		if _, err = db.Cancel(ctx, plan.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
		prior = result.Plan
	}
}
