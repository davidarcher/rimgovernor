package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func questAcceptRequest(t *testing.T) QuestAcceptRequest {
	t.Helper()
	accept, _ := domain.NewQuestAccept("quest-1", "pawn-1", 0)
	a, _ := domain.NewQuestAcceptAction("quest-accept-1", accept)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	quest := QuestFacts{
		Quest: "quest-1", SnapshotToken: "quest-cas", State: domain.Known("NotYetAccepted"), RequiresAccepter: domain.Known(true),
		CanAccept: domain.Known(true), ChoiceCount: domain.Known(int32(1)), EligibleAccepters: []domain.PawnID{"pawn-1"},
		HasTradeRequest: domain.Known(false),
	}
	return QuestAcceptRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: QuestAcceptFacts{Snapshot: s, QuestTick: 12, PreviewTick: 13, Quest: quest, NativeCanTry: domain.Known(true)}}
}

func TestQuestAcceptAdmission(t *testing.T) {
	r := questAcceptRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateQuestAccept(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestQuestAcceptAdmissionWithoutAccepterOrChoice(t *testing.T) {
	accept, _ := domain.NewQuestAccept("quest-1", "", -1)
	a, _ := domain.NewQuestAcceptAction("quest-accept-1", accept)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	quest := QuestFacts{
		Quest: "quest-1", SnapshotToken: "quest-cas", State: domain.Known("NotYetAccepted"), RequiresAccepter: domain.Known(false),
		CanAccept: domain.Known(true), ChoiceCount: domain.Known(int32(0)), HasTradeRequest: domain.Known(false),
	}
	r := QuestAcceptRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: QuestAcceptFacts{Snapshot: s, QuestTick: 12, PreviewTick: 13, Quest: quest, NativeCanTry: domain.Known(true)}}
	if d := EvaluateQuestAccept(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
}

func TestQuestAcceptDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*QuestAcceptRequest)
	}{
		{"zero action", func(r *QuestAcceptRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *QuestAcceptRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *QuestAcceptRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *QuestAcceptRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *QuestAcceptRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *QuestAcceptRequest) { r.Facts.PreviewTick = 11 }},
		{"zero generation", func(r *QuestAcceptRequest) { r.Current.Native = 0 }},
		{"native", func(r *QuestAcceptRequest) { r.Current.Native++ }},
		{"direction", func(r *QuestAcceptRequest) { r.Current.Direction++ }},
		{"colony", func(r *QuestAcceptRequest) { r.Current.Colony = "other" }},
		{"load", func(r *QuestAcceptRequest) { r.Current.Load = "other" }},
		{"map", func(r *QuestAcceptRequest) { r.Current.Map++ }},
		{"plan", func(r *QuestAcceptRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *QuestAcceptRequest) { r.Current.Revision++ }},
		{"quest CAS", func(r *QuestAcceptRequest) { r.Facts.Quest.SnapshotToken = "" }},
		{"wrong quest", func(r *QuestAcceptRequest) { r.Facts.Quest.Quest = "other" }},
		{"unknown state", func(r *QuestAcceptRequest) { r.Facts.Quest.State = domain.Unknown[string]() }},
		{"already settled", func(r *QuestAcceptRequest) { r.Facts.Quest.State = domain.Known("Ongoing") }},
		{"unknown can accept", func(r *QuestAcceptRequest) { r.Facts.Quest.CanAccept = domain.Unknown[bool]() }},
		{"cannot accept", func(r *QuestAcceptRequest) { r.Facts.Quest.CanAccept = domain.Known(false) }},
		{"unknown choice count", func(r *QuestAcceptRequest) { r.Facts.Quest.ChoiceCount = domain.Unknown[int32]() }},
		{"ambiguous choice", func(r *QuestAcceptRequest) { r.Facts.Quest.ChoiceCount = domain.Known(int32(2)) }},
		{"choice mismatch", func(r *QuestAcceptRequest) { r.Facts.Quest.ChoiceCount = domain.Known(int32(0)) }},
		{"unknown requires accepter", func(r *QuestAcceptRequest) { r.Facts.Quest.RequiresAccepter = domain.Unknown[bool]() }},
		{"missing eligible accepter", func(r *QuestAcceptRequest) { r.Facts.Quest.EligibleAccepters = nil }},
		{"preview refusal", func(r *QuestAcceptRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *QuestAcceptRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := questAcceptRequest(t)
			c.change(&r)
			d := EvaluateQuestAccept(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

func TestQuestAcceptRefusesAccepterWhenNotRequired(t *testing.T) {
	r := questAcceptRequest(t)
	r.Facts.Quest.RequiresAccepter = domain.Known(false)
	d := EvaluateQuestAccept(r)
	if d.Admitted || len(d.Refused) != 1 {
		t.Fatal(d)
	}
}
