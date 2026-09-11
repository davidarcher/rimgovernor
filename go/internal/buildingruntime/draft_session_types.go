package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// DraftCapabilities selects a complete native draft implementation. None of these
// capabilities acquires permission; live dispatch also requires the session lease.
type DraftCapabilities struct {
	Native  DraftNative
	Writer  DraftWriter
	Cleanup DraftCleanupWriter
}

// CleanupDraft shares the executor writer and remains usable after ordinary Stop.
// Session/worker ownership must join every caller before closing shared handles.
func (s *Session) CleanupDraft(ctx context.Context, plan domain.PlanID, action domain.ActionID) (executor.Result, error) {
	return s.executor.CleanupDraft(ctx, plan, action)
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

var _ WorldSource = (*DraftBoundary)(nil)
