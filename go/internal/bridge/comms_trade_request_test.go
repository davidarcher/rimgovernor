package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"testing"
)

func TestCommsTradeRequestClosedWireAndObservableReceipt(t *testing.T) {
	for _, kind := range []domain.TradeRequestKind{domain.TradeRequestCaravan, domain.TradeRequestOrbital} {
		request := domain.CommsTradeRequest{Kind: kind, Faction: "faction", TraderKind: "bulk", Console: "console", Negotiator: "pawn", ExpectedLastRequestTick: -900000}
		action, err := domain.NewCommsTradeRequestAction("request", request)
		if err != nil {
			t.Fatal(err)
		}
		wire, err := IntentAction("request/1", action)
		if err != nil {
			t.Fatal(err)
		}
		got := wire.GetCommsTradeRequest()
		if got == nil || got.GetFactionId() != request.Faction || got.GetTraderKind() != request.TraderKind || got.GetConsoleId() != request.Console || got.GetNegotiatorId() != request.Negotiator || got.GetExpectedLastRequestTick() != request.ExpectedLastRequestTick {
			t.Fatal(got)
		}
		orbital := got.Kind == c.TradeRequestKind_TRADE_REQUEST_KIND_ORBITAL
		if orbital != (kind == domain.TradeRequestOrbital) || action.Kind().IntentMode() {
			t.Fatal(got, action.Kind().IntentMode())
		}
	}
}
