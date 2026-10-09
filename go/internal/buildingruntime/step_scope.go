package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

// stepScopeSource is the bare scope read every routine source offers.
type stepScopeSource interface {
	Tick(context.Context) (*l.TickReply, bridge.Result, error)
}

// stepScope reads the observation scope (load, map, tick, generation, pause
// state) a Rounds pass or planner validates its reads against through
// lifecycle_read_tick, which the scheduler step's bundle seeds into the step
// read cache, so a review after a stop costs no identity round trip.
func stepScope(ctx context.Context, native stepScopeSource) (observation.Identity, error) {
	reply, _, err := native.Tick(ctx)
	if err != nil {
		return observation.Identity{}, err
	}
	return observation.DecodeTick(reply)
}
