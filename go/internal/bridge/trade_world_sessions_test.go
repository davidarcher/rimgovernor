package bridge

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestWorldTradeSessionAndSheetKeepInventoryOwner(t *testing.T) {
	targets := []*c.TradeTarget{
		{Kind: &c.TradeTarget_Settlement{Settlement: &c.SettlementTradeTarget{SettlementId: proto.String("town"), CaravanId: proto.String("crew-a")}}},
		{Kind: &c.TradeTarget_Settlement{Settlement: &c.SettlementTradeTarget{SettlementId: proto.String("town"), CaravanId: proto.String("crew-b")}}},
		{Kind: &c.TradeTarget_OrbitalShip{OrbitalShip: &c.OrbitalTradeTarget{ShipId: proto.String("ship")}}},
	}
	seen := map[string]bool{}
	for _, target := range targets {
		participant := TradeParticipantOf(target)
		if participant.Validate() != nil || seen[participant.Key()] {
			t.Fatal(participant)
		}
		seen[participant.Key()] = true
		for _, open := range []bool{false, true} {
			session := &o.TradeSession{Context: pbContext(), Target: target, NegotiatorId: proto.String("pawn"), Open: proto.Bool(open)}
			got, _, err := tradeSessionClient(t, session).ReadTradeSession(context.Background(), pbIdentity())
			if err != nil || got.Trader != participant.Key() || got.Open != open || TradeParticipantOf(got.Target) != participant {
				t.Fatalf("session: %+v %v", got, err)
			}
		}
		sheet := tradeSheetFixture([]*o.TradeLine{tradeSheetLine("line", "Steel", 5, 30)})
		sheet.Target = target
		client, _ := tradeSheetClient(t, sheet)
		got, _, err := client.ReadTradeSheet(context.Background(), pbIdentity())
		if err != nil || got.Trader != participant.Key() || TradeParticipantOf(got.Target) != participant || got.Rows[0].ColonyCount != 5 {
			t.Fatalf("sheet: %+v %v", got, err)
		}
		// Typed cancel uses the identical target and cannot become a map trader.
		cancel, err := domain.NewTradeEnd("placeholder", "pawn", domain.TradeEndCancel, false)
		if err != nil {
			t.Fatal(err)
		}
		cancel, err = cancel.WithParticipant(participant)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewTradeAction("cancel", cancel)
		if err != nil {
			t.Fatal(err)
		}
		wire, err := tradeAction(action)
		if err != nil || TradeParticipantOf(wire.GetTrade().Target) != participant {
			t.Fatalf("cancel: %v %v", wire, err)
		}
	}
}

func TestOrbitalCensusUsesSameScopedSellerAsLiveSheet(t *testing.T) {
	snapshot := &o.TradersSnapshot{Context: pbContext(), Traders: []*o.Trader{{
		Trader: &o.EntityRef{Id: proto.String("ship"), Position: &c.Cell{X: proto.Int32(0), Z: proto.Int32(0)}},
		Kind:   proto.String("Orbital_BulkGoods"), CanTrade: proto.Bool(true), Travelling: proto.Bool(false), Orbital: proto.Bool(true), GoodsStacks: proto.Uint32(12),
	}}}
	got, err := decodeTraders(snapshot, pbIdentity())
	if err != nil || len(got.Traders) != 1 {
		t.Fatalf("%+v %v", got, err)
	}
	row := got.Traders[0]
	if row.ID != "orbital/ship" || row.Participant != (domain.TradeParticipant{Kind: domain.TradeParticipantOrbital, ID: "ship"}) || row.GoodsStacks != 12 {
		t.Fatal(row)
	}
}
