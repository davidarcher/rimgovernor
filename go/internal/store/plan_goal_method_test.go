package store

import (
	"context"
	"testing"
)

func TestPlanMethodRoundTripsReason(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	g, e := s.CommitMethodReason(ctx, g.Standard.ID, g.Revision, "shell", "room for 2 unhoused", plan(t, "reasoned", "reasoned-action"))
	if e != nil {
		t.Fatal(e)
	}
	s.Close()
	s = open(t, path)
	m, ok, e := s.PlanMethod(ctx, "reasoned")
	if e != nil || !ok || m.Method != "shell" || m.Reason != "room for 2 unhoused" {
		t.Fatal(m, ok, e)
	}
	bare, _, h := goalFixture(t)
	if _, e = bare.CommitMethod(ctx, h.Standard.ID, h.Revision, "bare", plan(t, "bare", "bare-action")); e != nil {
		t.Fatal(e)
	}
	if m, ok, e = bare.PlanMethod(ctx, "bare"); e != nil || !ok || m.Reason != "" {
		t.Fatal(m, ok, e)
	}
	if _, ok, e = s.PlanMethod(ctx, "absent"); e != nil || ok {
		t.Fatal(ok, e)
	}
}
