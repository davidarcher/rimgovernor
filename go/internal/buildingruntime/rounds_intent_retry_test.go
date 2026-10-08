package buildingruntime

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestIntentFailureBudgetExcludesSuccessfulWrites(t *testing.T) {
	reviewer, db, _, _, _ := roundsFixture(t)
	ctx := context.Background()
	var methods []domain.Method
	const prefix = "train-warg-Release-"
	add := func(receipt domain.Receipt, methodPrefix string, episode uint64) {
		t.Helper()
		id := domain.PlanID(fmt.Sprintf("retry-plan-%d", len(methods)))
		value, err := domain.NewHusbandry("warg", domain.HusbandryTrain, "Release")
		if err != nil {
			t.Fatal(err)
		}
		var actions []domain.Action
		// Several refused actions in one method count as one failed method.
		for i := 0; i < 2; i++ {
			action, err := domain.NewHusbandryAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
			if err != nil {
				t.Fatal(err)
			}
			actions = append(actions, action)
		}
		plan, err := domain.NewPlan(id, 1, actions)
		if err != nil {
			t.Fatal(err)
		}
		if err = db.CreatePlan(ctx, plan); err != nil {
			t.Fatal(err)
		}
		snapshot := reviewer.player.session.State().Snapshot
		snapshot.Plan, snapshot.Revision = id, 1
		for _, action := range actions {
			if _, err = db.Prepare(ctx, id, action.ID(), snapshot, 7); err != nil {
				t.Fatal(err)
			}
			if _, err = db.Dispatch(ctx, id, action.ID(), snapshot, 7); err != nil {
				t.Fatal(err)
			}
			if _, err = db.RecordReceipt(ctx, id, action.ID(), 1, receipt); err != nil {
				t.Fatal(err)
			}
		}
		methods = append(methods, domain.Method{Episode: episode, Method: domain.MethodID(fmt.Sprintf("%s%d", methodPrefix, len(methods))), Plan: id})
	}
	for i := 0; i < 10; i++ {
		add(domain.ReceiptAccepted, prefix, 1)
	}
	add(domain.ReceiptRefused, "train-warg-Obedience-", 1)
	add(domain.ReceiptRefused, prefix, 2)
	if failures, err := failedIntentMethods(ctx, db, methods, 1, prefix); err != nil || failures != 0 {
		t.Fatal("successful or unrelated writes spent retries", failures, err)
	}
	for i := 0; i < maxFailedIntentMethods; i++ {
		add(domain.ReceiptRefused, prefix, 1)
	}
	if failures, err := failedIntentMethods(ctx, db, methods, 1, prefix); err != nil || failures != maxFailedIntentMethods {
		t.Fatal("real refusals did not reach the bounded budget", failures, err)
	}
	if count := medicalAttemptCount(methods, 1, prefix); count != 10+maxFailedIntentMethods {
		t.Fatal("method identities must count successful writes too", count)
	}
}
