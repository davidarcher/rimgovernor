package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// RuleLeaseTicks is how long native keeps an attached rule set without a
// renewal (an hour): Go re-attaches the set every Round, and a lapse
// deactivates every rule natively (docs/developers/contracts/native-rules.md).
const RuleLeaseTicks int64 = 2500

// huntChainRadius is the cells around the hunter the chain rule searches for
// its next prey; native's maximum is 100.
const huntChainRadius = 80

// HuntChainRuleID is the id of the hunt-chain rule.
const HuntChainRuleID = "hunt-chain"

// RuleSet is the declarative native rules a plan needs right now, in the
// order native receives them. Go authors it; the transport carries it without
// knowing what any rule is for. An empty set clears every active rule.
type RuleSet struct {
	Rules []domain.Rule
}

// HuntChainRules derives the rule set from the hunt plan: with a hunter who
// can take Hunting and a designated hunt prey standing, a hunter who kills
// moves on to the nearest designated prey instead of hauling its own kill;
// otherwise the set is empty. known is false while either census is unread,
// so a Round with no evidence changes nothing.
func HuntChainRules(sources domain.Fact[[]AcquisitionSource], hunters domain.Fact[[]PawnProfile]) (set RuleSet, known bool) {
	rows, sourcesKnown := sources.Value()
	profiles, huntersKnown := hunters.Value()
	if !sourcesKnown || !huntersKnown {
		return RuleSet{}, false
	}
	if _, ok := HunterFor(profiles); !ok {
		return RuleSet{}, true
	}
	for _, row := range rows {
		if row.Hunt && row.Designated {
			return RuleSet{Rules: []domain.Rule{{
				ID:         HuntChainRuleID,
				Trigger:    domain.RulePreyKilled,
				Predicates: []domain.RulePredicate{domain.RuleActorUndrafted, domain.RuleActorHuntingWorkActive, domain.RuleTargetAvailable},
				Action:     domain.RuleGiveJob,
				Job:        "Hunt",
				Target:     domain.RuleNearestDesignatedPrey,
				Radius:     huntChainRadius,
			}}}, true
		}
	}
	return RuleSet{}, true
}
