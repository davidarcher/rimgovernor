package store

import (
	"context"
	"errors"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// TestRoundsPlanRetirementNoDoubleSpend replays the double-spend the deleted
// retirement floor blocked: once a settled plan retires, stock observed
// before its evidence must not buy a second method. The goal tick check refuses
// it without the floor, and the same stock at a fresh tick still admits.
func TestRoundsPlanRetirementNoDoubleSpend(t *testing.T) {
	t.Parallel()
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			path := memoryPath(t)
			s := open(t, path)
			r := roundsRequest()
			g := roundsGoal(t, reviewRounds(t, s, &r), policy.MaintainResource)
			q := methodRequest(t, g, "old", 100)
			d, err := s.AdmitBuildingMethod(ctx, q)
			if err != nil || !d.Admitted {
				t.Fatal(d, err)
			}
			action := q.Plan.Actions()[0].ID()
			if _, err = s.Prepare(ctx, q.Plan.ID(), action, q.Current, q.Tick); err != nil {
				t.Fatal(err)
			}
			if _, err = s.Dispatch(ctx, q.Plan.ID(), action, q.Current, 15); err != nil {
				t.Fatal(err)
			}
			receipt := domain.ReceiptAccepted
			if effect == domain.EffectUnsuccessful {
				receipt = domain.ReceiptRefused
			}
			if _, err = s.RecordReceipt(ctx, q.Plan.ID(), action, 1, receipt); err != nil {
				t.Fatal(err)
			}
			built, _ := q.Plan.Actions()[0].Building()
			r.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "wall", Building: built, Cells: []domain.Cell{built.Cell()}}}})
			r.Tick = 20
			reviewRounds(t, s, &r)
			s.Close()
			s = open(t, path)
			p, err := s.LoadPlan(ctx, q.Plan.ID())
			if err != nil || !p.Retired {
				t.Fatal("plan not retired", p, err)
			}
			other := anotherGoal(t, s, "replacement")
			next := methodRequest(t, other, "new", 100)
			next.Tick, next.Stock.Tick, next.Previews[0].Tick = 14, 14, 14
			if d, err = s.AdmitBuildingMethod(ctx, next); err == nil && d.Admitted {
				t.Fatal("retirement made old stock spendable", d)
			}
			next.Tick, next.Stock.Tick, next.Previews[0].Tick = 20, 20, 20
			if d, err = s.AdmitBuildingMethod(ctx, next); err != nil || !d.Admitted {
				t.Fatal("fresh stock blocked", d, err)
			}
		})
	}
}

func TestRoundsPlanRetirementRepeatedMethodsAndHistory(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := roundsRequest()
	g := roundsGoal(t, reviewRounds(t, s, &r), policy.MaintainResource)
	for i := 0; i < 260; i++ {
		id := fmt.Sprintf("method-%03d", i)
		p := plan(t, domain.PlanID(id), domain.ActionID(id+"-a"))
		committed, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, domain.MethodID(id), p)
		if err != nil {
			t.Fatal(i, err)
		}
		if _, err := s.Cancel(ctx, p.ID(), p.Actions()[0].ID()); err != nil {
			t.Fatal(err)
		}
		if i == 0 {
			r.Tick--
		}
		g = roundsGoal(t, reviewRounds(t, s, &r), policy.MaintainResource)
		if i == 0 {
			if g.Revision <= committed.Revision {
				t.Fatal("same-tick retirement retained stale revision")
			}
			if _, err = s.CommitMethod(ctx, g.Standard.ID, committed.Revision, "stale", plan(t, "stale", "stale-a")); !errors.Is(err, ErrConflict) {
				t.Fatal("stale goal revision admitted", err)
			}
		}
		if len(g.Methods) != 0 {
			t.Fatal("settled method retained", i)
		}
	}
	s.Close()
	s = open(t, path)
	active, err := s.LoadPlans(ctx)
	if err != nil || len(active) != 0 {
		t.Fatal(active, err)
	}
	m, err := s.LoadMethod(ctx, g.Standard.ID, 0, "method-000")
	if err != nil || m.Plan != "method-000" {
		t.Fatal(m, err)
	}
	p, err := s.LoadPlan(ctx, m.Plan)
	if err != nil || !p.Retired || p.Progress[0].View().Stage != domain.Cancelled {
		t.Fatal(p, err)
	}
	if err = s.CreatePlan(ctx, p.Spec); err == nil {
		t.Fatal("retired plan identity reused")
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "method-000", plan(t, "duplicate", "duplicate-a")); err == nil {
		t.Fatal("retired method identity reused")
	}
	if _, err = s.Cancel(ctx, p.Spec.ID(), p.Spec.Actions()[0].ID()); err == nil {
		t.Fatal("retired plan changed")
	}
}

func TestRoundsPlanRetirementPinsCurrentAndRollsBack(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	g := roundsGoal(t, reviewRounds(t, s, &r), policy.MaintainResource)
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "current", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "p", "a"); err != nil {
		t.Fatal(err)
	}
	reviewRounds(t, s, &r)
	p, err := s.LoadPlan(ctx, "p")
	if err != nil || p.Retired {
		t.Fatal("current plan retired", p, err)
	}
	if _, err = s.db.ExecContext(ctx, `CREATE TRIGGER fail_review BEFORE UPDATE ON rounds BEGIN SELECT RAISE(ABORT,'review failure'); END`); err != nil {
		t.Fatal(err)
	}
	r.Current.Plan = "other"
	if _, err = s.ReviewRounds(ctx, r); err == nil {
		t.Fatal("injected failure ignored")
	}
	p, err = s.LoadPlan(ctx, "p")
	if err != nil || p.Retired {
		t.Fatal("retirement escaped transaction", p, err)
	}
}

func TestRoundsPlanRetirementPinsUnfinishedMethods(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"dependency", "unknown"} {
		t.Run(kind, func(t *testing.T) {
			ctx := context.Background()
			s := open(t, memoryPath(t))
			r := roundsRequest()
			g := roundsGoal(t, reviewRounds(t, s, &r), policy.MaintainResource)
			p := plan(t, "method", "method-a", "method-b")
			if kind == "dependency" {
				var err error
				p, err = domain.NewPlan(p.ID(), p.Revision(), p.Actions(), domain.ActionDependency{Action: "method-b", Requires: "method-a"})
				if err != nil {
					t.Fatal(err)
				}
			}
			if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "method", p); err != nil {
				t.Fatal(err)
			}
			current := scope()
			current.Plan = p.ID()
			if _, err := s.Prepare(ctx, p.ID(), "method-a", current, 10); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, p.ID(), "method-a", current, 10); err != nil {
				t.Fatal(err)
			}
			// An unknown intent receipt leaves it to be sent again.
			receipt := domain.ReceiptAccepted
			if kind == "unknown" {
				receipt = domain.ReceiptUnknown
			}
			if _, err := s.RecordReceipt(ctx, p.ID(), "method-a", 1, receipt); err != nil {
				t.Fatal(err)
			}
			if kind != "dependency" {
				if _, err := s.Cancel(ctx, p.ID(), "method-b"); err != nil {
					t.Fatal(err)
				}
			}
			r.Tick = 20
			reviewRounds(t, s, &r)
			got, err := s.LoadPlan(ctx, p.ID())
			if err != nil || got.Retired {
				t.Fatal("pinned work retired", kind, got, err)
			}
		})
	}
}
