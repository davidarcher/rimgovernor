package domain

import "errors"

// TradeParticipant is the closed identity of a seller, distinct from an
// economic TradeTarget (a desired stock level). Settlement trades also carry
// the player caravan whose inventory and negotiator own the session.
type TradeParticipant struct {
	Kind    TradeParticipantKind
	ID      string
	Caravan string
}

type TradeParticipantKind string

const (
	TradeParticipantMap        TradeParticipantKind = "map_trader"
	TradeParticipantSettlement TradeParticipantKind = "settlement"
	TradeParticipantOrbital    TradeParticipantKind = "orbital_ship"
)

func (p TradeParticipant) Validate() error {
	if !validID(p.ID) {
		return errors.New("invalid trade participant ID")
	}
	switch p.Kind {
	case TradeParticipantMap, TradeParticipantOrbital:
		if p.Caravan != "" {
			return errors.New("caravan on non-settlement trade participant")
		}
	case TradeParticipantSettlement:
		if !validID(p.Caravan) || p.Caravan == p.ID {
			return errors.New("invalid settlement trade caravan")
		}
	default:
		return errors.New("invalid trade participant kind")
	}
	return nil
}

// WithParticipant changes only the seller identity; the four existing trade
// steps, floors and signature checks remain the same lifecycle.
func (t Trade) WithParticipant(p TradeParticipant) (Trade, error) {
	if err := p.Validate(); err != nil {
		return Trade{}, err
	}
	t.participant = p
	return newTrade(t.kind, p, t.negotiator, t.giftMode, t.Lines(), t.allowPawns, t.expectedDealSignature, t.allowEmpty, t.endKind, t.receiveQuest)
}

func (t Trade) Participant() TradeParticipant { return t.participant }

// Key is the identity of a priced seller and the inventory owning the deal.
func (p TradeParticipant) Key() string {
	switch p.Kind {
	case TradeParticipantMap:
		return p.ID
	case TradeParticipantOrbital:
		return "orbital/" + p.ID
	case TradeParticipantSettlement:
		return "settlement/" + p.ID + "/" + p.Caravan
	default:
		return ""
	}
}
