package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func obstructionCrop(name string, resource Resource, per float64, destroys bool) CropChoice {
	return CropChoice{Name: name, Harvests: domain.Known(resource), UnitsPerCell: domain.Known(per), HarvestDestroys: domain.Known(destroys)}
}

func obstructionIDs(cs []SupplyCandidate) []string {
	var ids []string
	for _, c := range cs {
		ids = append(ids, c.ID)
	}
	return ids
}

func TestObstructionCandidatesPriceClearance(t *testing.T) {
	ruin := ClearanceTarget{EntityID: "Thing_1", DefName: "Ancient_Wall", Minimum: domain.Cell{X: 3, Z: 3}, Maximum: domain.Cell{X: 3, Z: 3}}
	remote := ClearanceTarget{EntityID: "Thing_9", DefName: "Ancient_Wall"}
	tree := ClearanceTarget{EntityID: "Thing_2", DefName: "Plant_TreeOak", Minimum: domain.Cell{X: 5, Z: 5}, Maximum: domain.Cell{X: 5, Z: 5}}
	chunk := ClearanceTarget{EntityID: "Thing_3", DefName: "ChunkGranite", Minimum: domain.Cell{X: 6, Z: 6}, Maximum: domain.Cell{X: 6, Z: 6}, Count: 1}
	evidence := func(id string) SalvageEvidence {
		return SalvageEvidence{Safe: domain.Known(true), Candidate: SourceCandidate(CandidateSalvage, id, domain.Known(40.0), domain.Known(0.0), true, 75,
			SourceYield(ResourceKey{Def: "Steel"}, 30, 0, domain.Known(int64(500))), SourceYield(ResourceKey{Def: "Plasteel"}, 5, 0, domain.Known(int64(500))))}
	}
	pricing := ObstructionPricing{
		Salvage: map[string]SalvageEvidence{"Thing_1": evidence("Thing_1"), "Thing_9": evidence("Thing_9")},
		Crops:   []CropChoice{obstructionCrop("Plant_TreeOak", "WoodLog", 40, true)},
		Remote:  "Thing_9",
	}
	ops := []Operation{
		{Kind: OpFurnitureOut, Targets: []ClearanceTarget{ruin, remote}},
		{Kind: OpPack, Targets: []ClearanceTarget{ruin}},
		{Kind: OpCut, Targets: []ClearanceTarget{tree}},
		{Kind: OpHaulOut, Targets: []ClearanceTarget{chunk}},
	}
	headroom := domain.Known(int64(100))
	home := domain.Cell{}
	steel := ObstructionCandidates("Steel", ops, pricing, home, headroom)
	if got := obstructionIDs(steel); len(got) != 1 || got[0] != "Thing_1" || steel[0].Kind != CandidateSalvage || len(steel[0].Yields) != 1 {
		t.Fatalf("steel candidates = %v, want the plan-blocking ruin's steel only (not the remote one, one row per thing)", got)
	}
	if n, _ := steel[0].Yields[0].StockCap.Value(); n != 30 {
		t.Fatalf("ruin steel yield = %d, want the cost-list 30", n)
	}
	wood := ObstructionCandidates("WoodLog", ops, pricing, home, headroom)
	if got := obstructionIDs(wood); len(got) != 1 || got[0] != "Thing_2" || wood[0].Kind != CandidateChop {
		t.Fatalf("wood candidates = %v, want the cut tree", got)
	}
	if n, _ := wood[0].Yields[0].StockCap.Value(); n != 40 {
		t.Fatalf("tree wood yield = %d, want 40", n)
	}
	stone := ObstructionCandidates("ChunkGranite", ops, pricing, home, headroom)
	if got := obstructionIDs(stone); len(got) != 1 || got[0] != "Thing_3" {
		t.Fatalf("stone candidates = %v, want the hauled chunk", got)
	}
}

func TestObstructionCandidatesPriceUnknownAsNothing(t *testing.T) {
	tree := ClearanceTarget{EntityID: "Thing_2", DefName: "Plant_TreeOak"}
	for name, crop := range map[string]CropChoice{
		"unknown yield":    {Name: "Plant_TreeOak", Harvests: domain.Known(Resource("WoodLog")), HarvestDestroys: domain.Known(true)},
		"harvest survives": obstructionCrop("Plant_TreeOak", "WoodLog", 40, false),
	} {
		got := ObstructionCandidates("WoodLog", []Operation{{Kind: OpCut, Targets: []ClearanceTarget{tree}}}, ObstructionPricing{Crops: []CropChoice{crop}}, domain.Cell{}, domain.Unknown[int64]())
		if len(got) != 0 {
			t.Fatalf("%s: candidates = %v, want none", name, obstructionIDs(got))
		}
	}
	haul := ClearanceTarget{EntityID: "Thing_3", DefName: "ChunkGranite"}
	if got := ObstructionCandidates("ChunkGranite", []Operation{{Kind: OpHaulOut, Targets: []ClearanceTarget{haul}}}, ObstructionPricing{}, domain.Cell{}, domain.Unknown[int64]()); len(got) != 0 {
		t.Fatalf("a stack of unknown size priced %v, want none", obstructionIDs(got))
	}
}

func TestObstructionCandidatesRankAmongSources(t *testing.T) {
	ruin := ClearanceTarget{EntityID: "Thing_1", DefName: "Ancient_Wall"}
	pricing := ObstructionPricing{Salvage: map[string]SalvageEvidence{"Thing_1": {Safe: domain.Known(true), Candidate: SourceCandidate(CandidateSalvage, "Thing_1", domain.Known(10.0), domain.Known(0.0), true, 75,
		SourceYield(ResourceKey{Def: "Steel"}, 50, 0, domain.Known(int64(500))))}}}
	candidates := ObstructionCandidates("Steel", []Operation{{Kind: OpFurnitureOut, Targets: []ClearanceTarget{ruin}}}, pricing, domain.Cell{}, domain.Known(int64(500)))
	candidates = append(candidates, SourceCandidate(CandidateMining, "mine", domain.Known(2000.0), domain.Known(0.0), true, 75, SourceYield(ResourceKey{Def: "Steel"}, 50, 0, domain.Known(int64(500)))))
	plan, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: "Steel", Deficit: 50, Candidates: candidates}}, domain.Known(1e9))
	if err != nil {
		t.Fatal(err)
	}
	opened := plan.Opened("Steel")
	if len(opened) == 0 || opened[0].Candidate.ID != "Thing_1" {
		t.Fatalf("opened = %v, want the cheap ruin deconstruction ranked first", opened)
	}
}
