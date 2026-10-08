package domain

import "errors"

type TradeRequestKind string

const (
	TradeRequestCaravan     TradeRequestKind = "caravan"
	TradeRequestOrbital     TradeRequestKind = "orbital"
	CommsTradeRequestAction ActionKind       = "comms_trade_request"
)

// CommsTradeRequest fences a single paid vanilla request. Native cooldown and
// queue facts own recovery; a queued comms job is not the request effect.
type CommsTradeRequest struct {
	Kind                                     TradeRequestKind
	Faction, TraderKind, Console, Negotiator string
	ExpectedLastRequestTick                  int64
}

func (r CommsTradeRequest) Validate() error {
	if (r.Kind != TradeRequestCaravan && r.Kind != TradeRequestOrbital) || !validID(r.Faction) || !validID(r.TraderKind) || !validID(r.Console) || !validID(r.Negotiator) || r.ExpectedLastRequestTick < -2147483648 || r.ExpectedLastRequestTick > 2147483647 {
		return errors.New("invalid comms trade request")
	}
	return nil
}
func NewCommsTradeRequestAction(id ActionID, request CommsTradeRequest) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action id")
	}
	if err := request.Validate(); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: CommsTradeRequestAction, commsTradeRequest: request}, nil
}
func (a Action) CommsTradeRequest() (CommsTradeRequest, bool) {
	return a.commsTradeRequest, a.kind == CommsTradeRequestAction
}
