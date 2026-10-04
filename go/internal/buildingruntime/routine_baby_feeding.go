package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// reviewBabyFeeding sets the babies' food review (#1681) from the Biotech
// colony section's baby care and the food supply. It is unknown without
// either, or when a baby has no consumer row; a colony with no babies reviews
// as not short.
func (r *Rounder) reviewBabyFeeding(p *observation.ColonyProjection) {
	p.Facts.BabyFeeding = domain.Unknown[policy.BabyFeeding]()
	biotech, known := p.Biotech.Value()
	supply, supplied := p.FoodSupply.Value()
	if !known || !supplied {
		return
	}
	babies := make([]policy.PawnID, 0, len(biotech.Babies))
	for _, b := range biotech.Babies {
		babies = append(babies, policy.PawnID(b.PawnID))
	}
	review, err := policy.ReviewBabyFeeding(babies, len(biotech.Breastfeeders), supply, r.seasonal(p.Facts).FoodTargetDays)
	if err == nil {
		p.Facts.BabyFeeding = domain.Known(review)
	}
}
