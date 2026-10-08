package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// ValidateTradeTarget validates the closed participant variants against the
// action/observation identity. World-object IDs resolve natively in that world.
func ValidateTradeTarget(target *c.TradeTarget, identity *c.Identity) error {
	if err := ValidateIdentity(identity); err != nil {
		return err
	}
	if target == nil {
		return contract("trade target missing")
	}
	switch v := target.Kind.(type) {
	case *c.TradeTarget_MapTrader:
		if v.MapTrader == nil || validID(v.MapTrader.GetTraderId()) != nil {
			return contract("invalid map trade target")
		}
	case *c.TradeTarget_Settlement:
		if v.Settlement == nil || validID(v.Settlement.GetSettlementId()) != nil || validID(v.Settlement.GetCaravanId()) != nil || v.Settlement.GetSettlementId() == v.Settlement.GetCaravanId() {
			return contract("invalid settlement trade target")
		}
	case *c.TradeTarget_OrbitalShip:
		if v.OrbitalShip == nil || validID(v.OrbitalShip.GetShipId()) != nil {
			return contract("invalid orbital trade target")
		}
	default:
		return contract("trade target arm missing")
	}
	return nil
}

func mapTradeTarget(id string) *c.TradeTarget {
	return &c.TradeTarget{Kind: &c.TradeTarget_MapTrader{MapTrader: &c.MapTradeTarget{TraderId: proto.String(id)}}}
}

func mapTraderID(target *c.TradeTarget) string { return target.GetMapTrader().GetTraderId() }

// TradeParticipantOf reads the already-validated closed wire identity.
func TradeParticipantOf(target *c.TradeTarget) domain.TradeParticipant {
	switch v := target.GetKind().(type) {
	case *c.TradeTarget_MapTrader:
		return domain.TradeParticipant{Kind: domain.TradeParticipantMap, ID: v.MapTrader.GetTraderId()}
	case *c.TradeTarget_OrbitalShip:
		return domain.TradeParticipant{Kind: domain.TradeParticipantOrbital, ID: v.OrbitalShip.GetShipId()}
	case *c.TradeTarget_Settlement:
		return domain.TradeParticipant{Kind: domain.TradeParticipantSettlement, ID: v.Settlement.GetSettlementId(), Caravan: v.Settlement.GetCaravanId()}
	default:
		return domain.TradeParticipant{}
	}
}
