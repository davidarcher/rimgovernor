package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// mortarsBuilt counts the layout's mortars once the tier stood in
// the latest census; a colony without a layout or a built tier has none.
func mortarsBuilt(ctx context.Context, journal *store.Store, snapshot domain.GenerationSnapshot) (int, error) {
	record, ok, err := journal.LoadDefenseLayout(ctx, store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map})
	if err != nil || !ok {
		return 0, err
	}
	tier, _, ok := record.Tier(policy.TierMortars)
	if !ok || !tier.Built {
		return 0, nil
	}
	return len(tier.Buildings), nil
}

// loadMortarShells is the load's mortar shells by kind; a source that
// serves no definitions has none, so the armory stocks no shells.
func loadMortarShells(ctx context.Context, native any, snapshot domain.GenerationSnapshot) (policy.MortarShells, error) {
	source, ok := native.(observation.DefinitionSource)
	if !ok {
		return policy.MortarShells{}, nil
	}
	catalog, err := source.DefinitionCatalog(ctx, boundary.Identity(snapshot))
	if err != nil {
		return policy.MortarShells{}, err
	}
	return catalog.MortarShells(policy.MortarSafeRadius)
}

// shellTargets is the armory's shell stock for the projection's colony.
func shellTargets(ctx context.Context, native any, journal *store.Store, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection) ([]policy.Amount, error) {
	mortars, err := mortarsBuilt(ctx, journal, snapshot)
	if err != nil {
		return nil, err
	}
	shells, err := loadMortarShells(ctx, native, snapshot)
	if err != nil {
		return nil, err
	}
	return policy.MortarShellTargets(mortars, policy.AssessArmory(projection.Facts.RaidPoints, projection.Facts.Research), shells), nil
}
