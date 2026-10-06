package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSalvagePricedFiltersToKnownCapResource(t *testing.T) {
	steel := SourceYield(ResourceKey{Def: "Steel"}, 10, 1, domain.Known(int64(50)))
	unknown := SourceYield(ResourceKey{Def: "Steel"}, 10, 1, domain.Known(int64(50)))
	unknown.StockCap = domain.Fact[int64]{}
	wood := SourceYield(ResourceKey{Def: "WoodLog"}, 5, 1, domain.Known(int64(50)))
	e := SalvageEvidence{Candidate: SourceCandidate(CandidateSalvage, "x", domain.Known(1.0), domain.Known(1.0), true, 75, steel, unknown, wood)}
	c, ok := SalvagePriced("t1", e, "Steel")
	if !ok || c.ID != "t1" || c.Kind != CandidateSalvage || len(c.Yields) != 1 {
		t.Fatalf("filtered = %+v ok=%v", c, ok)
	}
	if c, ok := SalvagePriced("t1", e); !ok || len(c.Yields) != 3 {
		t.Fatalf("unfiltered = %+v", c)
	}
	if _, ok := SalvagePriced("t1", e, "Silver"); ok {
		t.Fatal("silver priced")
	}
}
