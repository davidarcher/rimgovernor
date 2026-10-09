package domain

import (
	"encoding/json"
	"errors"
)

// RulesAttachAction attaches the native declarative rules: a
// RulesAttachIntent on Actions/Apply that replaces every active native rule and
// sets the lease. It is an intent kind, so the attachment has a receipt and
// sits in the session journal before native is written.
const RulesAttachAction ActionKind = "rules_attach"

// MaxRules is native's active-rule limit (docs/developers/contracts/native-rules.md).
const MaxRules = 16

// RuleTrigger, RulePredicate, RuleActionKind and RuleTarget are the closed
// words of a native rule; the bridge maps each to its wire enum.
type (
	RuleTrigger    string
	RulePredicate  string
	RuleActionKind string
	RuleTarget     string
)

const (
	RulePreyKilled RuleTrigger = "prey_killed"

	RuleActorUndrafted         RulePredicate = "actor_undrafted"
	RuleActorHuntingWorkActive RulePredicate = "actor_hunting_work_active"
	RuleTargetAvailable        RulePredicate = "target_available"

	RuleGiveJob RuleActionKind = "give_job"

	RuleNearestDesignatedPrey RuleTarget = "nearest_designated_prey"
)

// Rule is "on Trigger, if every Predicate holds, do Action Job on Target within
// Radius cells of the actor". Go authors it; native only executes it.
type Rule struct {
	ID         string
	Trigger    RuleTrigger
	Predicates []RulePredicate
	Action     RuleActionKind
	Job        string
	Target     RuleTarget
	Radius     uint32
}

// RulesAttach is an immutable, comparable value: the canonical encoded rules
// (empty list clears) and the lease in ticks from the apply tick.
type RulesAttach struct {
	rules string
	lease int64
}

// NewRulesAttach validates rules (at most MaxRules, unique ids) and a positive lease.
func NewRulesAttach(rules []Rule, leaseTicks int64) (RulesAttach, error) {
	if leaseTicks <= 0 || len(rules) > MaxRules {
		return RulesAttach{}, errors.New("invalid rules attach")
	}
	seen := map[string]bool{}
	for _, rule := range rules {
		if !validID(rule.ID) || seen[rule.ID] || rule.Trigger == "" || rule.Action == "" || !validID(rule.Job) || rule.Target == "" || rule.Radius == 0 {
			return RulesAttach{}, errors.New("invalid rule")
		}
		for _, predicate := range rule.Predicates {
			if !validID(string(predicate)) {
				return RulesAttach{}, errors.New("invalid rule predicate")
			}
		}
		seen[rule.ID] = true
	}
	if rules == nil {
		rules = []Rule{}
	}
	data, err := json.Marshal(rules)
	if err != nil {
		return RulesAttach{}, err
	}
	return RulesAttach{string(data), leaseTicks}, nil
}

// Rules are the rules in request order.
func (r RulesAttach) Rules() []Rule {
	var rules []Rule
	_ = json.Unmarshal([]byte(r.rules), &rules)
	return rules
}

// LeaseTicks is the lease length from the apply tick.
func (r RulesAttach) LeaseTicks() int64 { return r.lease }

func NewRulesAttachAction(id ActionID, attach RulesAttach) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	canonical, err := NewRulesAttach(attach.Rules(), attach.lease)
	if err != nil || canonical != attach {
		return Action{}, errors.New("invalid rules attach")
	}
	return Action{id: id, kind: RulesAttachAction, rulesAttach: attach}, nil
}

func (a Action) RulesAttach() (RulesAttach, bool) { return a.rulesAttach, a.kind == RulesAttachAction }
