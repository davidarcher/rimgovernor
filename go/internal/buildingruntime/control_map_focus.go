package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	"google.golang.org/protobuf/proto"
)

type mapFocusNative interface {
	FocusMap(context.Context, *a.FocusMap) (*a.ControlReply, bridge.Result, error)
}

type handoffGenerationKey struct{}

// ConfirmMapFocus proves vanilla changed the view while the prior Auto grant
// was live. Another native interruption cannot borrow that permission.
func (s *Session) ConfirmMapFocus(ctx context.Context, before, target domain.GenerationSnapshot, generation domain.NativeGeneration) error {
	if generation != before.Native+1 {
		return ErrControl
	}
	if err := s.control.config.StopWrites(ctx); err != nil {
		return err
	}
	reply, _, err := s.control.native.ReadAuthority(ctx, controlIdentity(target))
	if err != nil {
		return err
	}
	status := reply.GetStatus()
	if err = controlStatus(reply, target); err != nil {
		return err
	}
	if status.GetContext().GetNativeGeneration() != uint64(generation) || status.GetInactive().GetReason() != a.RevocationReason_REVOCATION_REASON_IDENTITY_CHANGED {
		return ErrControl
	}
	return ctx.Err()
}

func (s *Session) FocusMap(ctx context.Context, before, target domain.GenerationSnapshot) (domain.NativeGeneration, error) {
	c := s.control
	native, ok := c.native.(mapFocusNative)
	if !ok {
		return 0, ErrControl
	}
	if err := c.config.StopWrites(ctx); err != nil {
		return 0, err
	}
	if err := ctx.Err(); err != nil {
		return 0, err
	}
	reply, _, err := native.FocusMap(ctx, &a.FocusMap{Identity: controlIdentity(before), Target: controlIdentity(target), ExpectedGeneration: proto.Uint64(uint64(before.Native))})
	if err != nil {
		return 0, err
	}
	return domain.NativeGeneration(reply.GetFocusedMap().GetContext().GetNativeGeneration()), nil
}
