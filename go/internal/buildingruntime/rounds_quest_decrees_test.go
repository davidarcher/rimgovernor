package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestDecreeCensusWorkPreservesActiveMatchingBill(t *testing.T) {
	bench := policy.GearBench{Bills: domain.Known([]policy.GearBill{{Recipe: "MakeArmor", Active: domain.Known(true)}})}
	if !decreeCensusWork(bench, "MakeArmor") {
		t.Fatal("active generic bill was duplicated")
	}
	if decreeCensusWork(bench, "MakeHelmet") {
		t.Fatal("different recipe counted as work")
	}
	bench.Bills = domain.Known([]policy.GearBill{{Recipe: "MakeArmor", Active: domain.Known(false)}})
	if decreeCensusWork(bench, "MakeArmor") {
		t.Fatal("inactive bill counted as work")
	}
}
