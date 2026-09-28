package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// censusRow is one designated census row native first saw at since.
func censusRow(id string, hunt, taken bool, since domain.Tick) policy.AcquisitionSource {
	return policy.AcquisitionSource{ID: id, Hunt: hunt, Designated: true, DesignatedTick: since, Taken: taken, Yield: 10, NutritionYield: 2}
}

func census(rows ...policy.AcquisitionSource) domain.Fact[[]policy.AcquisitionSource] {
	return domain.Known(rows)
}

// A designated plant nobody took past AcquisitionStallTicks, measured from
// native's first sight of the designation, is withdrawn (#1044).
func TestStalledDesignationsWithdrawUntakenHarvest(t *testing.T) {
	t.Parallel()
	progress := []domain.Progress{dispatchedHunt(t, "harvest-0", "healroot", 100)}
	rows := census(censusRow("healroot", false, false, 500))
	if got := stalledDesignations(progress, rows, false, 500+59999, harvestContract(60000)); got != nil {
		t.Fatal("stalled before the bound elapses", got)
	}
	got := stalledDesignations(progress, rows, false, 500+60000, harvestContract(60000))
	if len(got) != 1 || got[0].Action != "harvest-0" || got[0].Thing != "healroot" {
		t.Fatal("did not report the stalled designation", got)
	}
	if got := stalledDesignations(progress, rows, false, 500+60000, harvestContract(0)); got != nil {
		t.Fatal("zero bound must disable stall detection", got)
	}
	if got := stalledDesignations(progress, domain.Unknown[[]policy.AcquisitionSource](), false, 500+60000, harvestContract(60000)); got != nil {
		t.Fatal("unknown census stalls nothing", got)
	}
	if got := stalledDesignations(progress, rows, true, 500+60000, huntContract(60000)); got != nil {
		t.Fatal("a harvest row is not a hunt", got)
	}
}

func TestStalledDesignationsSkipTakenAndUndesignatedRows(t *testing.T) {
	t.Parallel()
	progress := []domain.Progress{dispatchedHunt(t, "harvest-0", "healroot", 100)}
	if got := stalledDesignations(progress, census(censusRow("healroot", false, true, 0)), false, 1000000, harvestContract(60000)); got != nil {
		t.Fatal("a taken row is not stalled", got)
	}
	row := censusRow("healroot", false, false, 0)
	row.Designated, row.DesignatedTick = false, 0
	if got := stalledDesignations(progress, census(row), false, 1000000, harvestContract(60000)); got != nil {
		t.Fatal("an undesignated row is not stalled", got)
	}
}

// A hunt untaken within HuntStallTicks is left alone; past it, it stalls.
// A taken hunt (a hunter holds it, even one HuntingSafety keeps refusing)
// never does.
func TestStalledDesignationsHuntGracePeriod(t *testing.T) {
	t.Parallel()
	progress := []domain.Progress{dispatchedHunt(t, "hunt-deer", "deer", 100)}
	rows := census(censusRow("deer", true, false, 100))
	if got := stalledDesignations(progress, rows, true, 100+5999, huntContract(6000)); got != nil {
		t.Fatal("stalled before grace elapses", got)
	}
	got := stalledDesignations(progress, rows, true, 100+6000, huntContract(6000))
	if len(got) != 1 || got[0].Action != "hunt-deer" || got[0].Thing != "deer" {
		t.Fatal("did not report stalled hunt", got)
	}
	if got := stalledDesignations(progress, rows, false, 100+6000, harvestContract(6000)); got != nil {
		t.Fatal("a hunt row is not a harvest", got)
	}
	if got := stalledDesignations(progress, census(censusRow("deer", true, true, 100)), true, 100+6000, huntContract(6000)); got != nil {
		t.Fatal("a taken hunt is not stalled", got)
	}
}

// Only the plan's open, dispatched action on the row is withdrawn.
func TestStalledDesignationsNeedOpenDispatchedAction(t *testing.T) {
	t.Parallel()
	hunt := dispatchedHunt(t, "hunt-deer", "deer", 100)
	rows := census(censusRow("deer", true, false, 100))
	resolved, err := hunt.Observe(domain.Observation{Action: hunt.View().Action, Attempt: hunt.View().Attempt, Snapshot: hunt.View().Snapshot, Tick: 100, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}, hunt.View().Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if got := stalledDesignations([]domain.Progress{resolved}, rows, true, 100+6000, huntContract(6000)); got != nil {
		t.Fatal("resolved action must not be reported as stalled", got)
	}
	cancelled, err := hunt.Cancel()
	if err != nil {
		t.Fatal(err)
	}
	if got := stalledDesignations([]domain.Progress{cancelled}, rows, true, 100+6000, huntContract(6000)); got != nil {
		t.Fatal("cancelled action reported as stalled again", got)
	}
	if got := stalledDesignations(nil, rows, true, 100+6000, huntContract(6000)); got != nil {
		t.Fatal("a row without a plan action is not this plan's to withdraw", got)
	}
}

// Stalled rows come off native's pending food and wood totals.
func TestWithoutStalledTakesRowsOffPending(t *testing.T) {
	t.Parallel()
	rows := census(censusRow("a", false, false, 0), censusRow("b", true, false, 0), censusRow("c", false, false, 0))
	stalled := map[string]bool{"a": true, "b": true}
	if got, _ := withoutStalled(domain.Known(10.0), rows, stalled, true).Value(); got != 6 {
		t.Fatal("food pending", got)
	}
	if got, _ := withoutStalled(domain.Known(25.0), rows, stalled, false).Value(); got != 5 {
		t.Fatal("wood pending", got)
	}
	if got, _ := withoutStalled(domain.Known(3.0), rows, stalled, true).Value(); got != 0 {
		t.Fatal("pending floors at zero", got)
	}
	if _, known := withoutStalled(domain.Unknown[float64](), rows, stalled, true).Value(); known {
		t.Fatal("unknown pending stays unknown")
	}
}
