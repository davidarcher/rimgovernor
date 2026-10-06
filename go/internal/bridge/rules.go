package bridge

import (
	"context"

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
