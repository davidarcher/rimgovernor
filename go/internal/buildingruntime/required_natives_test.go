package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// The rounder requires the bench census read, so the base fake
// answers it as empty; tests that exercise benches define their own.
func (n *roundsNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	return nil, bridge.Result{}, nil
}
