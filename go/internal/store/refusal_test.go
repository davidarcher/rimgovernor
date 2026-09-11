package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestTrustedRefusalSurvivesReopen(t *testing.T) {
	ctx := context.Background()
	s, path := fixture(t)
	prepare(t, s, "a")
	if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "p", "a", 1, domain.ReceiptRefused); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	v := state.Progress[0].View()
	if v.Unresolved || v.Stage != domain.Pending || v.Attempt != 1 {
		t.Fatal(v)
	}
	if _, err = s.Prepare(ctx, "p", "a", scope(), 11); err != nil {
		t.Fatal(err)
	}
	next, err := s.Dispatch(ctx, "p", "a", scope(), 11)
	if err != nil || next.View().Attempt != 2 {
		t.Fatal(err)
	}
}
