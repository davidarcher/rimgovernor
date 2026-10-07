package clearance

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func TestRecoveryCasesRegistered(t *testing.T) {
	for _, name := range []string{"clearance/salvage-remote", "clearance/recovery-covered", "clearance/recovery-cluster",
		"clearance/recovery-loot-covered", "clearance/recovery-hostile", "clearance/recovery-foreign-ids"} {
		if _, ok := cases.Lookup(name); !ok {
			t.Errorf("case %s is not registered", name)
		}
	}
}

func TestGoBuiltIDMatchesNativeForm(t *testing.T) {
	if id, err := goBuiltID("Thing_Battery123", "Battery"); err != nil || id != "Thing_Battery123" {
		t.Fatalf("got %q, %v", id, err)
	}
	for _, bad := range []string{"Thing_123", "Thing_Batteryx", "Battery123"} {
		if _, err := goBuiltID(bad, "Battery"); err == nil {
			t.Errorf("%q accepted", bad)
		}
	}
}

func TestClusterDoneNeedsEveryRuinGoneAndDelivery(t *testing.T) {
	gone := map[string]any{"present": false}
	for name, audit := range map[string]map[string]any{
		"done":     {"cluster": []any{gone, gone}, "delivered": 5.0},
		"standing": {"cluster": []any{gone, map[string]any{"present": true}}, "delivered": 5.0},
		"no_yield": {"cluster": []any{gone}, "delivered": 0.0},
		"no_ruins": {"delivered": 5.0},
	} {
		if got := clusterDone(audit); got != (name == "done") {
			t.Errorf("%s: %v", name, got)
		}
	}
}
