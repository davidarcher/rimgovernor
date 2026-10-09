package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func ruleIdeoligion(precepts ...PreceptDef) Ideoligion {
	defs := IdeologyDefs{Precepts: map[string]PreceptDef{}}
	var held []HeldPrecept
	for n, p := range precepts {
		defs.Precepts[p.Name] = p
		held = append(held, HeldPrecept{ID: string(rune('a' + n)), Def: p.Name})
	}
	return Ideoligion{Defs: defs, Facts: IdeoligionFacts{Precepts: held}}
}

func took(event string, moods ...float64) PreceptEffect {
	return PreceptEffect{Kind: EffectSelfTookAction, HistoryEvent: event, StageMoods: moods}
}

func saw(event string, moods ...float64) PreceptEffect {
	return PreceptEffect{Kind: EffectWitnessedAction, HistoryEvent: event, StageMoods: moods}
}

func unwilling(event string, chance *float64, traits ...string) PreceptEffect {
	e := PreceptEffect{Kind: EffectUnwilling, HistoryEvent: event, NullifyingTraits: traits}
	if chance != nil {
		e.Chance = domain.Known(*chance)
	}
	return e
}

// TestActionStanceFamilies: one precept set answers each action
// family from its typed effects; events are fixture data, not a policy list.
func TestActionStanceFamilies(t *testing.T) {
	half := 0.5
	ideo := ruleIdeoligion(
		PreceptDef{Name: "Slaughter_Horrible", Effects: []PreceptEffect{took("Slaughter", -4), saw("Slaughter", -2)}},
		PreceptDef{Name: "Sale_Disapproved", Effects: []PreceptEffect{took("Sale", -1, -3)}},
		PreceptDef{Name: "Eating_Approved", Effects: []PreceptEffect{took("AteHumanMeat", 3)}},
		PreceptDef{Name: "Butcher_Abhorrent", Effects: []PreceptEffect{unwilling("ButcheredHuman", nil, "Psychopath")}},
		PreceptDef{Name: "Slavery_Abhorrent", Effects: []PreceptEffect{took("Enslave", -6), unwilling("Enslave", &half)}},
		PreceptDef{Name: "OrganUse_Horrible", Effects: []PreceptEffect{saw("OrganUse", -4), took("OrganUse", -15)}},
		PreceptDef{Name: "Apparel_Req", Effects: []PreceptEffect{{Kind: EffectApparel}}},
	)
	tests := []struct {
		name           string
		action         PreceptAction
		subject        PreceptSubject
		stance         PreceptStance
		doer, witness  float64
		refusal        float64
		matchedEffects int
	}{
		{"slaughter costs doer and witnesses", PreceptAction{HistoryEvent: "Slaughter"}, PreceptSubject{}, PreceptPenalised, 4, 2, 0, 2},
		{"sale costs the worst stage", PreceptAction{HistoryEvent: "Sale"}, PreceptSubject{}, PreceptPenalised, 3, 0, 0, 1},
		{"eating human meat is approved", PreceptAction{HistoryEvent: "AteHumanMeat"}, PreceptSubject{}, PreceptApproved, 0, 0, 0, 1},
		{"butchering forbids the colony", PreceptAction{HistoryEvent: "ButcheredHuman"}, PreceptSubject{}, PreceptForbidden, 0, 0, 1, 1},
		{"a psychopath butchers freely", PreceptAction{HistoryEvent: "ButcheredHuman"}, PreceptSubject{Pawn: true, Traits: []string{"Psychopath"}}, PreceptAllowed, 0, 0, 0, 0},
		{"a kind pawn still refuses", PreceptAction{HistoryEvent: "ButcheredHuman"}, PreceptSubject{Pawn: true, Traits: []string{"Kind"}}, PreceptForbidden, 0, 0, 1, 1},
		{"slavery is chance-refused and costs mood", PreceptAction{HistoryEvent: "Enslave"}, PreceptSubject{}, PreceptPenalised, 6, 0, 0.5, 2},
		{"organ use sums by who takes it", PreceptAction{HistoryEvent: "OrganUse"}, PreceptSubject{}, PreceptPenalised, 15, 4, 0, 2},
		{"burial has no precept", PreceptAction{HistoryEvent: "BuriedCorpse"}, PreceptSubject{}, PreceptAllowed, 0, 0, 0, 0},
		{"apparel effect is untyped so unknown", PreceptAction{Apparel: true}, PreceptSubject{}, PreceptUnknown, 0, 0, 0, 1},
		{"no action named is unknown", PreceptAction{}, PreceptSubject{}, PreceptUnknown, 0, 0, 0, 0},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			v := ActionStance(domain.Known(ideo), tc.action, tc.subject)
			if v.Stance != tc.stance || v.DoerMoodCost != tc.doer || v.WitnessMoodCost != tc.witness || v.RefusalChance != tc.refusal || len(v.Effects) != tc.matchedEffects {
				t.Fatalf("%+v, want stance %s doer %v witness %v refusal %v effects %d", v, tc.stance, tc.doer, tc.witness, tc.refusal, tc.matchedEffects)
			}
		})
	}
}

// TestActionStanceUnknownIdeoligionHolds: an unread ideoligion is never
// read as allowed, and an apparel action with no apparel precept is allowed.
func TestActionStanceUnknownIdeoligionHolds(t *testing.T) {
	if v := ActionStance(domain.Unknown[Ideoligion](), PreceptAction{HistoryEvent: "Slaughter"}, PreceptSubject{}); v.Stance != PreceptUnknown {
		t.Fatalf("unread ideoligion = %+v", v)
	}
	if v := ruleIdeoligion().ActionStance(PreceptAction{Apparel: true}, PreceptSubject{}); v.Stance != PreceptAllowed {
		t.Fatalf("no apparel precept = %+v", v)
	}
}

// TestActionStanceSlaveSkipsNonSlaveEffects: an effect only for non-slaves
// does not bear on a slave.
func TestActionStanceSlaveSkipsNonSlaveEffects(t *testing.T) {
	e := took("Sale", -5)
	e.OnlyForNonSlaves = true
	ideo := ruleIdeoligion(PreceptDef{Name: "Sale_Horrible", Effects: []PreceptEffect{e}})
	if v := ideo.ActionStance(PreceptAction{HistoryEvent: "Sale"}, PreceptSubject{Pawn: true, Slave: true}); v.Stance != PreceptAllowed {
		t.Fatalf("slave = %+v", v)
	}
	if v := ideo.ActionStance(PreceptAction{HistoryEvent: "Sale"}, PreceptSubject{Pawn: true}); v.Stance != PreceptPenalised || v.DoerMoodCost != 5 {
		t.Fatalf("free pawn = %+v", v)
	}
}
