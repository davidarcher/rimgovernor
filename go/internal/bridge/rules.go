package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// The native declarative rules (operations.proto Rules, #2152): Go authors
// every rule and attaches the whole set with a lease each Round; native only
// executes.
const (
	rulesAttachMethod = "rimgovernor/rules_attach"
	rulesClearMethod  = "rimgovernor/rules_clear"
	rulesStatusMethod = "rimgovernor/rules_read_status"
)

// AttachReactions replaces every active native rule with the accepted ones and
// sets the lease to expiresAtTick (an absolute game tick). Refused rules
// come back on the reply with their reasons.
func (client *Client) AttachReactions(ctx context.Context, identity *c.Identity, rules []*o.Rule, expiresAtTick int64) (*o.RulesAttached, Result, error) {
	if ValidateIdentity(identity) != nil || expiresAtTick <= 0 {
		return nil, Result{}, contract("invalid rules attach")
	}
	request := &o.RulesAttachRequest{Identity: proto.Clone(identity).(*c.Identity), ExpiresAtTick: proto.Int64(expiresAtTick)}
	for _, rule := range rules {
		request.Rules = append(request.Rules, proto.Clone(rule).(*o.Rule))
	}
	reply := &o.RulesAttachReply{}
	raw, err := client.protoCall(ctx, rulesAttachMethod, request, reply)
	if err != nil {
		return nil, raw, err
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	attached := reply.GetAttached()
	if attached == nil || buildingUnknown(reply) != nil || buildingContext(attached.Context, identity, 0, false) != nil || len(attached.AcceptedIds)+len(attached.Refused) != len(rules) {
		return nil, raw, contract("invalid rules attach evidence")
	}
	for _, refusal := range attached.Refused {
		if refusal.GetReason() == o.RuleRefusalReason_RULE_REFUSAL_REASON_UNSPECIFIED {
			return nil, raw, contract("rule refusal reason missing")
		}
	}
	return attached, raw, nil
}

// ClearReactions deactivates every native rule and reports how many were active.
func (client *Client) ClearReactions(ctx context.Context, identity *c.Identity) (uint32, Result, error) {
	if ValidateIdentity(identity) != nil {
		return 0, Result{}, contract("invalid rules clear")
	}
	reply := &o.RulesClearReply{}
	raw, err := client.protoCall(ctx, rulesClearMethod, &o.RulesClearRequest{Identity: proto.Clone(identity).(*c.Identity)}, reply)
	if err != nil {
		return 0, raw, err
	}
	if reply.GetFailure() != nil {
		return 0, raw, failure(reply.GetFailure(), raw)
	}
	cleared := reply.GetCleared()
	if cleared == nil || buildingUnknown(reply) != nil || buildingContext(cleared.Context, identity, 0, false) != nil || cleared.Cleared == nil {
		return 0, raw, contract("invalid rules clear evidence")
	}
	return cleared.GetCleared(), raw, nil
}

// ReadReactions reads the active native rules, their lease and firing history.
func (client *Client) ReadReactions(ctx context.Context, identity *c.Identity) (*o.RulesStatus, Result, error) {
	if ValidateIdentity(identity) != nil {
		return nil, Result{}, contract("invalid rules status read")
	}
	reply := &o.RulesStatusReply{}
	raw, err := client.protoRead(ctx, rulesStatusMethod, &o.RulesStatusRequest{Identity: proto.Clone(identity).(*c.Identity)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	status := reply.GetStatus()
	if status == nil || buildingUnknown(reply) != nil || buildingContext(status.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid rules status evidence")
	}
	for _, rule := range status.Rules {
		if validID(rule.GetRuleId()) != nil {
			return nil, raw, contract("rules status rule id")
		}
	}
	return status, raw, nil
}

var (
	ruleTriggers   = map[domain.RuleTrigger]o.RuleTrigger{domain.RulePreyKilled: o.RuleTrigger_RULE_TRIGGER_PREY_KILLED}
	ruleActions    = map[domain.RuleActionKind]o.RuleAction{domain.RuleGiveJob: o.RuleAction_RULE_ACTION_GIVE_JOB}
	ruleTargets    = map[domain.RuleTarget]o.RuleTargetSelector{domain.RuleNearestDesignatedPrey: o.RuleTargetSelector_RULE_TARGET_SELECTOR_NEAREST_DESIGNATED_PREY}
	rulePredicates = map[domain.RulePredicate]o.RulePredicate{
		domain.RuleActorUndrafted:         o.RulePredicate_RULE_PREDICATE_ACTOR_UNDRAFTED,
		domain.RuleActorHuntingWorkActive: o.RulePredicate_RULE_PREDICATE_ACTOR_HUNTING_WORK_ACTIVE,
		domain.RuleTargetAvailable:        o.RulePredicate_RULE_PREDICATE_TARGET_AVAILABLE,
	}
)

// rulesAttachAction is the RulesAttachIntent that replaces every native rule
// and sets the lease (#2154); the journal holds it before native is written.
func rulesAttachAction(action domain.Action) (*o.Action, error) {
	attach, ok := action.RulesAttach()
	if !ok {
		return nil, contract("not a rules attach action")
	}
	intent := &o.RulesAttachIntent{LeaseTicks: proto.Int64(attach.LeaseTicks())}
	for _, rule := range attach.Rules() {
		trigger, triggerOK := ruleTriggers[rule.Trigger]
		kind, actionOK := ruleActions[rule.Action]
		target, targetOK := ruleTargets[rule.Target]
		if !triggerOK || !actionOK || !targetOK {
			return nil, contract("unsupported rule %s", rule.ID)
		}
		wire := &o.Rule{Id: proto.String(rule.ID), Trigger: trigger.Enum(), Action: kind.Enum(), Job: proto.String(rule.Job), Target: target.Enum(), Radius: proto.Uint32(rule.Radius)}
		for _, predicate := range rule.Predicates {
			value, ok := rulePredicates[predicate]
			if !ok {
				return nil, contract("unsupported rule predicate %s", predicate)
			}
			wire.Predicates = append(wire.Predicates, value)
		}
		intent.Rules = append(intent.Rules, wire)
	}
	return &o.Action{Intent: &o.Action_RulesAttach{RulesAttach: intent}}, nil
}
