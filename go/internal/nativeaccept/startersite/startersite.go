package startersite

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
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
// #709): the
// south-west corner and door of the rectangle the initial shelter's starter
// search ranks first on the loaded game, as the siteX/siteZ/doorX/doorZ
// arguments FixtureHut takes, or none when no such rectangle fits. The hut then stands where the controller
// would have raised it, so the colony grid a case derives from it is the
// one real play would have, and natural rock there is reused as wall.
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
	reading, err := observation.ObserveColony(ctx, h.Client, wallClock{}, expected, time.Minute, true, nil)
	if err != nil {
		return nil, fmt.Errorf("colony facts: %w", err)
	}
	layout, ok, err := buildingruntime.StarterSite(reading.Projection, size)
	if err != nil {
		return nil, err
	}
	if !ok {
		// The controller would raise a concave or grown shell here, which
		// the fixture cannot stage; it searches for its own square instead.
		return map[string]any{}, nil
	}
	door := layout.Shell.Door()
	return map[string]any{"siteX": layout.Room.X, "siteZ": layout.Room.Z, "doorX": door.X, "doorZ": door.Z}, nil
}
