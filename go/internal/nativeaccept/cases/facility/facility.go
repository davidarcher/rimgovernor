// Package facility holds issue #4's facility cases: a room the game itself
// scores as a workshop or hospital, staged by the autonomous service and
// audited against live native facts rather than the journal alone. The
// comfort cases became colony snapshot tests (#750,
// buildingruntime/routine_facility_snapshot_test.go).
package facility

import (
	"context"
	"fmt"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// window is how long a facility goal gets to recover; the window ends
// early on recovery.
const window = 12 * time.Minute

// spec is the serve spec the facility cases share, over families.
func spec(prefix, families string, extra ...string) *cases.ServeSpec {
	return &cases.ServeSpec{Families: []string{families}, NativeTimeout: 15 * time.Second, Prefix: prefix, Extra: extra}
}

// openJournal reopens the stopped service's durable journal for the audit.
func openJournal(ctx context.Context, s cases.Session) (*store.Store, error) {
	journal, err := store.Open(ctx, filepath.Join(s.Config().Output, "service.sqlite"))
	if err != nil {
		return nil, fmt.Errorf("reopen journal: %w", err)
	}
	return journal, nil
}

// goalRecovered reports whether a watch sample's goal is recovered and
// satisfied.
func goalRecovered(sample map[string]any) bool {
	need, _ := sample["need"].(string)
	status, _ := sample["status"].(string)
	return domain.NeedState(need) == domain.NeedRecovered && domain.GoalStatus(status) == domain.GoalSatisfied
}
