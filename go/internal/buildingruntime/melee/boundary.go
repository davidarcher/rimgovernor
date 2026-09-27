// Package melee sends melee attack and subdue orders as Actions/Apply
// intents (#856). Native validates the drafted pawn and the target live and
// reuses its attack and subdue job code; the receipt is terminal.
package melee

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
)

type MeleeCapabilities struct {
	Writer boundary.ActionsWriter
}

type MeleeBoundary struct {
	*boundary.Boundary
	writer boundary.ActionsWriter
}

func NewMeleeBoundary(place *boundary.Boundary, writer boundary.ActionsWriter) (*MeleeBoundary, error) {
	if place == nil || writer == nil {
		return nil, errors.New("invalid melee boundary dependencies")
	}
	return &MeleeBoundary{place, writer}, nil
}

// WriteMelee sends the melee intent through Actions/Apply.
func (b *MeleeBoundary) WriteMelee(ctx context.Context, p executor.Placement) (executor.Receipt, error) {
	return b.DispatchIntent(ctx, p, b.writer)
}

var _ executor.MeleeBoundary = (*MeleeBoundary)(nil)
