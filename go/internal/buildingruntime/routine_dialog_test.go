package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func choiceDialog(labels ...string) *o.ChoiceDialog {
	d := &o.ChoiceDialog{WindowId: proto.Int32(77), WindowType: proto.String("Verse.Dialog_NodeTree"), Title: proto.String("Research finished"), Text: proto.String("Done."), Interactive: proto.Bool(true)}
	for i, label := range labels {
		selectable := label[0] != '!'
		if !selectable {
			label = label[1:]
		}
		option := &o.ChoiceDialogOption{Index: proto.Int32(int32(i)), Label: proto.String(label), Selectable: proto.Bool(selectable), Resolves: proto.Bool(true)}
		if !selectable {
			option.DisabledReason = proto.String("fixture")
		}
		d.Options = append(d.Options, option)
	}
	return d
}

// The dialog goal opens while the census carries a dialog section, closes as
// recovered once it is gone, and the planner answers it with the preferred
// selectable option exactly once per observed dialog.
func TestRoutineDialogGoalAndPlannerAnswerPreferredOption(t *testing.T) {
	t.Parallel()
	r, db, _, _, n := routineFixture(t)
	ctx := context.Background()
	v := n.reply.GetObserved()
	v.Dialog = choiceDialog("!OK", "Research screen", "Ignore")
	out, err := r.Step(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var goal domain.GoalID
	for _, binding := range out.Review.Goals {
		if binding.Need == policy.AnswerDialog {
			goal = binding.Goal
		}
	}
	g, err := db.LoadGoal(ctx, goal)
	if err != nil || g.Goal.Status != domain.GoalActive || g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g, err)
	}
	planner, err := NewRoutineDialogPlanner(r, n, policy.DialogAnswerPolicy{Prefer: policy.DefaultDialogAnswerPrefer})
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted || result.Option != "Ignore" {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	answer, ok := plan.Spec.Actions()[0].DialogAnswer()
	if !ok || answer.WindowID() != 77 || answer.OptionIndex() != 2 || answer.OptionLabel() != "Ignore" {
		t.Fatal(answer, ok)
	}
	next, err := planner.Step(ctx)
	if err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}
	v.Dialog = nil
	if _, err = r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if g, err = db.LoadGoal(ctx, goal); err != nil || g.Goal.Need != domain.NeedRecovered {
		t.Fatal("dialog goal did not recover once the dialog closed", g, err)
	}
}

// A dialog still inside its interactivity delay is not unanswerable: the
// planner waits for the next review instead of reporting exhaustion (#179).
func TestRoutineDialogPlannerWaitsForInteractivity(t *testing.T) {
	t.Parallel()
	r, _, _, _, n := routineFixture(t)
	ctx := context.Background()
	dialog := choiceDialog("OK")
	dialog.Interactive = proto.Bool(false)
	n.reply.GetObserved().Dialog = dialog
	if _, err := r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineDialogPlanner(r, n, policy.DialogAnswerPolicy{})
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodNotInteractive {
		t.Fatal(result, err)
	}
	dialog.Interactive = proto.Bool(true)
	if result, err = planner.Step(ctx); err != nil || result.Reason != BuildingMethodAdmitted || result.Option != "OK" {
		t.Fatal(result, err)
	}
}

func TestRoutineDialogPlannerHoldsWithoutSelectableOption(t *testing.T) {
	t.Parallel()
	r, _, _, _, n := routineFixture(t)
	ctx := context.Background()
	n.reply.GetObserved().Dialog = choiceDialog("!OK", "!Research screen")
	if _, err := r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineDialogPlanner(r, n, policy.DialogAnswerPolicy{})
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Reason != BuildingMethodExhausted {
		t.Fatal(result, err)
	}
}
