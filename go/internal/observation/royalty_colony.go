package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// colonyRoyalty projects a validated Royalty section; an absent or
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

// RoyaltyOf is the royalty fact: the colonists' own holdings and psycasts
// from their pawn rows, the ladder and permits from the def mirror
// and the colony section's neuroformers, ceremonies and thrones. The
// colony section's presence is the Royalty-applicable gate, as for the other
// DLC sections: without it the fact is unknown and nothing is read. A pawn
// row or def the read cannot use is an error and the fact stays unknown.
func (p ColonyProjection) RoyaltyOf(pawns *o.PawnSnapshot, catalog *bridge.DefinitionCatalog) (domain.Fact[policy.RoyaltyFacts], error) {
	colony, ok := p.RoyaltyColony.Value()
	if !ok {
		return domain.Unknown[policy.RoyaltyFacts](), nil
	}
	colony, err := catalog.WithNeuroformerDefs(colony)
	if err != nil {
		return domain.Unknown[policy.RoyaltyFacts](), err
	}
	p.RoyaltyColony = domain.Known(colony)
	facts, err := bridge.PawnRoyaltyFacts(pawns, catalog)
	if err != nil {
		return domain.Unknown[policy.RoyaltyFacts](), err
	}
	mirrored, err := catalog.WithTitleDefs(facts)
	if err != nil {
		return domain.Unknown[policy.RoyaltyFacts](), err
	}
	return p.WithRoyaltyColony(mirrored), nil
}
