package bridge

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// A policy prune builds one PolicyPruneIntent with its database and
// the canonical ids.
func TestPolicyPruneBuildsIntent(t *testing.T) {
	for db, want := range policyDatabases {
		value, err := domain.NewPolicyPrune(db, []string{"B_2", "A_1"})
		if err != nil {
			t.Fatal(err)
		}
		action, err := domain.NewPolicyPruneAction("p1", value)
		if err != nil {
			t.Fatal(err)
		}
		if !action.Kind().IntentMode() {
			t.Fatal("policy prune is not an intent kind")
		}
		wire, err := IntentAction("plan/1", action)
		if err != nil {
			t.Fatal(err)
		}
		if p := wire.GetPolicyPrune(); p.GetDatabase() != want || want == o.PolicyDatabase_POLICY_DATABASE_UNSPECIFIED || !slices.Equal(p.GetDeleteIds(), []string{"A_1", "B_2"}) {
			t.Fatalf("%v", wire)
		}
	}
}
