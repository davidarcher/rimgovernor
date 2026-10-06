package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func supplyTestCandidates() []AcquisitionCandidate {
	mine := MineCandidates("Steel", []ResourceSource{{ThingID: "ore", Yield: 100, Distance: 10, Method: ResourceSourceMine, Safety: "open_surface"}}, domain.Known(int64(500)))
	tree := AcquisitionSourceCandidates("WoodLog", []AcquisitionSource{{ID: "tree", Resource: "WoodLog", Tree: true, Yield: 40}}, domain.Cell{}, domain.Known(int64(500)))
	return append(mine, tree...)
}

// A steel shortfall raises mining of steel and nothing for wood: the wood tree
// serves no demand.
func TestResourceSupplyShortfallOpensItsOwnResource(t *testing.T) {
	t.Parallel()
	plan, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: "Steel", Deficit: 100, Candidates: supplyTestCandidates()}}, domain.Known(100000.0))
	if err != nil {
		t.Fatal(err)
	}
	if got := plan.OpenedIDs("Steel", AcquisitionMining); !got["ore"] {
		t.Fatal("steel mine not opened", plan.Plan.Explain())
	}
	if got := plan.Opened("WoodLog"); len(got) != 0 {
		t.Fatal("wood opened for a steel shortfall", plan.Plan.Explain())
	}
}

// One labor budget is shared by every floor: the better-scored candidate takes
// it (the cheap chop) and the other waits for the next Round.
func TestResourceSupplySharesOneLaborBudget(t *testing.T) {
	t.Parallel()
	inputs := []ResourceSupplyInput{
		{Resource: "Steel", Deficit: 100, Candidates: supplyTestCandidates()[:1]},
		{Resource: "WoodLog", Deficit: 40, Candidates: supplyTestCandidates()[1:]},
	}
	// Mining 100 steel costs 2000 labor ticks, chopping 40 wood 600.
	plan, err := PlanResourceSupply(inputs, domain.Known(2100.0))
	if err != nil {
		t.Fatal(err)
	}
	if len(plan.Opened("Steel")) != 0 || len(plan.Opened("WoodLog")) != 1 {
		t.Fatal("the budget was not shared", plan.Plan.Explain())
	}
	if plan, err = PlanResourceSupply(inputs, domain.Known(1e6)); err != nil || len(plan.Opened("Steel")) != 1 || len(plan.Opened("WoodLog")) != 1 {
		t.Fatal("both floors open with budget", plan.Plan.Explain(), err)
	}
}

// A floor already covered opens nothing, and a candidate listed under two
// resources is priced once.
func TestResourceSupplyCoveredFloorOpensNothing(t *testing.T) {
	t.Parallel()
	plan, err := PlanResourceSupply([]ResourceSupplyInput{
		{Resource: "Steel", Deficit: 0, Candidates: supplyTestCandidates()[:1]},
		{Resource: "WoodLog", Deficit: 40, Candidates: supplyTestCandidates()[1:]},
		{Resource: "Wood2", Deficit: 40, Candidates: supplyTestCandidates()[1:]},
	}, domain.Known(1e6))
	if err != nil || len(plan.Opened("Steel")) != 0 || len(plan.Opened("WoodLog")) != 1 {
		t.Fatal(plan.Plan.Explain(), err)
	}
}
