package bridge

import (
	"context"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// tradeSessionTool is never fact-cached: a walk ends or opens its session
// without any write of ours to invalidate a cached row.
const tradeSessionTool = "rimgovernor/observations_read_trade_session"

// TradeSessionRead is native's one live trade: the negotiator walking
// to Trader to open a session (Open false), or the pair holding the open
// session (Open true). Trader and Negotiator are empty when neither exists.
type TradeSessionRead struct {
	Target     *c.TradeTarget
	Context    *c.ObservationContext
	Trader     string
	Negotiator string
	Open       bool
}

// ReadTradeSession reads which negotiator is walking to or trading with which
// trader. The read issues no order: a walk the game ended simply reads as
// absent.
func (client *Client) ReadTradeSession(ctx context.Context, identity *c.Identity) (TradeSessionRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return TradeSessionRead{}, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	request := &o.TradeSessionRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}}
	reply := &o.TradeSessionReply{}
	raw, err := client.protoRead(ctx, tradeSessionTool, request, reply)
	if err != nil {
		return TradeSessionRead{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return TradeSessionRead{}, raw, err
	}
	var session *o.TradeSession
	switch v := reply.Outcome.(type) {
	case *o.TradeSessionReply_Failure:
		return TradeSessionRead{}, raw, failure(v.Failure, raw)
	case *o.TradeSessionReply_Unavailable:
		return TradeSessionRead{}, raw, unavailable(v.Unavailable, raw)
	case *o.TradeSessionReply_Observed:
		session = v.Observed
	default:
		return TradeSessionRead{}, raw, contract("trade session outcome missing")
	}
	if err = ValidateContext(session.Context); err != nil {
		return TradeSessionRead{}, raw, err
	}
	if !sameIdentity(session.Context.Identity, identity) {
		return TradeSessionRead{}, raw, contract("trade session world mismatch")
	}
	out := TradeSessionRead{Context: session.Context, Target: proto.CloneOf(session.Target), Trader: TradeParticipantOf(session.Target).Key(), Negotiator: session.GetNegotiatorId(), Open: session.GetOpen()}
	if (session.Target == nil) != (session.NegotiatorId == nil) || out.Open && session.Target == nil {
		return TradeSessionRead{}, raw, contract("trade session names half a pair")
	}
	if session.Target != nil && (ValidateTradeTarget(session.Target, identity) != nil || validID(out.Negotiator) != nil || out.Trader != "" && out.Trader == out.Negotiator) {
		return TradeSessionRead{}, raw, contract("invalid trade session pair")
	}
	return out, raw, nil
}
