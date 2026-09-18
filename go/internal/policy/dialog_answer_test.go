package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestChooseDialogOptionPrefersInOrderThenFirstSelectable(t *testing.T) {
	options := []DialogOption{{Index: 0, Label: "Attack", Selectable: true}, {Index: 1, Label: "Trade with them", Selectable: true}, {Index: 2, Label: "Ignore them", Selectable: true}}
	if got, ok := ChooseDialogOption(options, DialogAnswerPolicy{Prefer: DefaultDialogAnswerPrefer}); !ok || got.Index != 2 {
		t.Fatal(got, ok)
	}
	if got, ok := ChooseDialogOption(options, DialogAnswerPolicy{Prefer: []string{"trade", "ignore"}}); !ok || got.Index != 1 {
		t.Fatal(got, ok)
	}
	if got, ok := ChooseDialogOption(options, DialogAnswerPolicy{Prefer: []string{"give"}}); !ok || got.Index != 0 {
		t.Fatal("expected fallback to the first selectable option", got, ok)
	}
	options[0].Selectable = false
	if got, ok := ChooseDialogOption(options, DialogAnswerPolicy{}); !ok || got.Index != 1 {
		t.Fatal("disabled option chosen", got, ok)
	}
	if _, ok := ChooseDialogOption([]DialogOption{{Label: "OK"}}, DialogAnswerPolicy{Prefer: []string{"OK"}}); ok {
		t.Fatal("no selectable option must yield no choice")
	}
}

func TestEvaluateDialogAnswerAdmitsOnlyFreshAcceptedPreview(t *testing.T) {
	value, _ := domain.NewDialogAnswer(7, 0, "OK")
	action, _ := domain.NewDialogAnswerAction("dialog-0", value)
	current := domain.GenerationSnapshot{Colony: "c", Load: "l", Map: 0, Plan: "p", Revision: 1, Native: 3}
	plan, _ := domain.NewPlan("p", 1, []domain.Action{action})
	progress, err := domain.NewProgress(plan, action.ID())
	if err != nil {
		t.Fatal(err)
	}
	request := func(accepted domain.Fact[bool], tick domain.Tick) DialogAnswerRequest {
		return DialogAnswerRequest{Action: action, Progress: progress, Current: current, MinimumTick: 10, Facts: DialogAnswerFacts{Snapshot: current, Tick: tick, Accepted: accepted}}
	}
	if d := EvaluateDialogAnswer(request(domain.Known(true), 12)); !d.Admitted {
		t.Fatal(d)
	}
	if d := EvaluateDialogAnswer(request(domain.Known(false), 12)); d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != DialogRefused {
		t.Fatal(d)
	}
	if d := EvaluateDialogAnswer(request(domain.Unknown[bool](), 12)); d.Admitted || d.Refused[0].Reason != UnknownFacts {
		t.Fatal(d)
	}
	if d := EvaluateDialogAnswer(request(domain.Known(true), 9)); d.Admitted || d.Refused[0].Reason != StaleFacts {
		t.Fatal(d)
	}
}
