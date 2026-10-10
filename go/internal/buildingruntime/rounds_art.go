package buildingruntime

import (
	"context"
	"fmt"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoundsArtPlanner is MaintainArt's planner. The pinned sculpture bills are
// the ledger's: DeclareOrders declares them (OrderDeclarer) and the planner
// commits no bill method of its own. What stays is the game time a sculpture
// takes, which a placed bill does not ask the clock for.
type RoundsArtPlanner struct {
	reviewer *Rounder
	mu       sync.Mutex
	// sculpting is whether the latest review read an active sculpture bill.
	sculpting bool
}

// artNativeWorkTicks is the window an active sculpture bill asks for per
// step; the review re-reads the bench between windows.
const artNativeWorkTicks = 2500

func NewRoundsArtPlanner(reviewer *Rounder) (*RoundsArtPlanner, error) {
	if reviewer == nil {
		return nil, fmt.Errorf("%w: NewRoundsArtPlanner: reviewer == nil", ErrControl)
	}
	return &RoundsArtPlanner{reviewer: reviewer}, nil
}

// DeclareOrders is the declaration owned by policy.MaintainArt.
func (r *RoundsArtPlanner) DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	declared, err := r.declareOrders(ctx, snapshot, projection, benches)
	return declared.For(policy.MaintainArt), err
}

// declareOrders declares the pinned sculpture batches (OrderDeclarer) and notes
// whether one is being sculpted.
func (r *RoundsArtPlanner) declareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	r.mu.Lock()
	r.sculpting = policy.SculptureInProgress(benches)
	r.mu.Unlock()
	request := policy.ArtOrderRequest{Benches: benches, Colonists: projection.Facts.Colonists, Items: projection.Facts.Items, RoomsOwed: projection.Facts.SculptureRoomsOwed, Profiles: domain.Unknown[[]policy.PawnProfile]()}
	if pawns, known := projection.WorkPawns.Value(); known {
		profiles := policy.Profiles(pawns)
		request.Profiles = domain.Known(profiles)
		request.Demand = artDemand(projection, profiles)
	}
	return policy.DeclareArtOrders(request), nil
}

func (r *RoundsArtPlanner) step(call, _ context.Context, _ *stepArbiter) (RoundsBillResult, error) {
	p := r.reviewer.player
	state := p.session.State()
	if !state.Enabled {
		return RoundsBillResult{Verdict: BuildingReasonDisabled}, nil
	}
	review, err := p.journal.LoadRounds(call)
	if err != nil {
		return RoundsBillResult{}, err
	}
	if !review.Enabled || review.Snapshot != state.Snapshot {
		return RoundsBillResult{Verdict: BuildingReasonNoReview}, nil
	}
	_, workable, err := p.journal.WorkableOwner(call, review, policy.MaintainArt)
	if err != nil {
		return RoundsBillResult{}, err
	}
	r.mu.Lock()
	sculpting := r.sculpting
	r.mu.Unlock()
	if !workable || !sculpting {
		return RoundsBillResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	return RoundsBillResult{Verdict: waitFor(policy.CauseExistingWork, "sculpture"), NativeWorkTicks: artNativeWorkTicks}, nil
}

// artDemand sizes the art bills from the same bedroom census as
// sculptureRoomsOwed, the colony stock and the artists' skills; an unknown
// census leaves only the small sculpture.
func artDemand(facts observation.ColonyProjection, profiles []policy.PawnProfile) policy.ArtDemand {
	stock, _ := facts.Resources.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	obs, sk := facts.Facts.Sleeping.Value()
	traits := sleepingTraits(facts)
	if !rk || !ck || !sk || !census.Colony || traits == nil {
		return policy.NewArtDemand(domain.Unknown[policy.SleepingObservation](), nil, nil, stock, profiles, facts.Facts.Items)
	}
	tier, _ := facts.TechTier.Value()
	return policy.NewArtDemand(facts.Facts.Sleeping, policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness), policy.FurnitureRooms(rooms, census, facts.Cells), stock, profiles, facts.Facts.Items)
}
