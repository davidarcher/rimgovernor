package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// TestRulesAttachIntentWire is the bridge contract for #2154: a rules_attach
// action is a journaled intent whose wire arm carries the rules and a relative lease.
func TestRulesAttachIntentWire(t *testing.T) {
	rule := domain.Rule{ID: "hunt-chain", Trigger: domain.RulePreyKilled, Predicates: []domain.RulePredicate{domain.RuleActorUndrafted, domain.RuleActorHuntingWorkActive, domain.RuleTargetAvailable},
		Action: domain.RuleGiveJob, Job: "Hunt", Target: domain.RuleNearestDesignatedPrey, Radius: 80}
	attach, err := domain.NewRulesAttach([]domain.Rule{rule}, 2500)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewRulesAttachAction("rules-0", attach)
	if err != nil {
		t.Fatal(err)
	}
	if !action.Kind().IntentMode() {
		t.Fatal("rules_attach is not an intent-mode kind")
	}
	wire, err := IntentAction("key-1", action)
	if err != nil {
		t.Fatal(err)
	}
	got := wire.GetRulesAttach()
	if wire.GetKey() != "key-1" || got.GetLeaseTicks() != 2500 || len(got.Rules) != 1 {
		t.Fatalf("wire = %v", wire)
	}
	r := got.Rules[0]
	if r.GetId() != "hunt-chain" || r.GetTrigger() != o.RuleTrigger_RULE_TRIGGER_PREY_KILLED || r.GetAction() != o.RuleAction_RULE_ACTION_GIVE_JOB || r.GetJob() != "Hunt" ||
		r.GetTarget() != o.RuleTargetSelector_RULE_TARGET_SELECTOR_NEAREST_DESIGNATED_PREY || r.GetRadius() != 80 || len(r.Predicates) != 3 {
		t.Fatalf("rule = %v", r)
	}
}

func TestRulesAttachIntentClears(t *testing.T) {
	attach, _ := domain.NewRulesAttach(nil, 2500)
	action, _ := domain.NewRulesAttachAction("rules-1", attach)
	wire, err := IntentAction("key-2", action)
	if err != nil || wire.GetRulesAttach() == nil || len(wire.GetRulesAttach().Rules) != 0 {
		t.Fatalf("wire = %v, %v", wire, err)
	}
}
