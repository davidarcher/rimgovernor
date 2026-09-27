package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
)

// MovementCapabilities sends move intents through Actions/Apply; native
// validates each against live state (alive, spawned, drafted, reachable)
// and treats an order that already matches as applied.
type MovementCapabilities struct {
	Writer boundary.ActionsWriter
}

type movementBoundary struct {
	*boundary.Boundary
	writer boundary.ActionsWriter
}

func (b *movementBoundary) WriteMovement(ctx context.Context, p executor.Placement) (executor.Receipt, error) {
	return b.DispatchIntent(ctx, p, b.writer)
}

var _ executor.MovementBoundary = (*movementBoundary)(nil)
