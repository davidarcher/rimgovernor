package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

// stepScopeSource is the identity read every routine source offers.
type stepScopeSource interface {
	Identity(context.Context) (*l.IdentityReply, bridge.Result, error)
}

// stepTickSource is the bare scope read a native client offers beside it.
type stepTickSource interface {
	Tick(context.Context) (*l.TickReply, bridge.Result, error)
}

// stepScope reads the observation scope (load, map, tick, generation, pause
// state) a routine review or planner validates its reads against. It
// prefers lifecycle_read_tick, which the scheduler step's bundle seeds into
// the step read cache, so a review after a stop costs no identity round trip
// (#200); a source without the tick read falls back to the full identity,
// whose capability list no routine consults.
func stepScope(ctx context.Context, native stepScopeSource) (observation.Identity, error) {
	if ticks, ok := native.(stepTickSource); ok {
		reply, _, err := ticks.Tick(ctx)
		if err != nil {
			return observation.Identity{}, err
		}
		return observation.DecodeTick(reply)
	}
	reply, _, err := native.Identity(ctx)
	if err != nil {
		return observation.Identity{}, err
	}
	return observation.DecodeIdentity(reply)
}
