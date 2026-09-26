package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Each MaintainResource planner yields a resource to the other's fresh
// higher bid, never to its own, a lower one, a withdrawn one, one from
// another world or one past its TTL (#728).
func TestAcquisitionBoardJointRanking(t *testing.T) {
	t.Parallel()
	var b acquisitionBoard
	world := domain.GenerationSnapshot{Colony: "c", Load: "l", Plan: "p"}
	if _, yield := b.bid(world, "WoodLog", bidResource, 0.2, policy.AcquisitionProduce, 100); yield {
		t.Fatal("yielded with no rival")
	}
	if _, yield := b.bid(world, "WoodLog", bidAcquisition, 0.5, policy.AcquisitionChop, 100); yield {
		t.Fatal("higher bid yielded")
	}
	if rival, yield := b.bid(world, "WoodLog", bidResource, 0.2, policy.AcquisitionProduce, 200); !yield || rival.kind != policy.AcquisitionChop {
		t.Fatal("lower bid kept the resource", rival)
	}
	if _, yield := b.bid(world, "Steel", bidResource, 0.2, policy.AcquisitionMining, 200); yield {
		t.Fatal("rival on another resource")
	}
	other := world
	other.Plan = "q"
	if _, yield := b.bid(other, "WoodLog", bidResource, 0.2, policy.AcquisitionProduce, 200); yield {
		t.Fatal("rival from another world")
	}
	if _, yield := b.bid(world, "WoodLog", bidAcquisition, 0, "", 300); yield {
		t.Fatal("withdraw yielded")
	}
	if _, yield := b.bid(world, "WoodLog", bidResource, 0.2, policy.AcquisitionProduce, 300); yield {
		t.Fatal("withdrawn bid still held")
	}
	b.bid(world, "WoodLog", bidAcquisition, 0.5, policy.AcquisitionChop, 400)
	if _, yield := b.bid(world, "WoodLog", bidResource, 0.2, policy.AcquisitionProduce, 400+acquisitionBidTTL+1); yield {
		t.Fatal("expired bid held")
	}
}
