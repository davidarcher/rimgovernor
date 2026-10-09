package domain

import "testing"

func huntRule(id string) Rule {
	return Rule{ID: id, Trigger: RulePreyKilled, Predicates: []RulePredicate{RuleActorUndrafted, RuleTargetAvailable}, Action: RuleGiveJob, Job: "Hunt", Target: RuleNearestDesignatedPrey, Radius: 80, PredatorMarginCells: 25}
}

func TestRulesAttachActionIsAnIntentWithComparableValue(t *testing.T) {
	attach, err := NewRulesAttach([]Rule{huntRule("hunt-chain")}, 2500)
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewRulesAttachAction("rules-0", attach)
	if err != nil {
		t.Fatal(err)
	}
	got, ok := a.RulesAttach()
	if !ok || got.LeaseTicks() != 2500 || len(got.Rules()) != 1 || got.Rules()[0].ID != "hunt-chain" || a.Kind() != RulesAttachAction {
		t.Fatalf("rules attach = %+v %v", got, ok)
	}
	again, _ := NewRulesAttachAction("rules-0", attach)
	if a != again {
		t.Fatal("equal attachments differ")
	}
}

func TestRulesAttachValidation(t *testing.T) {
	if _, err := NewRulesAttach(nil, 2500); err != nil {
		t.Fatalf("an empty set clears: %v", err)
	}
	if _, err := NewRulesAttach([]Rule{huntRule("a")}, 0); err == nil {
		t.Fatal("zero lease accepted")
	}
	if _, err := NewRulesAttach([]Rule{huntRule("a"), huntRule("a")}, 2500); err == nil {
		t.Fatal("duplicate rule id accepted")
	}
	tooMany := make([]Rule, MaxRules+1)
	for i := range tooMany {
		tooMany[i] = huntRule(string(rune('a' + i)))
	}
	if _, err := NewRulesAttach(tooMany, 2500); err == nil {
		t.Fatal("more than MaxRules accepted")
	}
	bad := huntRule("a")
	bad.Radius = 0
	if _, err := NewRulesAttach([]Rule{bad}, 2500); err == nil {
		t.Fatal("zero radius accepted")
	}
}
