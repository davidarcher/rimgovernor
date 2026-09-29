package startersite

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
)

type wallClock struct{}

func (wallClock) Now() time.Time { return time.Now() }

// Args is ArgsFor(9), the 9x9 fixture hut's site.
func Args(ctx context.Context, h *na.Harness) (map[string]any, error) {
	return ArgsFor(9)(ctx, h)
}

// ArgsFor is a Fixture's ArgsFrom for a size x size fixture hut (#700,
// #709): the south-west corner and door of the square squareSite ranks
// first on the loaded game, as the siteX/siteZ/doorX/doorZ arguments
// FixtureHut takes; an error when no square fits. The controller itself
// builds only planned rooms (#1231); this fixture-only search keeps the
// huts near the colony centre with natural rock reused as wall.
func ArgsFor(size int32) func(context.Context, *na.Harness) (map[string]any, error) {
	return func(ctx context.Context, h *na.Harness) (map[string]any, error) {
		return args(ctx, h, size)
	}
}

func args(ctx context.Context, h *na.Harness, size int32) (map[string]any, error) {
	reply, _, err := h.Client.Identity(ctx)
	if err != nil {
		return nil, err
	}
	expected, err := observation.DecodeIdentity(reply)
	if err != nil {
		return nil, err
	}
	reading, err := observation.ObserveColony(observation.WithPlanningWindow(ctx, window{h.Client}), h.Client, wallClock{}, expected, time.Minute, true)
	if err != nil {
		return nil, fmt.Errorf("colony facts: %w", err)
	}
	shell, ok := squareSite(reading.Projection, size)
	if !ok {
		return nil, fmt.Errorf("no square %dx%d fixture hut site on this map", size, size)
	}
	door, b := shell.Door(), shell.Bounds()
	return map[string]any{"siteX": b.X, "siteZ": b.Z, "doorX": door.X, "doorZ": door.Z}, nil
}
