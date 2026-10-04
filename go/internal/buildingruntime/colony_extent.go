package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func (s *ClockScheduler) establishExtent(ctx context.Context, tick domain.Tick) error {
	held, ok := facts.Get[observation.ColonyProjection](s.facts.store, facts.Colony)
	if !ok || !held.Complete {
		return nil
	}
	p := held.Value
	state := s.player.session.State()
	if !state.ObservationKnown || p.Identity.Tick > tick || p.Identity.Colony != state.Snapshot.Colony || p.Identity.Map != state.Snapshot.Map || p.Identity.Load != state.Snapshot.Load {
		return nil
	}
	f := p.Facts
	extent, err := policy.DeriveColonyExtent(policy.ColonyExtentRequest{Bounds: domain.Known(p.Bounds), Construction: f.CurrentConstruction, Claims: f.ConstructionClaims, Home: f.HomeCoverage})
	if err != nil {
		return err
	}
	if value, known := extent.Value(); known {
		// Cached evidence may predate a player's latest expansion. Record at
		// this review's live tick so caching cannot masquerade as a rewind.
		_, err = s.player.journal.EstablishColonyExtent(ctx, state.Snapshot, tick, value.Regions)
	}
	return err
}
