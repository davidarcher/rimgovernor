package buildingruntime

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
)

// A plan that is neither a bound routine method nor a player submission is
// refused as ErrUnauthorizedPlan: still store.ErrConflict for every caller
// that matches the sentinel, but named as an authorization refusal rather
// than the sentinel's identity-collision text, which the worker's debug log
// otherwise reports for every retired or recovered method (#214).
func TestPlanAuthorizerNamesTheRefusal(t *testing.T) {
	db, err := store.Open(context.Background(), storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	root := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 7}
	target := root
	target.Plan, target.Revision = "routine-defense-unknown", 1
	for _, routine := range []bool{false, true} {
		err := planAuthorizer{db, routine}.AuthorizeRoutinePlan(context.Background(), root, target)
		if !errors.Is(err, ErrUnauthorizedPlan) || !errors.Is(err, store.ErrConflict) {
			t.Fatalf("routine=%v: err=%v, want ErrUnauthorizedPlan wrapping store.ErrConflict", routine, err)
		}
		if strings.Contains(err.Error(), "identity already exists") && !strings.Contains(err.Error(), "not authorized") {
			t.Fatalf("routine=%v: refusal reads as an identity collision: %v", routine, err)
		}
	}
}
