package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// colonyRoyalty projects a validated Royalty section (#1877); an absent or
// unavailable one is an unknown fact.
func colonyRoyalty(section *o.RoyaltySection) (domain.Fact[policy.RoyaltyColony], error) {
	f := section.GetObserved()
	if f == nil {
		return domain.Fact[policy.RoyaltyColony]{}, nil
	}
	r, err := bridge.DecodeRoyaltyColony(f)
	if err != nil {
		return domain.Fact[policy.RoyaltyColony]{}, err
	}
	return domain.Known(r), nil
}

// WithRoyaltyColony is the royalty read f completed with the colony section's
// neuroformers, ceremonies and thrones. Without the section the royalty fact
// is unknown: planning on a stock, ceremony or throne list that was not read
// would act on an empty one.
func (p ColonyProjection) WithRoyaltyColony(f policy.RoyaltyFacts) domain.Fact[policy.RoyaltyFacts] {
	colony, ok := p.RoyaltyColony.Value()
	if !ok {
		return domain.Unknown[policy.RoyaltyFacts]()
	}
	return domain.Known(f.WithColony(colony))
}
