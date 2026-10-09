package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestRulesWindowRequiresSuccessfulCurrentReceipt(t *testing.T) {
	for _, name := range []string{"accepted", "clear", "pending", "uncertain", "refused", "other-load", "other-authority"} {
		t.Run(name, func(t *testing.T) {
			s, n := schedulerFixture(t)
			s.config.Rules = &RoundsRulesPlanner{}
			s.catalog = nil
			s.config.Start.MaxTicks = 60000
			ctx := context.Background()
			db := s.player.journal
			set := huntChainSet(t)
			if name == "clear" {
				set.Rules = nil
			}
			attach, err := domain.NewRulesAttach(set.Rules, policy.RuleLeaseTicks)
			if err != nil {
				t.Fatal(err)
			}
			action, err := domain.NewRulesAttachAction("renewal", attach)
			if err != nil {
				t.Fatal(err)
			}
			plan, err := domain.NewPlan("rules-test", 1, []domain.Action{action})
			if err != nil {
				t.Fatal(err)
			}
			if err = db.CreatePlan(ctx, plan); err != nil {
				t.Fatal(err)
			}
			if err = db.SeedPlanMethod(ctx, plan.ID(), "rules-000000000011"); err != nil {
				t.Fatal(err)
			}
			current := s.session.State().Snapshot
			scope := current
			scope.Plan = plan.ID()
			scope.Revision = 1
			if name == "other-load" {
				scope.Load = "other"
			}
			if name == "other-authority" {
				scope.Native++
			}
			tick := domain.Tick(n.status.Context.GetTick())
			if name != "pending" {
				if _, err = db.Prepare(ctx, plan.ID(), action.ID(), scope, tick); err != nil {
					t.Fatal(err)
				}
				progress, err := db.Dispatch(ctx, plan.ID(), action.ID(), scope, tick)
				if err != nil {
					t.Fatal(err)
				}
				if name != "uncertain" {
					receipt := domain.ReceiptAccepted
					if name == "refused" {
						receipt = domain.ReceiptRefused
					}
					if _, err = db.RecordReceipt(ctx, plan.ID(), action.ID(), progress.View().Attempt, receipt); err != nil {
						t.Fatal(err)
					}
				}
			}
			want := uint32(60000)
			if name == "accepted" {
				want = 1250
			}
			got, err := s.rulesWindow(ctx, current, tick, 60000)
			if err != nil || got != want {
				t.Fatalf("ticks=%d want=%d err=%v", got, want, err)
			}
			if name == "accepted" {
				out, err := s.Step(ctx)
				if err != nil || out.Attempt == nil || out.Attempt.Phase != store.ClockApplied || out.Attempt.Intent.Command.Start.MaxTicks != 1250 {
					t.Fatalf("scheduler window=%+v err=%v", out.Attempt, err)
				}
				got, err = s.rulesWindow(ctx, current, tick+1250, 60000)
				if err != nil || got != 60000 {
					t.Fatalf("passed boundary parks clock: %d %v", got, err)
				}
			}
		})
	}
}

func TestRulesDispatchTurnOnlyYieldsForNewAdmission(t *testing.T) {
	for _, v := range []Verdict{BuildingReasonAdmitted, fieldUnavailable("hunt_census"), BuildingReasonNoDeficit, waitFor(WaitMethodUsed, "rules_method"), BuildingReasonDisabled} {
		if got := rulesDispatchTurn(true, &RoundsRulesResult{Verdict: v}); got != (v == BuildingReasonAdmitted) {
			t.Fatalf("verdict=%+v deferred=%v", v, got)
		}
	}
	if rulesDispatchTurn(false, &RoundsRulesResult{Verdict: BuildingReasonAdmitted}) || rulesDispatchTurn(true, nil) {
		t.Fatal("yield without worker or admission")
	}
}
