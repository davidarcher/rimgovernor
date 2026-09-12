package domain

import "testing"

func TestDraftCannotRetainConstructionIdentity(t *testing.T) {
	p, scope, claim := draftDispatched(t)
	observation := draftObservation(p, scope, EffectCompleted)
	observation.Construction = &ConstructionIdentity{Origin: "blueprint", Current: "building"}
	if got, err := p.ObserveDraft(observation, scope, Known(claim)); err == nil || got != p {
		t.Fatal("draft acquired building ownership", got, err)
	}
}

func TestCompletedConstructionIdentityRequiresCausalTerminalEvidence(t *testing.T) {
	p, scope := dispatched(t)
	identity := ConstructionIdentity{Origin: "blueprint", Current: "building"}
	for _, effect := range []Effect{EffectUnknown, EffectPending, EffectCompleted, EffectAbsent} {
		for _, causality := range []ObservationCausality{"", AfterDispatch} {
			o := Observation{Action: p.View().Action, Attempt: p.View().Attempt, Snapshot: scope, Tick: 11, Effect: effect, Causality: causality, Construction: &identity}
			got, err := p.Observe(o, scope)
			if effect == EffectCompleted && causality == AfterDispatch {
				proof, known := got.View().Construction.Value()
				if err != nil || !known || proof != identity {
					t.Fatal(got, err)
				}
			} else if err == nil || got != p {
				t.Fatal("invalid identity proof changed progress", got, err)
			}
		}
	}
	identity.Current = ""
	if _, err := p.Observe(Observation{Action: p.View().Action, Attempt: p.View().Attempt, Snapshot: scope, Tick: 11, Effect: EffectCompleted, Causality: AfterDispatch, Construction: &identity}, scope); err == nil {
		t.Fatal("empty native identity accepted")
	}
}
