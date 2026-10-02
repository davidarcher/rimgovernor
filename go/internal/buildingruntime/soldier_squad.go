package buildingruntime

import (
	"context"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// reviewSoldierSquad keeps the persistent soldier squad (#1558) current:
// sticky members, gaps filled with the best fighters, saved only on change.
// Unknown pawn facts keep the stored squad.
func reviewSoldierSquad(ctx context.Context, journal *store.Store, snapshot domain.GenerationSnapshot, pawns []policy.WorkPawn) error {
	world := store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}
	previous, err := journal.LoadSoldierSquad(ctx, world)
	if err != nil {
		return err
	}
	squad, known := policy.ReviewSoldierSquad(previous, pawns)
	if !known || slices.Equal(squad.Members, previous.Members) {
		return nil
	}
	return journal.SaveSoldierSquad(ctx, store.SoldierSquadRecord{World: world, Members: squad.Members})
}
