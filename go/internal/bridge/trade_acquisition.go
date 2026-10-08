package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

const tradeAcquisitionTool = "rimgovernor/observations_read_trade_acquisition"

// ReadTradeAcquisition preserves optional native facts: absent cost, relation,
// eligibility or sampled timing cannot become zero or permission to dispatch.
// pack uses the existing formation intent solely as a calculation input.
func (client *Client) ReadTradeAcquisition(ctx context.Context, identity *c.Identity, pack *op.FormCaravanIntent) (*o.TradeAcquisition, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &o.TradeAcquisitionRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.CloneOf(identity)}, Pack: proto.CloneOf(pack)}
	reply := &o.TradeAcquisitionReply{}
	raw, err := client.protoRead(ctx, tradeAcquisitionTool, request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.TradeAcquisitionReply_Failure:
		return nil, raw, failure(v.Failure, raw)
	case *o.TradeAcquisitionReply_Unavailable:
		return nil, raw, unavailable(v.Unavailable, raw)
	case *o.TradeAcquisitionReply_Observed:
		if err := validateTradeAcquisition(v.Observed, identity); err != nil {
			return nil, raw, err
		}
		return proto.CloneOf(v.Observed), raw, ctx.Err()
	default:
		return nil, raw, contract("trade acquisition outcome missing")
	}
}

func validateTradeAcquisition(v *o.TradeAcquisition, identity *c.Identity) error {
	if v == nil {
		return contract("trade acquisition missing")
	}
	if err := ValidateContext(v.Context); err != nil {
		return err
	}
	if !sameIdentity(v.Context.Identity, identity) {
		return contract("trade acquisition world mismatch")
	}
	if len(v.Consoles)+len(v.PassingShips)+len(v.Requests)+len(v.Arrivals)+len(v.CommsWork) > tradersMaximumRows {
		return contract("trade acquisition exceeds bound")
	}
	seen := map[string]bool{}
	for _, row := range v.Consoles {
		if row == nil || validID(row.GetId()) != nil || seen[row.GetId()] {
			return contract("invalid trade console")
		}
		seen[row.GetId()] = true
		if err := tradeNegotiatorIDs(row.NegotiatorIds); err != nil {
			return err
		}
	}
	for _, row := range v.PassingShips {
		if row == nil || validID(row.GetId()) != nil || seen[row.GetId()] || row.Trader == nil || !diagnostic(row.TraderKind) || !diagnostic(row.FactionId) || row.DepartureTick != nil && row.GetDepartureTick() < v.Context.GetTick() {
			return contract("invalid passing ship")
		}
		seen[row.GetId()] = true
		if !row.GetTrader() && (row.TraderKind != nil || row.CanTrade != nil) {
			return contract("trader facts on non-trader ship")
		}
	}
	seen = map[string]bool{}
	for _, row := range v.Arrivals {
		if row == nil || validID(row.GetFactionId()) != nil || validID(row.GetTraderKind()) != nil || row.Kind != c.TradeRequestKind_TRADE_REQUEST_KIND_CARAVAN && row.Kind != c.TradeRequestKind_TRADE_REQUEST_KIND_ORBITAL || row.ArrivalTick == nil || row.GetArrivalTick() < 0 || row.RetryTicks != nil && row.GetRetryTicks() < 0 {
			return contract("invalid trade request arrival")
		}
	}
	for _, row := range v.CommsWork {
		if row == nil || validID(row.GetNegotiatorId()) != nil || validID(row.GetConsoleId()) != nil || validID(row.GetFactionId()) != nil || row.JobId == nil || row.GetJobId() < 0 {
			return contract("invalid queued trade comms work")
		}
	}
	for _, row := range v.Requests {
		if row == nil || validID(row.GetFactionId()) != nil || validID(row.GetTraderKind()) != nil || row.Kind != c.TradeRequestKind_TRADE_REQUEST_KIND_CARAVAN && row.Kind != c.TradeRequestKind_TRADE_REQUEST_KIND_ORBITAL {
			return contract("invalid trade request option")
		}
		key := row.GetFactionId() + "/" + row.Kind.String() + "/" + row.GetTraderKind()
		if seen[key] {
			return contract("duplicate trade request option")
		}
		seen[key] = true
		if row.GoodwillCost != nil && row.GetGoodwillCost() < 0 || row.CooldownRemainingTicks != nil && row.GetCooldownRemainingTicks() < 0 || row.ArrivalMinTicks != nil && row.GetArrivalMinTicks() < 0 || row.ArrivalMaxTicks != nil && row.GetArrivalMaxTicks() < row.GetArrivalMinTicks() || row.Goodwill != nil && (row.GetGoodwill() < -100 || row.GetGoodwill() > 100) {
			return contract("invalid trade request economics")
		}
		if row.RelationAfterPayment != nil && row.GetRelationAfterPayment() != "Ally" && row.GetRelationAfterPayment() != "Neutral" && row.GetRelationAfterPayment() != "Hostile" {
			return contract("invalid trade request relation")
		}
		if err := tradeNegotiatorIDs(row.NegotiatorIds); err != nil {
			return err
		}
		if row.GetEligible() && (row.GoodwillCost == nil || row.RelationAfterPayment == nil || row.GetRelationAfterPayment() != "Ally" || row.CooldownRemainingTicks == nil || row.GetCooldownRemainingTicks() != 0 || row.LastRequestTick == nil || len(row.NegotiatorIds) == 0 || len(v.Consoles) == 0 || row.Kind == c.TradeRequestKind_TRADE_REQUEST_KIND_ORBITAL && (v.OrbitalAvailable == nil || !v.GetOrbitalAvailable() || len(v.PassingShips) != 0)) {
			return contract("unsafe trade request eligibility")
		}
	}
	if pack := v.Pack; pack != nil {
		for _, value := range []*float64{pack.MassUsage, pack.MassCapacity, pack.FoodDays, pack.FoodRotDays} {
			if value != nil && (math.IsNaN(*value) || math.IsInf(*value, 0) || *value < 0) {
				return contract("invalid trade pack estimate")
			}
		}
		for _, route := range []*o.WorldRoute{pack.Outbound, pack.Home} {
			if route != nil && (route.Destination == nil || route.GetDestination() < 0 || route.EstimatedTicks != nil && route.GetEstimatedTicks() < 0) {
				return contract("invalid trade pack route")
			}
		}
	}
	return nil
}

func tradeNegotiatorIDs(ids []string) error {
	seen := map[string]bool{}
	for _, id := range ids {
		if validID(id) != nil || seen[id] {
			return contract("invalid trade negotiator IDs")
		}
		seen[id] = true
	}
	return nil
}
