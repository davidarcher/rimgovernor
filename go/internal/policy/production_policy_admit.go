package policy

import (
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ProductionPolicySatisfied mirrors ResearchProjectClaimed's shape: the
// native floors/stopped rows already match the desired replacement (someone
// else applied it, or a prior attempt already landed), so pushing the write
// again would be a needless native call rather than a real correction.
const ProductionPolicySatisfied Reason = "production_policy_satisfied"

// ProductionPolicyFacts is the fresh native production-policy state
// EvaluateProductionPolicy re-checks immediately before dispatch: the
// current Floors/Stopped rows (to confirm a real divergence still exists)
// plus the CAS snapshot token the write's ExpectedSnapshotToken must match.
type ProductionPolicyFacts struct {
	Snapshot domain.GenerationSnapshot
	Tick     domain.Tick
	Floors   domain.Fact[map[Resource]int64]
	Stopped  domain.Fact[[]Resource]
	Token    string
}

type ProductionPolicyRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       ProductionPolicyFacts
}

func sameFloors(current map[Resource]int64, desired []domain.ResourceFloor) bool {
	if len(current) != len(desired) {
		return false
	}
	for _, row := range desired {
		if current[Resource(row.Resource)] != row.Floor {
			return false
		}
	}
	return true
}
func sameStopped(current []Resource, desired []string) bool {
	if len(current) != len(desired) {
		return false
	}
	sorted := append([]Resource(nil), current...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i] < sorted[j] })
	for i, name := range desired {
		if string(sorted[i]) != name {
			return false
		}
	}
	return true
}

// EvaluateProductionPolicy re-validates one already-proposed floors/stopped
// replacement immediately before dispatch, the same shape EvaluateResearchSelect
// uses. Admission proves the desired state still genuinely diverges from the
// fresh native read right now; it does not prove the native write will be
// accepted (native Preview, run by the boundary immediately afterward, is the
// actual acceptance gate).
func EvaluateProductionPolicy(r ProductionPolicyRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	value, ok := r.Action.ProductionPolicy()
	canonical, err := domain.NewProductionPolicyAction(r.Action.ID(), value)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || r.Current.Direction == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.Tick < r.MinimumTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.Tick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	floors, floorsKnown := f.Floors.Value()
	stopped, stoppedKnown := f.Stopped.Value()
	if !floorsKnown || !stoppedKnown || !validToken(f.Token) {
		return refuse(UnknownFacts)
	}
	if sameFloors(floors, value.Floors()) && sameStopped(stopped, value.Stopped()) {
		return refuse(ProductionPolicySatisfied)
	}
	return DraftDecision{Admitted: true}
}
