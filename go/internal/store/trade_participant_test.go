package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestTradeParticipantJournalRoundTrip(t *testing.T) {
	for _, participant := range []domain.TradeParticipant{
		{Kind: domain.TradeParticipantMap, ID: "seller"},
		{Kind: domain.TradeParticipantSettlement, ID: "seller", Caravan: "caravan"},
		{Kind: domain.TradeParticipantOrbital, ID: "seller"},
	} {
		t.Run(string(participant.Kind), func(t *testing.T) {
			ctx := context.Background()
			s, path, g := goalFixture(t)
			trade, err := domain.NewTradeOpen("seller", "negotiator", false)
			if err != nil {
				t.Fatal(err)
			}
			trade, err = trade.WithParticipant(participant)
			if err != nil {
				t.Fatal(err)
			}
			action, err := domain.NewTradeAction("trade", trade)
			if err != nil {
				t.Fatal(err)
			}
			plan, err := domain.NewPlan("trade-plan", 1, []domain.Action{action})
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", plan); err != nil {
				t.Fatal(err)
			}
			s.Close()
			s = open(t, path)
			defer s.Close()
			loaded, err := s.LoadPlan(ctx, "trade-plan")
			if err != nil {
				t.Fatal(err)
			}
			got, ok := loaded.Spec.Actions()[0].Trade()
			if !ok || got != trade {
				t.Fatal("trade participant lost in journal", got, trade)
			}
		})
	}
}
