package bridge

import (
	"context"
	"errors"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func huntRule(id string) *op.Rule {
	return &op.Rule{Id: proto.String(id), Trigger: op.RuleTrigger_RULE_TRIGGER_PREY_KILLED.Enum(),
		Predicates: []op.RulePredicate{op.RulePredicate_RULE_PREDICATE_ACTOR_UNDRAFTED, op.RulePredicate_RULE_PREDICATE_TARGET_AVAILABLE},
		Action:     op.RuleAction_RULE_ACTION_GIVE_JOB.Enum(), Job: proto.String("Hunt"),
		Target: op.RuleTargetSelector_RULE_TARGET_SELECTOR_NEAREST_DESIGNATED_PREY.Enum(), Radius: proto.Uint32(40)}
}

func TestAttachReactionsReportsAcceptedAndRefused(t *testing.T) {
	rules := []*op.Rule{huntRule("a"), huntRule("a")}
	valid := &op.RulesAttachReply{Outcome: &op.RulesAttachReply_Attached{Attached: &op.RulesAttached{Context: pbContext(), AcceptedIds: []string{"a"},
		Refused: []*op.RuleRefusal{{RuleId: proto.String("a"), Reason: op.RuleRefusalReason_RULE_REFUSAL_REASON_DUPLICATE_ID.Enum()}}}}}
	for _, test := range []struct {
		name   string
		change func(*op.RulesAttachReply)
		ok     bool
	}{
		{"accepted and refused", func(*op.RulesAttachReply) {}, true},
		{"refusal without reason", func(v *op.RulesAttachReply) { v.GetAttached().Refused[0].Reason = nil }, false},
		{"missing rule", func(v *op.RulesAttachReply) { v.GetAttached().AcceptedIds = nil }, false},
		{"failure outcome", func(v *op.RulesAttachReply) {
			v.Outcome = &op.RulesAttachReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum()}}
		}, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			reply := proto.Clone(valid).(*op.RulesAttachReply)
			test.change(reply)
			s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
				if arg.Tool != "rimgovernor/rules_attach" {
					t.Fatal(arg.Tool)
				}
				return pbResult(reply), nil
			}}
			got, _, err := testClient(t, s, testBudget).AttachReactions(context.Background(), pbIdentity(), rules, 2500)
			if !test.ok {
				if !errors.Is(err, ErrContract) && !errors.Is(err, ErrRefused) {
					t.Fatal("expected rejection", err)
				}
				return
			}
			if err != nil || len(got.AcceptedIds) != 1 || len(got.Refused) != 1 {
				t.Fatal(got, err)
			}
		})
	}
	if _, _, err := testClient(t, &testServer{schema: protoSchema}, testBudget).AttachReactions(context.Background(), pbIdentity(), rules, 0); !errors.Is(err, ErrContract) {
		t.Fatal("a lease without a tick must not be sent", err)
	}
}
