package bridge

import (
	"context"
	"errors"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// AuthorityControl is a separately held mutation capability. Construct it only
// in the trusted player-control owner; possession of a Client grants no mode.
// Calls never retry, reconnect, or enable service writes automatically.
type AuthorityControl struct{ client *Client }

func NewAuthorityControl(client *Client) (*AuthorityControl, error) {
	if client == nil {
		return nil, contract("authority client required")
	}
	return &AuthorityControl{client: client}, nil
}

// AuthorityUncertain means control may have changed. Observe current authority
// before another control decision; a failed response is never a retry license.
type AuthorityUncertain struct {
	Cause   error
	Receipt Result
}

func (e *AuthorityUncertain) Error() string { return "authority outcome uncertain: " + e.Cause.Error() }
func (e *AuthorityUncertain) Unwrap() error { return e.Cause }

func (client *Client) ReadAuthority(ctx context.Context, identity *c.Identity) (*a.StatusReply, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	request := &a.StatusRequest{Identity: proto.Clone(identity).(*c.Identity)}
	reply := &a.StatusReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/authority_read_status", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch value := reply.Outcome.(type) {
	case *a.StatusReply_Failure:
		return reply, raw, failure(value.Failure, raw)
	case *a.StatusReply_Status:
		if value.Status == nil {
			return nil, raw, contract("missing authority status")
		}
		status := value.Status
		if err = ValidateContext(status.Context); err != nil {
			return nil, raw, err
		}
		if !sameIdentity(status.Context.Identity, request.Identity) {
			return nil, raw, contract("authority identity mismatch")
		}
		switch state := status.State.(type) {
		case *a.Status_Unavailable:
			return reply, raw, unavailable(state.Unavailable, raw)
		case *a.Status_Active:
			err = activeAuthority(state.Active)
		case *a.Status_Inactive:
			err = inactiveAuthority(state.Inactive)
		default:
			err = contract("missing authority state")
		}
		if err == nil && status.Context.NativeGeneration == nil {
			err = contract("missing authority generation")
		}
	default:
		err = contract("missing authority outcome")
	}
	if err != nil {
		return nil, raw, err
	}
	return reply, raw, nil
}

// SetMode is the only way to change authority explicitly: Auto grants the bot
// authority outright, Manual revokes it. There is no acquire/renew handshake
// because there is only ever one bot process (see #52).
func (control *AuthorityControl) SetMode(ctx context.Context, request *a.SetMode) (*a.ControlReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("set-mode required")
	}
	request = proto.Clone(request).(*a.SetMode)
	if err := errors.Join(authorityUnknown(request), authorityRequest(request.Identity, request.ExpectedGeneration), authorityMode(request.Mode)); err != nil {
		return nil, Result{}, err
	}
	if request.GetExpectedGeneration() == ^uint64(0) {
		return nil, Result{}, contract("authority generation exhausted")
	}
	return control.call(ctx, &a.ControlRequest{Operation: &a.ControlRequest_SetMode{SetMode: request}})
}
func (control *AuthorityControl) Revoke(ctx context.Context, request *a.Revoke) (*a.ControlReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("revoke required")
	}
	request = proto.Clone(request).(*a.Revoke)
	if err := errors.Join(authorityUnknown(request), authorityRequest(request.Identity, request.ExpectedGeneration)); err != nil {
		return nil, Result{}, err
	}
	switch request.GetReason() {
	case a.RevocationReason_REVOCATION_REASON_MANUAL, a.RevocationReason_REVOCATION_REASON_DISCONNECT, a.RevocationReason_REVOCATION_REASON_SHUTDOWN:
	default:
		return nil, Result{}, contract("external revocation reason required")
	}
	if request.GetExpectedGeneration() == ^uint64(0) {
		return nil, Result{}, contract("authority generation exhausted")
	}
	return control.call(ctx, &a.ControlRequest{Operation: &a.ControlRequest_Revoke{Revoke: request}})
}
func (control *AuthorityControl) call(ctx context.Context, request *a.ControlRequest) (*a.ControlReply, Result, error) {
	if control == nil || control.client == nil {
		return nil, Result{}, contract("authority capability required")
	}
	if err := ctx.Err(); err != nil {
		return nil, Result{}, err
	}
	reply := &a.ControlReply{}
	raw, err := control.client.protoCall(ctx, "rimgovernor/authority_control", request, reply)
	if err != nil {
		return nil, raw, &AuthorityUncertain{err, raw}
	}
	if value, ok := reply.Outcome.(*a.ControlReply_Failure); ok {
		err = failure(value.Failure, raw)
		if errors.Is(err, ErrRefused) {
			return reply, raw, err
		}
	} else {
		err = validateAuthorityControl(request, reply)
	}
	if err != nil {
		return nil, raw, &AuthorityUncertain{err, raw}
	}
	return reply, raw, nil
}
func validateAuthorityControl(request *a.ControlRequest, reply *a.ControlReply) error {
	var identity *c.Identity
	var generation uint64
	var reason a.RevocationReason
	var mode a.Mode
	switch op := request.Operation.(type) {
	case *a.ControlRequest_SetMode:
		identity, generation, mode = op.SetMode.Identity, op.SetMode.GetExpectedGeneration(), op.SetMode.GetMode()
	case *a.ControlRequest_Revoke:
		identity, generation, reason = op.Revoke.Identity, op.Revoke.GetExpectedGeneration(), op.Revoke.GetReason()
	default:
		return contract("unsupported authority request")
	}
	if reason != 0 {
		revoked := reply.GetRevoked()
		if revoked == nil {
			return contract("revoke requires inactive reply")
		}
		if err := errors.Join(authorityContext(revoked.Context, identity, generation+1), inactiveAuthority(revoked.Authority)); err != nil {
			return err
		}
		if revoked.Authority.GetReason() != reason {
			return contract("revocation reason mismatch")
		}
		return nil
	}
	if mode == a.Mode_MODE_MANUAL {
		revoked := reply.GetRevoked()
		if revoked == nil {
			return contract("manual set-mode requires inactive reply")
		}
		return errors.Join(authorityContext(revoked.Context, identity, generation+1), inactiveAuthority(revoked.Authority))
	}
	granted := reply.GetGranted()
	if granted == nil {
		return contract("set-mode auto requires granted reply")
	}
	if err := errors.Join(authorityContext(granted.Context, identity, generation+1), activeAuthority(granted.Authority)); err != nil {
		return err
	}
	return nil
}
func authorityContext(value *c.ObservationContext, identity *c.Identity, generation uint64) error {
	if err := ValidateContext(value); err != nil {
		return err
	}
	if value.NativeGeneration == nil || value.GetNativeGeneration() != generation || !sameIdentity(value.Identity, identity) {
		return contract("authority context mismatch")
	}
	return nil
}
func authorityIdentity(value *c.Identity) error {
	return errors.Join(authorityUnknown(value), ValidateIdentity(value))
}
func authorityRequest(identity *c.Identity, generation *uint64) error {
	if err := authorityIdentity(identity); err != nil {
		return err
	}
	if generation == nil || *generation == 0 {
		return contract("positive authority generation required")
	}
	return nil
}
// authorityDuration bounds a requested lease duration in milliseconds. It is
// no longer used by the authority domain itself (Mode has no time-based
// expiry), but the clock domain's own, unrelated lease-duration requests
// still need the same bound.
func authorityDuration(value *uint32) error {
	if value == nil || *value < 1000 || *value > 30000 {
		return contract("authority duration outside1000..30000ms")
	}
	return nil
}
func authorityMode(value *a.Mode) error {
	if value == nil || *value != a.Mode_MODE_AUTO && *value != a.Mode_MODE_MANUAL {
		return contract("explicit authority mode required")
	}
	return nil
}
func activeAuthority(value *a.ActiveAuthority) error {
	if value == nil || value.GetMode() != a.Mode_MODE_AUTO {
		return contract("invalid active authority")
	}
	return nil
}
func inactiveAuthority(value *a.InactiveAuthority) error {
	if value == nil || value.Reason == nil || value.GetReason() < 1 || value.GetReason().Descriptor().Values().ByNumber(value.GetReason().Number()) == nil {
		return contract("invalid inactive authority")
	}
	return nil
}

// Recursive inspection is limited to generated messages at this transport boundary.
func authorityUnknown(value proto.Message) error {
	if value == nil || !value.ProtoReflect().IsValid() {
		return nil
	}
	message := value.ProtoReflect()
	if len(message.GetUnknown()) != 0 {
		return contract("unknown authority field")
	}
	var err error
	message.Range(func(field protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		if field.Kind() == protoreflect.MessageKind {
			err = authorityUnknown(value.Message().Interface())
		}
		return err == nil
	})
	return err
}
