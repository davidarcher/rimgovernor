package buildingruntime

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
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

// ChangeExpansionArea records territory intent under the shared player gate.
// It performs no native mutation and uses the currently observed timeline tick.
func (p *Player) ChangeExpansionArea(ctx context.Context, world store.World, id, reason string, cells []domain.Cell, remove bool) error {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return err
	}
	defer done()
	if err = p.world(call, world); err != nil {
		return err
	}
	source, ok := p.worlds.(interface {
		ReadExtentIdentity(context.Context) (observation.Identity, error)
	})
	if !ok {
		return ErrControl
	}
	identity, err := source.ReadExtentIdentity(call)
	if err != nil {
		return err
	}
	state := p.session.State()
	if identity.Colony != world.Colony || identity.Map != world.Map || identity.Load != world.Load {
		return store.ErrConflict
	}
	if !state.ObservationKnown || state.Snapshot.Colony != world.Colony || state.Snapshot.Map != world.Map || state.Snapshot.Load != world.Load {
		return store.ErrConflict
	}
	if !remove {
		held, ok := facts.Get[observation.ColonyProjection](p.extentFacts, facts.Colony)
		if !ok || !held.Complete || !held.Value.Identity.SameContext(identity) {
			return ErrControl
		}
		for _, cell := range cells {
			if cell.X < 0 || cell.Z < 0 || cell.X >= held.Value.Bounds.Width || cell.Z >= held.Value.Bounds.Height {
				return store.ErrConflict
			}
		}
	}
	if err = p.current(call, epoch); err != nil {
		return err
	}
	if remove {
		return p.journal.RemoveExpansionArea(call, state.Snapshot, identity.Tick, id, reason)
	}
	cells = append([]domain.Cell(nil), cells...)
	sort.Slice(cells, func(i, j int) bool {
		return cells[i].X < cells[j].X || cells[i].X == cells[j].X && cells[i].Z < cells[j].Z
	})
	return p.journal.AddExpansionArea(call, state.Snapshot, identity.Tick, id, cells, reason)
}
