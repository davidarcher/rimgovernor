package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// gearRequest is the declaration request over the review's stamped gear census
// and the ledger's bench readback.
func gearRequest(projection observation.ColonyProjection, benches []policy.GearBench) policy.GearPlanningRequest {
	return policy.GearPlanningRequest{Observation: projection.Facts.Gear, Benches: domain.Known(benches), StuffCategories: projection.Facts.Items.StuffCategories}
}

// abstainOnRead is the declaration of a Round whose native read failed: the
// ledger removes nothing on it. A cancelled context is the caller's error.
func abstainOnRead(ctx context.Context, fact policy.Cause) (policy.Declared, error) {
	if ctx.Err() != nil {
		return policy.Declared{}, ctx.Err()
	}
	return policy.Abstaining(fact), nil
}

// DeclareOrders is the declaration owned by policy.MaintainEquipment.
func (r *RoundsGearPlanner) DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	declared, err := r.declareOrders(ctx, snapshot, projection, benches)
	return declared.For(policy.MaintainEquipment), err
}

// declareOrders declares MaintainEquipment's apparel batches (OrderDeclarer).
func (r *RoundsGearPlanner) declareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	return policy.DeclareGearOrders(gearRequest(projection, benches))
}

// DeclareOrders is the declaration owned by policy.MaintainEquipment.
func (r *RoundsArmoryPlanner) DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	declared, err := r.declareOrders(ctx, snapshot, projection, benches)
	return declared.For(policy.MaintainEquipment), err
}

// declareOrders declares the armory's weapon batches, armor ladder and mortar
// shell stock (OrderDeclarer), and notes whether a crafting spot is wanted.
func (r *RoundsArmoryPlanner) declareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	r.setSpotNeeded(false)
	tier := policy.AssessArmory(projection.Facts.RaidPoints, projection.Facts.Research).Tier
	census, known := projection.Facts.Gear.Value()
	if _, ok := r.native.(RoundsEquipSource); !ok {
		return policy.Abstaining(policy.CauseUnreadNativeRead), nil
	}
	if !known {
		return policy.Abstaining(policy.CauseUnreadGearDemand), nil
	}
	ids := make([]string, 0, len(census.Pawns))
	for _, p := range census.Pawns {
		ids = append(ids, string(p.Pawn))
	}
	fighters, hunters, unarmed, err := r.weaponDemand(ctx, snapshot, ids, int64(projection.Identity.Tick), benches, tier)
	if err != nil {
		return abstainOnRead(ctx, policy.CauseUnreadWeapons)
	}
	shells, err := shellTargets(ctx, r.native, r.reviewer.player.journal, snapshot, projection)
	if err != nil {
		return abstainOnRead(ctx, policy.CauseUnreadShells)
	}
	r.setSpotNeeded(unarmed > 0 && !weaponBenchHosted(benches))
	return policy.DeclareArmoryOrders(gearRequest(projection, benches), tier, mergeAmounts(fighters, hunters), shells)
}

func (r *RoundsArmoryPlanner) setSpotNeeded(v bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.spotNeeded = v
}

func (r *RoundsArmoryPlanner) spotWanted() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.spotNeeded
}
