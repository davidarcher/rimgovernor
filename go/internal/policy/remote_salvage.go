package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SalvageEvidence contains native output and route observations. Estimated
// yield never becomes inventory until ordinary pawn work delivers it.
type SalvageEvidence struct {
	Safe      domain.Fact[bool]
	Candidate SupplyCandidate
}

// SalvagePriced is the one salvage yield pricing function: the native
// cost-list evidence as a CandidateSalvage row for the thing id. Optional
// resources keep only the yields of those resources with a known StockCap > 0
// (a yield with unknown price or cap prices nothing, never zero); the bool
// reports whether any yield remains. With no resources every yield is kept.
func SalvagePriced(id string, e SalvageEvidence, resources ...Resource) (SupplyCandidate, bool) {
	c := e.Candidate
	c.ID, c.Kind = id, CandidateSalvage
	if len(resources) == 0 {
		return c, len(c.Yields) > 0
	}
	c.Yields = nil
	for _, y := range e.Candidate.Yields {
		if n, known := y.StockCap.Value(); known && n > 0 && slices.Contains(resources, y.Good.Def) {
			c.Yields = append(c.Yields, y)
		}
	}
	return c, len(c.Yields) > 0
}
