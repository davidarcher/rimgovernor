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

func anyRow(policy.AcquisitionSource) bool { return true }

// A designated plant nobody took past AcquisitionStallTicks, measured from
// native's first sight of the designation, is withdrawn (#1044), with no
// plan action paired to it (#1046).
func TestStalledDesignationsWithdrawUntakenHarvest(t *testing.T) {
	t.Parallel()
	rows := census(censusRow("healroot", false, false, 500))
	if got := stalledDesignations(rows, false, 500+59999, harvestContract(60000), anyRow); got != nil {
		t.Fatal("stalled before the bound elapses", got)
	}
	got := stalledDesignations(rows, false, 500+60000, harvestContract(60000), anyRow)
	if len(got) != 1 || got[0].ID != "healroot" {
		t.Fatal("did not report the stalled designation", got)
	}
	if got := stalledDesignations(rows, false, 500+60000, harvestContract(0), anyRow); got != nil {
		t.Fatal("zero bound must disable stall detection", got)
	}
	if got := stalledDesignations(domain.Unknown[[]policy.AcquisitionSource](), false, 500+60000, harvestContract(60000), anyRow); got != nil {
		t.Fatal("unknown census stalls nothing", got)
	}
	if got := stalledDesignations(rows, true, 500+60000, huntContract(60000), anyRow); got != nil {
		t.Fatal("a harvest row is not a hunt", got)
	}
	if got := stalledDesignations(rows, false, 500+60000, harvestContract(60000), func(policy.AcquisitionSource) bool { return false }); got != nil {
		t.Fatal("a row not of the goal's kind is not its to withdraw", got)
	}
}

func TestStalledDesignationsSkipTakenAndUndesignatedRows(t *testing.T) {
	t.Parallel()
	if got := stalledDesignations(census(censusRow("healroot", false, true, 0)), false, 1000000, harvestContract(60000), anyRow); got != nil {
		t.Fatal("a taken row is not stalled", got)
	}
	row := censusRow("healroot", false, false, 0)
	row.Designated, row.DesignatedTick = false, 0
	if got := stalledDesignations(census(row), false, 1000000, harvestContract(60000), anyRow); got != nil {
		t.Fatal("an undesignated row is not stalled", got)
	}
}

// A hunt untaken within HuntStallTicks is left alone; past it, it stalls.
// A taken hunt (a hunter holds it, even one HuntingSafety keeps refusing)
// never does.
func TestStalledDesignationsHuntGracePeriod(t *testing.T) {
	t.Parallel()
	rows := census(censusRow("deer", true, false, 100))
	if got := stalledDesignations(rows, true, 100+5999, huntContract(6000), anyRow); got != nil {
		t.Fatal("stalled before grace elapses", got)
	}
	got := stalledDesignations(rows, true, 100+6000, huntContract(6000), anyRow)
	if len(got) != 1 || got[0].ID != "deer" {
		t.Fatal("did not report stalled hunt", got)
	}
	if got := stalledDesignations(rows, false, 100+6000, harvestContract(6000), anyRow); got != nil {
		t.Fatal("a hunt row is not a harvest", got)
	}
	if got := stalledDesignations(census(censusRow("deer", true, true, 100)), true, 100+6000, huntContract(6000), anyRow); got != nil {
		t.Fatal("a taken hunt is not stalled", got)
	}
}

// A withdraw is one AcquisitionWithdrawAction whose method id hashes the
// source and the designation's first-seen tick (#1046).
func TestStallWithdrawIsOneHashedWithdrawAction(t *testing.T) {
	t.Parallel()
	row := censusRow("deer", true, false, 100)
	row.Resource, row.Cell = "Corpse_Deer", domain.Cell{X: 3, Z: 4}
	method, plan, err := stallWithdraw(row)
	if err != nil {
		t.Fatal(err)
	}
	actions := plan.Actions()
	if len(actions) != 1 || actions[0].Kind() != domain.AcquisitionWithdrawAction {
		t.Fatal("withdraw plan", actions)
	}
	if w, ok := actions[0].AcquisitionWithdraw(); !ok || w.Thing() != "deer" || w.Definition() != "Corpse_Deer" {
		t.Fatal("withdraw payload", w)
	}
	again, _, _ := stallWithdraw(row)
	row.DesignatedTick = 200
	later, _, _ := stallWithdraw(row)
	if again != method || later == method {
		t.Fatal("method id must hash the source and its designation tick", method, again, later)
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
