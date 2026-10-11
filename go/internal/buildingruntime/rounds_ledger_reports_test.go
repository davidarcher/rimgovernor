package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// reportingDeclarer is the test double of a bill purpose that reports a verdict
// beside its declaration.
type reportingDeclarer struct {
	*fakeDeclarer
	key, need policy.ConcernID
	latest    purposeReport
}

func (d *reportingDeclarer) reportTarget() (policy.ConcernID, policy.ConcernID) {
	return d.key, d.need
}

func (d *reportingDeclarer) report() purposeReport { return d.latest }

func reportingFixture(t *testing.T, latest purposeReport) (*ledgerFixture, *reportingDeclarer) {
	t.Helper()
	f := newLedgerFixture(t)
	f.native.benches = []policy.GearBench{ledgerBenchRow()}
	d := &reportingDeclarer{fakeDeclarer: &fakeDeclarer{}, key: "purposeKey", need: policy.MaintainWorkLedger, latest: latest}
	f.reviewer.AddOrderDeclarer(d)
	return f, d
}

// The ledger step files each purpose's verdict under its key: no deficit while
// the purpose's concern is not workable, else the verdict its declaration noted,
// with the game time it lends.
func TestLedgerFilesThePurposeVerdicts(t *testing.T) {
	f, _ := reportingFixture(t, purposeReport{order: waitFor(policy.CauseExistingWork, "sculpture"), workTicks: artNativeWorkTicks})
	idle := f.round()
	if len(idle.Reports) != 1 || idle.Reports[0] != (LedgerReport{Key: "purposeKey", Verdict: BuildingReasonNoDeficit}) || idle.NativeWorkTicks != 0 {
		t.Fatalf("an unowed purpose reported %+v", idle)
	}
	f.declare(ledgerOrder("Make_Vest"))
	owed := f.round()
	if owed.Verdict != BuildingReasonAdmitted || len(owed.Reports) != 1 || owed.Reports[0] != (LedgerReport{Key: "purposeKey", Verdict: waitFor(policy.CauseExistingWork, "sculpture")}) || owed.NativeWorkTicks != artNativeWorkTicks {
		t.Fatalf("an owed purpose reported %+v", owed)
	}
}

// A purpose whose own work is in flight reports existing work and lends no time;
// a journal read that fails reports the purpose unavailable and leaves bill
// placement alone.
func TestLedgerPurposeVerdictFailureDoesNotFailPlacement(t *testing.T) {
	f, d := reportingFixture(t, purposeReport{order: BuildingReasonNoDeficit, workTicks: 1})
	f.declare(ledgerOrder("Make_Vest"))
	d.latest.openWork = func(context.Context, *store.Store, store.WorkOwner) (bool, error) { return true, nil }
	open := f.round()
	if open.Reports[0].Verdict != BuildingReasonExistingWork || open.NativeWorkTicks != 0 {
		t.Fatalf("open work reported %+v", open)
	}
	f.declare(ledgerOrder("Make_Hat"))
	d.latest.openWork = func(context.Context, *store.Store, store.WorkOwner) (bool, error) {
		return false, errors.New("journal")
	}
	failed := f.round()
	if failed.Reports[0].Verdict != fieldUnavailable("purpose_plans") || failed.NativeWorkTicks != 0 {
		t.Fatalf("a failed read reported %+v", failed)
	}
	if failed.Verdict != BuildingReasonAdmitted && failed.Verdict != BuildingReasonExistingWork {
		t.Fatalf("a reporter's failed read changed the ledger's own verdict: %+v", failed)
	}
}

// A priority gate holds the purposes with the ledger, under the same verdict.
func TestLedgerHoldFilesEveryPurposeKey(t *testing.T) {
	f, _ := reportingFixture(t, purposeReport{})
	held := f.reviewer.heldReports(BuildingShelterPending)
	if len(held) != 1 || held[0] != (LedgerReport{Key: "purposeKey", Verdict: BuildingShelterPending}) {
		t.Fatalf("%+v", held)
	}
	entry := plannerEntry{held: plannerCatalogEntry(t, "workLedger").held}
	out := &ClockSchedulerResult{}
	s := &ClockScheduler{config: ClockSchedulerConfig{Ledger: f.planner}}
	if _, err := entry.awaiting(BuildingShelterPending)(s, nil, nil, out, nil); err != nil || out.Ledger == nil || len(out.Ledger.Reports) != 1 {
		t.Fatalf("%+v %v", out.Ledger, err)
	}
}

func plannerCatalogEntry(t *testing.T, name string) plannerEntry {
	t.Helper()
	for _, entry := range plannerCatalog {
		if entry.name == name {
			return entry
		}
	}
	t.Fatalf("no catalog planner %q", name)
	return plannerEntry{}
}

// The food purposes name why they chose nothing: the food plan has not opened
// the channel, or the reserve is unread.
func TestFoodReportNamesTheGap(t *testing.T) {
	preserve := &RoundsBillPlanner{purpose: policy.PreserveFood}
	closed := policy.FoodOrderRequest{Purpose: policy.PreserveFood, Facts: policy.RoundsFacts{FoodPlan: domain.Unknown[policy.FoodPlan]()}}
	if got := preserve.foodReport(closed, observation.ColonyProjection{}); got.order != awaitingFoodPlan("stock-protection") || got.workTicks != 0 {
		t.Fatalf("%+v", got)
	}
	open := policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{{Decision: policy.FoodPlanOpen, Channel: policy.SupplyCandidate{Kind: policy.CandidateReserve, ID: "stock-protection"}}}}
	unread := policy.FoodOrderRequest{Purpose: policy.PreserveFood, Facts: policy.RoundsFacts{FoodPlan: domain.Known(open)}}
	if got := preserve.foodReport(unread, observation.ColonyProjection{}); got.order != fieldUnavailable("food_reserve") {
		t.Fatalf("%+v", got)
	}
}

// Four bill purposes share their concerns with other planners, so each files
// under a subject key of its own: a key that is a concern id would compete with
// the concern's planners for its one winning cause.
func TestBillPurposeKeysAreSubjectKeys(t *testing.T) {
	concerns := map[policy.ConcernID]bool{}
	for _, inspection := range policy.AllInspections() {
		concerns[inspection.Concern] = true
	}
	seen := map[policy.ConcernID]bool{}
	for _, purpose := range []policy.BillPurpose{policy.CookFood, policy.PreserveFood, policy.ButcherFood, policy.CookAheadFood} {
		declarer := &RoundsBillPlanner{purpose: purpose}
		key, _ := declarer.reportTarget()
		if key == "" || concerns[key] || seen[key] {
			t.Fatalf("%s files under %q", purpose, key)
		}
		seen[key] = true
	}
}
