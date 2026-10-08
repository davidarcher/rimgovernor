package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func init() {
	// Paid requests require observed native effects, unlike receipt-terminal
	// idempotent settings. The builder uses Apply without registering IntentMode.
	intentKinds[domain.CommsTradeRequestAction] = commsTradeRequestAction
}
func commsTradeRequestAction(action domain.Action) (*o.Action, error) {
	r, ok := action.CommsTradeRequest()
	if !ok || r.Validate() != nil {
		return nil, contract("invalid comms trade request")
	}
	kind := c.TradeRequestKind_TRADE_REQUEST_KIND_CARAVAN
	if r.Kind == domain.TradeRequestOrbital {
		kind = c.TradeRequestKind_TRADE_REQUEST_KIND_ORBITAL
	}
	return &o.Action{Intent: &o.Action_CommsTradeRequest{CommsTradeRequest: &o.CommsTradeRequestIntent{
		Kind: kind, FactionId: proto.String(r.Faction), TraderKind: proto.String(r.TraderKind), ConsoleId: proto.String(r.Console), NegotiatorId: proto.String(r.Negotiator), ExpectedLastRequestTick: proto.Int64(r.ExpectedLastRequestTick),
	}}}, nil
}
