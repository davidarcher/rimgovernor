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
// in the trusted player-control owner; possession of a Client grants no lease.
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
			err = activeAuthority(state.Active, 30000)
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

func (control *AuthorityControl) Acquire(ctx context.Context, request *a.Acquire) (*a.ControlReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("acquire required")
	}
	request = proto.Clone(request).(*a.Acquire)
	if err := errors.Join(authorityUnknown(request), authorityRequest(request.Identity, request.ExpectedGeneration), authorityOwner(request.Owner), authorityDuration(request.LeaseMs)); err != nil {
		return nil, Result{}, err
	}
	if request.GetExpectedGeneration() == ^uint64(0) {
		return nil, Result{}, contract("authority generation exhausted")
	}
	return control.call(ctx, &a.ControlRequest{Operation: &a.ControlRequest_Acquire{Acquire: request}}, request.Owner)
}
func (control *AuthorityControl) Renew(ctx context.Context, request *a.Renew, expectedOwner *a.Owner) (*a.ControlReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("renew required")
	}
	request = proto.Clone(request).(*a.Renew)
	if err := errors.Join(authorityUnknown(request), authorityRequest(request.Identity, request.ExpectedGeneration), validID(request.GetControllerSessionId()), validID(request.GetLeaseId()), authorityDuration(request.LeaseMs)); err != nil {
		return nil, Result{}, err
	}
	if err := errors.Join(authorityUnknown(expectedOwner), authorityOwner(expectedOwner)); err != nil {
		return nil, Result{}, err
	}
	expectedOwner = proto.Clone(expectedOwner).(*a.Owner)
	if expectedOwner.GetControllerSessionId() != request.GetControllerSessionId() {
		return nil, Result{}, contract("renew owner session mismatch")
	}
	return control.call(ctx, &a.ControlRequest{Operation: &a.ControlRequest_Renew{Renew: request}}, expectedOwner)
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
	case a.RevocationReason_REVOCATION_REASON_MANUAL, a.RevocationReason_REVOCATION_REASON_PLAYER_DIRECTION, a.RevocationReason_REVOCATION_REASON_DISCONNECT, a.RevocationReason_REVOCATION_REASON_SHUTDOWN:
	default:
		return nil, Result{}, contract("external revocation reason required")
	}
	if request.GetExpectedGeneration() == ^uint64(0) {
		return nil, Result{}, contract("authority generation exhausted")
	}
	return control.call(ctx, &a.ControlRequest{Operation: &a.ControlRequest_Revoke{Revoke: request}}, nil)
}
func (control *AuthorityControl) call(ctx context.Context, request *a.ControlRequest, expectedOwner *a.Owner) (*a.ControlReply, Result, error) {
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
		err = validateAuthorityControl(request, reply, expectedOwner)
	}
	if err != nil {
		return nil, raw, &AuthorityUncertain{err, raw}
	}
	return reply, raw, nil
}
func validateAuthorityControl(request *a.ControlRequest, reply *a.ControlReply, expectedOwner *a.Owner) error {
	var identity *c.Identity
	var generation uint64
	var duration uint32
	var session, lease string
	var owner *a.Owner
	var reason a.RevocationReason
	switch op := request.Operation.(type) {
	case *a.ControlRequest_Acquire:
		identity, generation, duration, owner = op.Acquire.Identity, op.Acquire.GetExpectedGeneration(), op.Acquire.GetLeaseMs(), op.Acquire.Owner
		session = owner.GetControllerSessionId()
	case *a.ControlRequest_Renew:
		identity, generation, duration = op.Renew.Identity, op.Renew.GetExpectedGeneration(), op.Renew.GetLeaseMs()
		session, lease = op.Renew.GetControllerSessionId(), op.Renew.GetLeaseId()
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
	if lease == "" {
		generation++
	}
	granted := reply.GetGranted()
	if granted == nil {
		return contract("acquire/renew requires granted reply")
	}
	if err := errors.Join(authorityContext(granted.Context, identity, generation), activeAuthority(granted.Authority, duration), validID(granted.GetLeaseId())); err != nil {
		return err
	}
	if granted.Authority.Owner.GetControllerSessionId() != session || !proto.Equal(expectedOwner, granted.Authority.Owner) {
		return contract("granted owner mismatch")
	}
	if lease != "" && (granted.GetLeaseId() != lease || granted.Context.GetNativeGeneration() != generation) {
		return contract("renewal changed lease or generation")
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
func authorityOwner(value *a.Owner) error {
	if value == nil || value.PlayerDirection == nil || value.GetPlayerDirection() == 0 {
		return contract("authority owner required")
	}
	return validID(value.GetControllerSessionId())
}
func authorityDuration(value *uint32) error {
	if value == nil || *value < 1000 || *value > 30000 {
		return contract("authority duration outside1000..30000ms")
	}
	return nil
}
func activeAuthority(value *a.ActiveAuthority, maximum uint32) error {
	if value == nil || value.RemainingLeaseMs == nil || value.GetRemainingLeaseMs() == 0 || value.GetRemainingLeaseMs() > maximum {
		return contract("invalid active lease duration")
	}
	return authorityOwner(value.Owner)
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
