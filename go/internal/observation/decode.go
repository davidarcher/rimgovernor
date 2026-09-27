package observation

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
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

// DecodeTick reads the identity a lifecycle_read_tick reply reports; it is
// DecodeIdentity without the capability list.
func DecodeTick(reply *l.TickReply) (Identity, error) {
	if reply == nil {
		return Identity{}, fmt.Errorf("%w: missing tick", ErrContract)
	}
	switch value := reply.Outcome.(type) {
	case *l.TickReply_Loaded:
		if value.Loaded == nil {
			return Identity{}, fmt.Errorf("%w: missing loaded tick", ErrContract)
		}
		result, err := contextIdentity(value.Loaded.Context)
		if err != nil {
			return result, err
		}
		if value.Loaded.Paused != nil {
			result.Paused = domain.Known(value.Loaded.GetPaused())
		}
		return result, nil
	case *l.TickReply_Unavailable:
		return Identity{}, bridge.ErrUnavailable
	case *l.TickReply_Failure:
		return Identity{}, bridge.ErrRefused
	default:
		return Identity{}, fmt.Errorf("%w: tick outcome", ErrContract)
	}
}
