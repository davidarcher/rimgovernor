package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rangedHunter() PawnProfile {
	return PawnProfile{ID: "hunter", Ranged: true}
}

func TestHuntChainRulesFromHuntPlan(t *testing.T) {
	prey := []AcquisitionSource{{ID: "berry", Food: true}, {ID: "deer", Hunt: true, Designated: true}, {ID: "elk", Hunt: true, Designated: true}}
	set, known := HuntChainRules(domain.Known(prey), domain.Known([]PawnProfile{rangedHunter()}), nil)
	if !known || len(set.Rules) != 1 {
		t.Fatalf("set = %+v, known = %v", set, known)
	}
	rule := set.Rules[0]
	if rule.ID != HuntChainRuleID || rule.Trigger != domain.RulePreyKilled || rule.Action != domain.RuleGiveJob || rule.Job != "Hunt" || rule.Target != domain.RuleNearestDesignatedPrey || rule.Radius != huntChainRadius {
		t.Fatalf("rule = %+v", rule)
	}
	if _, err := domain.NewRulesAttach(set.Rules, RuleLeaseTicks); err != nil {
		t.Fatalf("the derived set is not attachable: %v", err)
	}
}

func TestHuntChainRulesStayUntilLastDesignationIsGone(t *testing.T) {
	hunters := domain.Known([]PawnProfile{rangedHunter()})
	one := []AcquisitionSource{{ID: "deer", Hunt: true, Designated: true}, {ID: "elk", Hunt: true}}
	if set, known := HuntChainRules(domain.Known(one), hunters, nil); !known || len(set.Rules) != 1 {
		t.Fatalf("final designated prey lost the chain: %+v", set)
	}
	two := []AcquisitionSource{{ID: "deer", Hunt: true, Designated: true}, {ID: "elk", Hunt: true, Designated: true}}
	if set, known := HuntChainRules(domain.Known(two), hunters, nil); !known || len(set.Rules) != 1 {
		t.Fatalf("two designated prey: %+v", set)
	}
	if set, known := HuntChainRules(domain.Known(one[1:]), hunters, nil); !known || len(set.Rules) != 0 {
		t.Fatalf("empty set kept the rule: %+v", set)
	}
}

func TestHuntChainRulesNeedHunterAndDesignatedPrey(t *testing.T) {
	designated := []AcquisitionSource{{ID: "deer", Hunt: true, Designated: true}, {ID: "elk", Hunt: true, Designated: true}}
	cases := []struct {
		name    string
		sources domain.Fact[[]AcquisitionSource]
		hunters domain.Fact[[]PawnProfile]
		known   bool
	}{
		{"no hunter", domain.Known(designated), domain.Known([]PawnProfile{{ID: "melee"}}), true},
		{"prey not designated", domain.Known([]AcquisitionSource{{ID: "deer", Hunt: true}}), domain.Known([]PawnProfile{rangedHunter()}), true},
		{"no prey", domain.Known([]AcquisitionSource(nil)), domain.Known([]PawnProfile{rangedHunter()}), true},
		{"unread census", domain.Unknown[[]AcquisitionSource](), domain.Known([]PawnProfile{rangedHunter()}), false},
		{"unread roster", domain.Known(designated), domain.Unknown[[]PawnProfile](), false},
	}
	for _, c := range cases {
		set, known := HuntChainRules(c.sources, c.hunters, nil)
		if known != c.known || len(set.Rules) != 0 {
			t.Errorf("%s: set = %+v, known = %v", c.name, set, known)
		}
	}
}

func TestHuntChainRulesKeepDesignatedPreyHeldFromNewAcquisition(t *testing.T) {
	hunters := domain.Known([]PawnProfile{rangedHunter()})
	for _, reason := range []string{HuntHoldNoButcher, HuntHoldNoHunter, HuntHoldFogged} {
		holds := []HuntHold{{Reason: reason, Source: AcquisitionSource{ID: "deer", Hunt: true, Designated: true}}}
		set, known := HuntChainRules(domain.Known([]AcquisitionSource{}), hunters, holds)
		if !known || len(set.Rules) != 1 {
			t.Errorf("%s cleared standing hunt orders: %+v, known=%v", reason, set, known)
		}
		holds[0].Source.Designated = false
		if set, known := HuntChainRules(domain.Known([]AcquisitionSource{}), hunters, holds); !known || len(set.Rules) != 0 {
			t.Errorf("%s offered an undesignated held prey: %+v", reason, set)
		}
		if _, known := HuntChainRules(domain.Unknown[[]AcquisitionSource](), hunters, holds); known {
			t.Errorf("%s made an unread census known", reason)
		}
	}
}
