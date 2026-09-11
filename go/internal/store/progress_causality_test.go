package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCausalTerminalOutcomesReplayAfterRestart(t *testing.T) {
	ctx := context.Background()
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			s, path := fixture(t)
			prepare(t, s, "a")
			if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
				t.Fatal(err)
			}
			if _, err := s.RecordReceipt(ctx, "p", "a", 1, domain.ReceiptUnknown); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "a", Attempt: 1, Snapshot: scope(), Tick: 10, Effect: effect, Causality: domain.AfterDispatch}
			if effect == domain.EffectUnsuccessful {
				observation.UnsuccessfulReason = domain.OutcomeNotAchieved
			}
			if err := s.Close(); err != nil {
				t.Fatal(err)
			}
			s = open(t, path)
			got, err := s.Observe(ctx, "p", observation, scope())
			if err != nil {
				t.Fatal(err)
			}
			if err := s.Close(); err != nil {
				t.Fatal(err)
			}
			s = open(t, path)
			loaded, err := s.LoadPlan(ctx, "p")
			if err != nil {
				t.Fatal(err)
			}
			if loaded.Progress[0].View() != got.View() || got.View().Unresolved {
				t.Fatal("terminal outcome did not replay")
			}
			if _, err := s.Prepare(ctx, "p", "a", scope(), 11); err == nil {
				t.Fatal("terminal outcome reopened")
			}
			if _, err := s.RecordReceipt(ctx, "p", "a", 1, domain.ReceiptAccepted); err == nil {
				t.Fatal("late receipt changed terminal state")
			}
		})
	}
}
