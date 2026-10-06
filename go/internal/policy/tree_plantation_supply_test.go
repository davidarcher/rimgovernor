package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A plantation opens only when the chop sources in reach cannot cover the
// deficit: ample wild trees price below it, none leave it the only candidate.
func TestTreePlantationOpensOnlyWhenWildTreesFall(t *testing.T) {
	plan, ok := PlanFieldByResource(treeRequest(300, treeCrop("Plant_TreeOak", 30, 46)))
	if !ok {
		t.Fatal(plan.Explain())
	}
	field, ok := ResourceFieldCandidate("WoodLog", plan)
	if !ok {
		t.Fatal("plantation unpriced")
	}
	lead, _ := field.LeadDays.Value()
	opened := func(candidates ...SupplyCandidate) map[string]bool {
		p, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: "WoodLog", Deficit: 300, HorizonDays: lead, Candidates: candidates}}, domain.Known(1e9))
		if err != nil {
			t.Fatal(err)
		}
		return p.OpenedIDs("WoodLog", CandidateHarvest)
	}
	if got := opened(field); !got[field.ID] {
		t.Fatalf("no wild trees: the plantation opens, got %v", got)
	}
	wild := AcquisitionSourceCandidates("WoodLog", []AcquisitionSource{{ID: "oak0", Resource: "WoodLog", Tree: true, Yield: 400}}, domain.Cell{}, domain.Known(int64(1000)))
	p, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: "WoodLog", Deficit: 300, HorizonDays: lead, Candidates: append(wild, field)}}, domain.Known(1e9))
	if err != nil {
		t.Fatal(err)
	}
	if !p.OpenedIDs("WoodLog", CandidateChop)["oak0"] || p.OpenedIDs("WoodLog", CandidateHarvest)[field.ID] {
		t.Fatalf("ample wild trees: chop opens, no plantation")
	}
}
