package bridge

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// CombatOrders applies one combat_orders batch as an Actions/Apply intent
// under key (#939) and decodes its per-order results: the fight's orders at
// a stop (#852). A refused or failed action returns an error.
func (client *Client) CombatOrders(ctx context.Context, identity *c.Identity, key string, command *o.CombatOrders) ([]CombatOrderResult, error) {
	if err := ValidateCombatOrders(command); err != nil {
		return nil, err
	}
	writer, err := NewActionsWriter(client)
	if err != nil {
		return nil, err
	}
	command = proto.Clone(command).(*o.CombatOrders)
	action := &o.Action{Key: proto.String(key), Intent: &o.Action_CombatOrders{CombatOrders: command}}
	reply, _, err := writer.Apply(ctx, identity, []*o.Action{action})
	if err != nil {
		return nil, err
	}
	switch v := reply.Results[0].Outcome.(type) {
	case *o.ActionResult_Refused:
		return nil, contract("combat orders refused: %s", v.Refused.GetReason())
	case *o.ActionResult_Failed:
		return nil, contract("combat orders failed: %s", v.Failed.GetDetail())
	}
	results, err := CombatOrderResults(reply.Results[0].GetApplied(), command)
	for _, res := range results {
		RecordCombatOrder(ctx, res)
	}
	return results, err
}
