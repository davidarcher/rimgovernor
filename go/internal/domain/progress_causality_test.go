package domain

import "testing"

func TestConstructionProofRequiresPendingAttemptCausality(t *testing.T) {
	p, scope := dispatched(t)
	for _, effect := range []Effect{EffectPending, EffectUnknown, EffectCompleted} {
		for _, causality := range []ObservationCausality{"", AfterDispatch} {
			o := Observation{Action: p.View().Action, Attempt: p.View().Attempt, Snapshot: scope, Tick: 11, Effect: effect, Causality: causality, ConstructionObserved: true}
			got, err := p.Observe(o, scope)
			if effect == EffectPending && causality == AfterDispatch {
				if err != nil {
					t.Fatal(err)
				}
				if tick, k := got.View().ConstructionObserved.Value(); !k || tick != 11 {
					t.Fatal(got.View())
				}
			} else if err == nil || got != p {
				t.Fatal("invalid proof changed progress", got, err)
			}
		}
	}
}

func TestEqualTickRequiresExplicitAttemptCausality(t *testing.T) {
	p, scope := dispatched(t)
	observation := Observation{Action: p.View().Action, Attempt: p.View().Attempt, Snapshot: scope, Tick: p.View().Tick, Effect: EffectCompleted}
	for _, causality := range []ObservationCausality{"", "invalid"} {
		observation.Causality = causality
		if got, err := p.Observe(observation, scope); err == nil || got != p {
			t.Fatal("uncorrelated same-tick completion accepted")
		}
	}
	observation.Causality = AfterDispatch
	got, err := p.Observe(observation, scope)
	if err != nil || got.View().Stage != Completed || got.View().Unresolved {
		t.Fatal(got, err)
	}
	observation.Tick--
	if _, err := p.Observe(observation, scope); err == nil {
		t.Fatal("causality allowed time reversal")
	}
	observation.Tick++
	observation.Attempt++
	if _, err := p.Observe(observation, scope); err == nil {
		t.Fatal("causality allowed wrong attempt")
	}
}

func TestKnownUnsuccessfulIsTerminalAndRetainsReason(t *testing.T) {
	for _, reason := range []UnsuccessfulReason{NativeFailure, NativeCancelled, NativeInterrupted, NativeExpired, TargetDead, OutcomeNotAchieved} {
		for _, cancelled := range []bool{false, true} {
			p, scope := dispatched(t)
			p, _ = p.RecordReceipt(p.View().Attempt, ReceiptUnknown)
			if cancelled {
				p, _ = p.Cancel()
			}
			got, err := p.Observe(Observation{Action: p.View().Action, Attempt: p.View().Attempt, Snapshot: scope, Tick: p.View().Tick, Effect: EffectUnsuccessful, Causality: AfterDispatch, UnsuccessfulReason: reason}, scope)
			want := Unsuccessful
			if cancelled {
				want = Cancelled
			}
			if err != nil || got.View().Stage != want || got.View().Unresolved {
				t.Fatal(got, err)
			}
			if value, known := got.View().UnsuccessfulReason.Value(); !known || value != reason {
				t.Fatal("reason lost")
			}
			if _, err := got.Prepare(scope, got.View().Tick+1); err == nil {
				t.Fatal("unsuccessful outcome retried")
			}
		}
	}
	p, scope := dispatched(t)
	for _, observation := range []Observation{
		{Action: p.View().Action, Attempt: 1, Snapshot: scope, Tick: 11, Effect: EffectUnsuccessful},
		{Action: p.View().Action, Attempt: 1, Snapshot: scope, Tick: 11, Effect: EffectUnsuccessful, UnsuccessfulReason: "invalid"},
		{Action: p.View().Action, Attempt: 1, Snapshot: scope, Tick: 11, Effect: EffectCompleted, UnsuccessfulReason: TargetDead},
	} {
		if got, err := p.Observe(observation, scope); err == nil || got != p {
			t.Fatal("invalid reason changed state")
		}
	}
}
