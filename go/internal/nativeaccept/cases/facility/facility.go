// Package facility holds issue #4's facility case: a room the game itself
// scores as a hospital, staged by the autonomous service and
// audited against live native facts rather than the journal alone. The
// comfort and workshop cases became colony snapshot tests (#750, #738,
// buildingruntime/routine_facility_snapshot_test.go and
// routine_workshop_snapshot_test.go).
package facility

import (
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// window is how long a facility goal gets to recover; the window ends
// early on recovery.
const window = 12 * time.Minute

// spec is the serve spec the facility cases share, over families.
func spec(prefix, families string, extra ...string) *cases.ServeSpec {
	return &cases.ServeSpec{Families: []string{families}, NativeTimeout: 15 * time.Second, Prefix: prefix, Extra: extra}
}

// goalRecovered reports whether a watch sample's goal is recovered and
// satisfied.
func goalRecovered(sample map[string]any) bool {
	need, _ := sample["need"].(string)
	status, _ := sample["status"].(string)
	return domain.NeedState(need) == domain.NeedRecovered && domain.GoalStatus(status) == domain.GoalSatisfied
}
