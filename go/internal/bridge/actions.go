package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// ActionsApplyMethod is Actions/Apply (#856): a batch of idempotent intents
// that native applies in order, each validated against live state and
// applied or refused on its own. A resent key returns its first result.
const ActionsApplyMethod = "rimgovernor/operations_apply"

// ActionsWriter sends intent batches.
type ActionsWriter struct{ client *Client }

func NewActionsWriter(client *Client) (*ActionsWriter, error) {
	if client == nil {
		return nil, contract("actions client missing")
	}
	return &ActionsWriter{client}, nil
}

// Apply sends actions and checks the reply carries one result per action,
// in order, under the same key. A batch_failure is returned as a
// *NativeFailure: nothing applied.
func (writer *ActionsWriter) Apply(ctx context.Context, identity *c.Identity, actions []*o.Action) (*o.ApplyReply, Result, error) {
	if writer == nil || writer.client == nil || ValidateIdentity(identity) != nil || len(actions) == 0 {
		return nil, Result{}, contract("invalid actions batch")
	}
	seen := map[string]bool{}
	for _, action := range actions {
		if action == nil || validID(action.GetKey()) != nil || seen[action.GetKey()] || action.GetIntent() == nil {
			return nil, Result{}, contract("action requires a unique key and an intent")
		}
		seen[action.GetKey()] = true
	}
	request := &o.ApplyRequest{Identity: proto.Clone(identity).(*c.Identity), Actions: actions}
	reply := &o.ApplyReply{}
	raw, err := writer.client.protoCall(ctx, ActionsApplyMethod, request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	if reply.BatchFailure != nil {
		return reply, raw, failure(reply.BatchFailure, raw)
	}
	if len(reply.Results) != len(actions) {
		return reply, raw, contract("actions reply result count mismatch")
	}
	for i, result := range reply.Results {
		if result.GetKey() != actions[i].GetKey() {
			return reply, raw, contract("actions reply key mismatch")
		}
		switch v := result.Outcome.(type) {
		case *o.ActionResult_Applied:
			if err := actionReceipt(v.Applied, identity); err != nil {
				return reply, raw, err
			}
		case *o.ActionResult_Refused:
			if v.Refused == nil || !diagnostic(v.Refused.Reason) {
				return reply, raw, contract("action refusal invalid")
			}
		case *o.ActionResult_Failed:
			if v.Failed == nil || v.Failed.Code == nil {
				return reply, raw, contract("action failure invalid")
			}
		default:
			return reply, raw, contract("action outcome missing")
		}
	}
	return reply, raw, nil
}

func actionReceipt(v *r.Receipt, identity *c.Identity) error {
	if v == nil {
		return contract("action receipt missing")
	}
	if err := buildingContext(v.AdmittedContext, identity, 0, false); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil || outcome.Applied.Observed == nil {
			return contract("action applied evidence missing")
		}
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("action uncertainty missing")
		}
	default:
		return contract("unsupported action receipt")
	}
	return nil
}
