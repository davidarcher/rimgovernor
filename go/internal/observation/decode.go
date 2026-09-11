package observation

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func contextIdentity(context *c.ObservationContext) (Identity, error) {
	if err := bridge.ValidateContext(context); err != nil {
		return Identity{}, fmt.Errorf("%w: %v", ErrContract, err)
	}
	result := Identity{Colony: domain.ColonyID(context.Identity.GetColonyId()), Map: domain.MapID(context.Identity.GetMapId()), Load: domain.LoadID(context.Identity.GetLoadToken()), Tick: domain.Tick(context.GetTick())}
	if context.NativeGeneration != nil {
		result.NativeGeneration = domain.Known(domain.NativeGeneration(context.GetNativeGeneration()))
	}
	return result, result.Validate()
}
func DecodeIdentity(reply *l.IdentityReply) (Identity, error) {
	if reply == nil {
		return Identity{}, fmt.Errorf("%w: missing identity", ErrContract)
	}
	switch value := reply.Outcome.(type) {
	case *l.IdentityReply_Loaded:
		if value.Loaded == nil {
			return Identity{}, fmt.Errorf("%w: missing loaded identity", ErrContract)
		}
		result, err := contextIdentity(value.Loaded.Context)
		if err != nil {
			return result, err
		}
		if value.Loaded.Paused != nil {
			result.Paused = domain.Known(value.Loaded.GetPaused())
		}
		return result, nil
	case *l.IdentityReply_Unavailable:
		return Identity{}, bridge.ErrUnavailable
	case *l.IdentityReply_Failure:
		return Identity{}, bridge.ErrRefused
	default:
		return Identity{}, fmt.Errorf("%w: identity outcome", ErrContract)
	}
}
func DecodeStatus(reply *o.StatusReply) (Status, error) {
	if reply == nil {
		return Status{}, fmt.Errorf("%w: missing status", ErrContract)
	}
	switch value := reply.Outcome.(type) {
	case *o.StatusReply_Observed:
		if value.Observed == nil {
			return Status{}, fmt.Errorf("%w: missing status snapshot", ErrContract)
		}
		if _, err := contextIdentity(value.Observed.Context); err != nil {
			return Status{}, err
		}
		return Status{Availability: GameLoaded}, nil
	case *o.StatusReply_Unavailable:
		return Status{}, bridge.ErrUnavailable
	case *o.StatusReply_Failure:
		return Status{}, bridge.ErrRefused
	default:
		return Status{}, fmt.Errorf("%w: status outcome", ErrContract)
	}
}
