package bridge

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestTradeTargetsRemainDistinct(t *testing.T) {
	base, err := domain.NewTradeOpen("seller", "negotiator", false)
	if err != nil {
		t.Fatal(err)
	}
	for _, p := range []domain.TradeParticipant{
		{Kind: domain.TradeParticipantMap, ID: "seller"},
		{Kind: domain.TradeParticipantSettlement, ID: "seller", Caravan: "crew"},
		{Kind: domain.TradeParticipantOrbital, ID: "seller"},
	} {
		t.Run(string(p.Kind), func(t *testing.T) {
			trade, err := base.WithParticipant(p)
			if err != nil {
				t.Fatal(err)
			}
			action, err := domain.NewTradeAction("a", trade)
			if err != nil {
				t.Fatal(err)
			}
			wire, err := tradeAction(action)
			if err != nil {
				t.Fatal(err)
			}
			target := wire.GetTrade().Target
			if err := ValidateTradeTarget(target, pbIdentity()); err != nil {
				t.Fatal(err)
			}
			if p.Kind != domain.TradeParticipantMap && target.GetMapTrader() != nil {
				t.Fatal("non-pawn target confused with map trader")
			}
			session, _, err := tradeSessionClient(t, &o.TradeSession{Context: pbContext(), Target: target, NegotiatorId: proto.String("negotiator"), Open: proto.Bool(true)}).ReadTradeSession(context.Background(), pbIdentity())
			if err != nil || !proto.Equal(session.Target, target) {
				t.Fatal("session target lost", session, err)
			}
			sheet := tradeSheetFixture(nil)
			sheet.Target = target
			client, _ := tradeSheetClient(t, sheet)
			observed, _, err := client.ReadTradeSheet(context.Background(), pbIdentity())
			if err != nil || !proto.Equal(observed.Target, target) {
				t.Fatal("sheet target lost", observed, err)
			}
		})
	}
	for _, target := range []*c.TradeTarget{nil, {}, {Kind: &c.TradeTarget_Settlement{Settlement: &c.SettlementTradeTarget{SettlementId: proto.String("s")}}}, {Kind: &c.TradeTarget_OrbitalShip{OrbitalShip: &c.OrbitalTradeTarget{ShipId: proto.String("")}}}} {
		if ValidateTradeTarget(target, pbIdentity()) == nil {
			t.Fatal("invalid participant accepted", target)
		}
	}
	other := pbContext()
	other.Identity.LoadToken = proto.String("other-load")
	_, _, err = tradeSessionClient(t, &o.TradeSession{Context: other, Target: mapTradeTarget("seller"), NegotiatorId: proto.String("negotiator")}).ReadTradeSession(context.Background(), pbIdentity())
	if err == nil {
		t.Fatal("wrong-world trade target accepted")
	}
}

func acquisitionFixture() *o.TradeAcquisition {
	return &o.TradeAcquisition{Context: pbContext(), OrbitalAvailable: proto.Bool(true),
		Consoles: []*o.TradeConsole{{Id: proto.String("console"), NegotiatorIds: []string{"negotiator"}}},
		Requests: []*o.TradeRequestOption{{FactionId: proto.String("faction"), Kind: c.TradeRequestKind_TRADE_REQUEST_KIND_ORBITAL, TraderKind: proto.String("kind"), Goodwill: proto.Int32(80), GoodwillCost: proto.Int32(30), RelationAfterPayment: proto.String("Ally"), LastRequestTick: proto.Int64(-900000), CooldownRemainingTicks: proto.Int64(0), Eligible: proto.Bool(true), NegotiatorIds: []string{"negotiator"}}},
	}
}

func TestTradeRequestAdmissionFacts(t *testing.T) {
	for _, tc := range []struct {
		name   string
		change func(*o.TradeAcquisition)
	}{
		{"existing non-trader ship", func(v *o.TradeAcquisition) {
			v.PassingShips = []*o.PassingTradeShip{{Id: proto.String("ship"), Trader: proto.Bool(false)}}
		}},
		{"alliance spent", func(v *o.TradeAcquisition) { v.Requests[0].RelationAfterPayment = proto.String("Neutral") }},
		{"cooldown", func(v *o.TradeAcquisition) { v.Requests[0].CooldownRemainingTicks = proto.Int64(1) }},
		{"unknown cost", func(v *o.TradeAcquisition) { v.Requests[0].GoodwillCost = nil }},
		{"missing console", func(v *o.TradeAcquisition) { v.Consoles = nil }},
		{"DLC unavailable", func(v *o.TradeAcquisition) { v.OrbitalAvailable = proto.Bool(false) }},
		{"wrong world", func(v *o.TradeAcquisition) { v.Context.Identity.ColonyId = proto.String("other") }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			v := acquisitionFixture()
			tc.change(v)
			if validateTradeAcquisition(v, pbIdentity()) == nil {
				t.Fatal("unsafe or mismatched facts accepted")
			}
		})
	}
	v := acquisitionFixture()
	if err := validateTradeAcquisition(v, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	v.Requests[0].Eligible = nil
	v.Requests[0].GoodwillCost = nil
	v.Requests[0].RelationAfterPayment = nil
	if err := validateTradeAcquisition(v, pbIdentity()); err != nil {
		t.Fatal("explicit unknown facts rejected", err)
	}
}

func TestTradeAcquisitionReadPreservesUnknown(t *testing.T) {
	v := acquisitionFixture()
	v.Requests[0].Eligible = nil
	v.Requests[0].GoodwillCost = nil
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		calls++
		if arg.Tool != tradeAcquisitionTool {
			t.Fatal("unexpected native write", arg.Tool)
		}
		return pbResult(&o.TradeAcquisitionReply{Outcome: &o.TradeAcquisitionReply_Observed{Observed: v}}), nil
	}}, testBudget)
	got, _, err := client.ReadTradeAcquisition(context.Background(), pbIdentity(), nil)
	if err != nil {
		t.Fatal(err)
	}
	if calls != 1 || got.Requests[0].GoodwillCost != nil || got.Requests[0].Eligible != nil {
		t.Fatal("unknown facts lost")
	}
}
