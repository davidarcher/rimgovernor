package draft

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// DraftCapabilities selects a complete native draft implementation. None of these
// capabilities acquires permission; live dispatch also requires the session lease.
type DraftCapabilities struct {
	Native  DraftNative
	Writer  DraftWriter
	Cleanup DraftCleanupWriter
}

// ReadWorld bypasses control gates and reads current native identity on every call.
func (b *DraftBoundary) ReadWorld(ctx context.Context) (store.World, error) {
	reply, _, err := b.native.Identity(ctx)
	if err != nil {
		return store.World{}, err
	}
	observed := reply.GetLoaded().GetContext()
	if err = bridge.ValidateContext(observed); err != nil {
		return store.World{}, err
	}
	world := store.World{Colony: domain.ColonyID(observed.Identity.GetColonyId()), Load: domain.LoadID(observed.Identity.GetLoadToken()), Map: domain.MapID(observed.Identity.GetMapId())}
	if err = world.Validate(); err != nil {
		return store.World{}, err
	}
	return world, ctx.Err()
}
