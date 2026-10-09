package food

import (
	"context"
	"database/sql"
	"fmt"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
)

func TestRulesJournaledIncludesRetiredCompletedAttachments(t *testing.T) {
	ctx := context.Background()
	path := storetest.Path(t)
	st, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer st.Close()
	rule := domain.Rule{ID: chainID, Trigger: domain.RulePreyKilled, Predicates: []domain.RulePredicate{domain.RuleActorUndrafted, domain.RuleActorHuntingWorkActive, domain.RuleTargetAvailable}, Action: domain.RuleGiveJob, Job: "Hunt", Target: domain.RuleNearestDesignatedPrey, Radius: 80, PredatorMarginCells: 25}
	for i, tick := range []domain.Tick{56, 1306} {
		id := domain.PlanID(fmt.Sprintf("plan-%d", i))
		actionID := domain.ActionID(fmt.Sprintf("action-%d", i))
		attach, err := domain.NewRulesAttach([]domain.Rule{rule}, policy.RuleLeaseTicks)
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewRulesAttachAction(actionID, attach)
		if err != nil {
			t.Fatal(err)
		}
		plan, err := domain.NewPlan(id, 1, []domain.Action{action})
		if err != nil {
			t.Fatal(err)
		}
		if err = st.CreatePlan(ctx, plan); err != nil {
			t.Fatal(err)
		}
		if err = st.SeedPlanMethod(ctx, id, domain.MethodID(fmt.Sprintf("rules-%012d", tick))); err != nil {
			t.Fatal(err)
		}
		scope := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: id, Revision: 1}
		if _, err = st.Prepare(ctx, id, actionID, scope, tick); err != nil {
			t.Fatal(err)
		}
		progress, err := st.Dispatch(ctx, id, actionID, scope, tick)
		if err != nil {
			t.Fatal(err)
		}
		if _, err = st.RecordReceipt(ctx, id, actionID, progress.View().Attempt, domain.ReceiptAccepted); err != nil {
			t.Fatal(err)
		}
	}
	// Retirement removes settled methods from the active catalog, not history.
	raw, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	defer raw.Close()
	if _, err = raw.ExecContext(ctx, "UPDATE plans SET retired=1"); err != nil {
		t.Fatal(err)
	}
	if active, err := st.LoadPlans(ctx); err != nil || len(active) != 0 {
		t.Fatalf("active=%v err=%v", active, err)
	}
	ticks, err := rulesJournaled(ctx, st)
	if err != nil || !reflect.DeepEqual(ticks, []int64{56, 1306}) {
		t.Fatalf("ticks=%v err=%v", ticks, err)
	}
}
