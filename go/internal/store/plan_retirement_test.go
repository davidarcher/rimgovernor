package store

import (
	"context"
	"errors"
	"fmt"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRoutinePlanRetirementRepeatedMethodsAndHistory(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "history.db")
	s := open(t, path)
	r := routineRequest()
	g := routineGoal(t, reviewRoutine(t, s, &r), policy.MaintainWood)
	for i := 0; i < 260; i++ {
		id := fmt.Sprintf("method-%03d", i)
		p := plan(t, domain.PlanID(id), domain.ActionID(id+"-a"))
		committed, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, domain.MethodID(id), p)
		if err != nil {
			t.Fatal(i, err)
		}
		if _, err := s.Cancel(ctx, p.ID(), p.Actions()[0].ID()); err != nil {
			t.Fatal(err)
		}
		if i == 0 {
			r.Tick = g.Goal.Tick
		}
		g = routineGoal(t, reviewRoutine(t, s, &r), policy.MaintainWood)
		if i == 0 {
			if g.Revision <= committed.Revision {
				t.Fatal("same-tick retirement retained stale revision")
			}
			if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, committed.Revision, "stale", plan(t, "stale", "stale-a")); !errors.Is(err, ErrConflict) {
				t.Fatal("stale goal revision admitted", err)
			}
		}
		if len(g.Methods) != 0 {
			t.Fatal("settled method retained", i)
		}
	}
	s.Close()
	s = open(t, path)
	active, err := s.LoadPlans(ctx, 1)
	if err != nil || len(active) != 0 {
		t.Fatal(active, err)
	}
	m, err := s.LoadGoalMethod(ctx, g.Goal.ID, 0, "method-000")
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
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "method-000", plan(t, "duplicate", "duplicate-a")); err == nil {
		t.Fatal("retired method identity reused")
	}
	if _, err = s.Cancel(ctx, p.Spec.ID(), p.Spec.Actions()[0].ID()); err == nil {
		t.Fatal("retired plan changed")
	}
}

func TestRoutinePlanRetirementTerminalFloorAndRestart(t *testing.T) {
	t.Parallel()
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		for _, cancelled := range []bool{false, true} {
			t.Run(fmt.Sprintf("%s/%v", effect, cancelled), func(t *testing.T) {
				ctx := context.Background()
				path := filepath.Join(t.TempDir(), "history.db")
				s := open(t, path)
				r := routineRequest()
				g := routineGoal(t, reviewRoutine(t, s, &r), policy.MaintainWood)
				q := methodRequest(t, g, "old", 100)
				d, err := s.AdmitBuildingMethod(ctx, q)
				if err != nil || !d.Admitted {
					t.Fatal(d, err)
				}
				p, err := s.LoadPlan(ctx, q.Plan.ID())
				if err != nil {
					t.Fatal(err)
				}
				action := q.Plan.Actions()[0].ID()
				if _, err = s.ReserveAndPrepare(ctx, q.Plan.ID(), action, p.Admissions[0].Admission); err != nil {
					t.Fatal(err)
				}
				if _, err = s.Dispatch(ctx, q.Plan.ID(), action, q.Current, q.Tick); err != nil {
					t.Fatal(err)
				}
				if cancelled {
					if _, err = s.CancelGoal(ctx, g.Goal.ID, d.Goal.Revision); err != nil {
						t.Fatal(err)
					}
				}
				observation := domain.Observation{Action: action, Attempt: 1, Snapshot: q.Current, Tick: 15, Effect: effect}
				if effect == domain.EffectUnsuccessful {
					observation.UnsuccessfulReason = domain.NativeFailure
				}
				if _, err = s.Observe(ctx, q.Plan.ID(), observation, q.Current); err != nil {
					t.Fatal(err)
				}
				prepared := plan(t, "prepared", "prepared-a")
				if err = s.CreatePlan(ctx, prepared); err != nil {
					t.Fatal(err)
				}
				preparedScope := scope()
				preparedScope.Plan = prepared.ID()
				if _, err = s.Prepare(ctx, prepared.ID(), "prepared-a", preparedScope, 14); err != nil {
					t.Fatal(err)
				}
				r.Tick = 14
				reviewRoutine(t, s, &r)
				p, err = s.LoadPlan(ctx, q.Plan.ID())
				if err != nil || p.Retired {
					t.Fatal("future evidence retired", p, err)
				}
				r.Tick = 20
				if _, err = s.db.ExecContext(ctx, `CREATE TRIGGER fail_retirement BEFORE UPDATE ON routine_review BEGIN SELECT RAISE(ABORT,'review failure'); END`); err != nil {
					t.Fatal(err)
				}
				if _, err = s.ReviewRoutine(ctx, r); err == nil {
					t.Fatal("injected retirement failure ignored")
				}
				var floors int
				if err = s.db.QueryRowContext(ctx, "SELECT count(*) FROM retirement_floors").Scan(&floors); err != nil || floors != 0 {
					t.Fatal("floor escaped rollback", floors, err)
				}
				if _, err = s.db.ExecContext(ctx, "DROP TRIGGER fail_retirement"); err != nil {
					t.Fatal(err)
				}
				reviewRoutine(t, s, &r)
				s.Close()
				s = open(t, path)
				p, err = s.LoadPlan(ctx, q.Plan.ID())
				if err != nil || !p.Retired || len(p.Admissions) != 1 {
					t.Fatal(p, err)
				}
				if _, err = s.Dispatch(ctx, prepared.ID(), "prepared-a", preparedScope, 14); err == nil {
					t.Fatal("prepared dispatch bypassed retirement floor")
				}
				if _, err = s.Cancel(ctx, prepared.ID(), "prepared-a"); err != nil {
					t.Fatal(err)
				}
				other := anotherGoal(t, s, "replacement")
				next := methodRequest(t, other, "new", 100)
				next.Tick, next.Stock.Tick, next.Previews[0].Tick = 14, 14, 14
				if _, err = s.AdmitBuildingMethod(ctx, next); err == nil {
					t.Fatal("retirement made old stock spendable")
				}
				standalone := plan(t, "standalone", "standalone-a")
				if err = s.CreatePlan(ctx, standalone); err != nil {
					t.Fatal(err)
				}
				current := scope()
				current.Plan = standalone.ID()
				if _, err = s.Prepare(ctx, standalone.ID(), "standalone-a", current, 14); err == nil {
					t.Fatal("standalone preparation bypassed floor")
				}
				current.Load = "replacement-load"
				if _, err = s.Prepare(ctx, standalone.ID(), "standalone-a", current, 14); err != nil {
					t.Fatal("floor crossed world identity", err)
				}
				if _, err = s.Cancel(ctx, standalone.ID(), "standalone-a"); err != nil {
					t.Fatal(err)
				}
				next.Tick, next.Stock.Tick, next.Previews[0].Tick = 20, 20, 20
				if d, err = s.AdmitBuildingMethod(ctx, next); err != nil || !d.Admitted {
					t.Fatal("fresh stock blocked", d, err)
				}
			})
		}
	}
}

func TestRoutinePlanRetirementPinsCurrentAndRollsBack(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "history.db"))
	r := routineRequest()
	g := routineGoal(t, reviewRoutine(t, s, &r), policy.MaintainWood)
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "current", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "p", "a"); err != nil {
		t.Fatal(err)
	}
	reviewRoutine(t, s, &r)
	p, err := s.LoadPlan(ctx, "p")
	if err != nil || p.Retired {
		t.Fatal("current plan retired", p, err)
	}
	if _, err = s.db.ExecContext(ctx, `CREATE TRIGGER fail_review BEFORE UPDATE ON routine_review BEGIN SELECT RAISE(ABORT,'review failure'); END`); err != nil {
		t.Fatal(err)
	}
	r.Current.Plan = "other"
	if _, err = s.ReviewRoutine(ctx, r); err == nil {
		t.Fatal("injected failure ignored")
	}
	p, err = s.LoadPlan(ctx, "p")
	if err != nil || p.Retired {
		t.Fatal("retirement escaped transaction", p, err)
	}
}

func TestRoutinePlanRetirementPinsUnfinishedAndPlayerMethods(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"dependency", "unknown", "player"} {
		t.Run(kind, func(t *testing.T) {
			ctx := context.Background()
			s := open(t, filepath.Join(t.TempDir(), "history.db"))
			r := routineRequest()
			g := routineGoal(t, reviewRoutine(t, s, &r), policy.MaintainWood)
			if kind == "player" {
				g = anotherGoal(t, s, "player-goal")
			}
			p := plan(t, "method", "method-a", "method-b")
			if kind == "dependency" {
				var err error
				p, err = domain.NewPlan(p.ID(), p.Revision(), p.Actions(), domain.ActionDependency{Action: "method-b", Requires: "method-a"})
				if err != nil {
					t.Fatal(err)
				}
			}
			if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "method", p); err != nil {
				t.Fatal(err)
			}
			if kind == "player" {
				for _, a := range p.Actions() {
					if _, err := s.Cancel(ctx, p.ID(), a.ID()); err != nil {
						t.Fatal(err)
					}
				}
			} else {
				current := scope()
				current.Plan = p.ID()
				if _, err := s.Prepare(ctx, p.ID(), "method-a", current, 10); err != nil {
					t.Fatal(err)
				}
				if _, err := s.Dispatch(ctx, p.ID(), "method-a", current, 10); err != nil {
					t.Fatal(err)
				}
				ob := domain.Observation{Action: "method-a", Attempt: 1, Snapshot: current, Tick: 11, Effect: domain.EffectCompleted}
				if kind == "unknown" {
					ob.Effect = domain.EffectUnknown
				}
				if _, err := s.Observe(ctx, p.ID(), ob, current); err != nil {
					t.Fatal(err)
				}
				if kind != "dependency" {
					if _, err := s.Cancel(ctx, p.ID(), "method-b"); err != nil {
						t.Fatal(err)
					}
				}
			}
			r.Tick = 20
			reviewRoutine(t, s, &r)
			got, err := s.LoadPlan(ctx, p.ID())
			if err != nil || got.Retired {
				t.Fatal("pinned work retired", kind, got, err)
			}
		})
	}
}
