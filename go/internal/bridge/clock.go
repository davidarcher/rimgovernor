package bridge

import (
	"context"
	"errors"
	"math"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// ClockControl is an explicit mutation capability, separate from clock reads.
// The runtime owner must authorize its use. No operation retries or reacquires.
type ClockControl struct{ client *Client }

func NewClockControl(client *Client) (*ClockControl, error) {
	if client == nil {
		return nil, contract("clock client required")
	}
	return &ClockControl{client}, nil
}

// ClockUncertain preserves a possibly applied clock operation. A retained
// canonical uncertain receipt is returned alongside this error when available.
type ClockUncertain struct {
	Cause   error
	Receipt Result
}

func (e *ClockUncertain) Error() string { return "clock outcome uncertain: " + e.Cause.Error() }
func (e *ClockUncertain) Unwrap() error { return e.Cause }

func clockPrecondition(pre *a.WritePrecondition, owner *a.Owner) error {
	if pre == nil {
		return contract("clock authority required")
	}
	if err := errors.Join(clockWire(pre), clockWire(owner), ValidateIdentity(pre.Identity), buildingAttempt(pre.Attempt), authorityOwner(owner)); err != nil {
		return err
	}
	if pre.ExpectedGeneration == nil || pre.GetExpectedGeneration() == 0 || pre.LeaseId == nil || validID(pre.GetLeaseId()) != nil || pre.Attempt.GetControllerSessionId() != owner.GetControllerSessionId() {
		return contract("clock authority presence or owner")
	}
	return nil
}
func clockOwned(request *k.OwnedRequest) error {
	if request == nil {
		return contract("owned clock request required")
	}
	return errors.Join(clockWire(request), ValidateIdentity(request.Identity), clockOwner(request.Owner))
}
func clockOriginal(request *k.OwnedRequest, pre *a.WritePrecondition, original *k.Epoch) error {
	if err := errors.Join(clockOwned(request), clockEpoch(original)); err != nil {
		return err
	}
	if pre.GetExpectedGeneration() != original.Origin.GetNativeGeneration() || !sameIdentity(request.Identity, pre.Identity) || !sameIdentity(original.Origin.Identity, request.Identity) || !proto.Equal(original.Owner, request.Owner) || request.Owner.GetControllerSessionId() != pre.Attempt.GetControllerSessionId() {
		return contract("clock original epoch mismatch")
	}
	return nil
}

func (control *ClockControl) Start(ctx context.Context, request *k.StartRequest, expectedOwner *a.Owner) (*k.ControlReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("clock start required")
	}
	request = proto.Clone(request).(*k.StartRequest)
	if err := errors.Join(clockWire(request), clockPrecondition(request.Authority, expectedOwner), authorityDuration(request.LeaseMs)); err != nil {
		return nil, Result{}, err
	}
	if request.Speed == nil || request.GetSpeed() < 1 || request.GetSpeed() > 3 || request.MaxTicks == nil || request.GetMaxTicks() < 1 || request.GetMaxTicks() > 1800000 {
		return nil, Result{}, contract("clock start speed or tick budget")
	}
	if err := clockPolicy(request.Policy, int64(request.GetMaxTicks())); err != nil {
		return nil, Result{}, err
	}
	expectedOwner = proto.Clone(expectedOwner).(*a.Owner)
	expectation := ClockExpectation{request.Authority.Identity, request.Authority.Attempt, expectedOwner, request.Authority.GetExpectedGeneration(), ClockCommand{Start: &ClockStart{Speed: request.GetSpeed(), Policy: request.Policy, LeaseMS: request.GetLeaseMs(), MaxTicks: request.GetMaxTicks()}}}
	return control.clockCall(ctx, "rimgovernor/clock_start", request, request.Authority, expectedOwner, func(r *k.ControlReceipt) error { return ValidateClockReceipt(r, expectation) })
}
func (control *ClockControl) Renew(ctx context.Context, request *k.RenewRequest, originalEpoch *k.Epoch, expectedOwner *a.Owner) (*k.ControlReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("clock renew required")
	}
	request = proto.Clone(request).(*k.RenewRequest)
	if err := errors.Join(clockWire(request), clockPrecondition(request.Authority, expectedOwner), authorityDuration(request.LeaseMs)); err != nil {
		return nil, Result{}, err
	}
	if err := clockOriginal(request.Epoch, request.Authority, originalEpoch); err != nil {
		return nil, Result{}, err
	}
	originalEpoch = proto.Clone(originalEpoch).(*k.Epoch)
	expectedOwner = proto.Clone(expectedOwner).(*a.Owner)
	expectation := ClockExpectation{request.Authority.Identity, request.Authority.Attempt, expectedOwner, request.Authority.GetExpectedGeneration(), ClockCommand{Renew: &ClockRenew{Original: originalEpoch, LeaseMS: request.GetLeaseMs()}}}
	return control.clockCall(ctx, "rimgovernor/clock_renew", request, request.Authority, expectedOwner, func(r *k.ControlReceipt) error { return ValidateClockReceipt(r, expectation) })
}
func (control *ClockControl) ChangeSpeed(ctx context.Context, request *k.SpeedRequest, originalEpoch *k.Epoch, expectedOwner *a.Owner) (*k.ControlReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("clock speed request required")
	}
	request = proto.Clone(request).(*k.SpeedRequest)
	if err := errors.Join(clockWire(request), clockPrecondition(request.Authority, expectedOwner)); err != nil {
		return nil, Result{}, err
	}
	if request.Speed == nil || request.GetSpeed() < 1 || request.GetSpeed() > 3 {
		return nil, Result{}, contract("clock ordinary speed required")
	}
	if err := clockOriginal(request.Epoch, request.Authority, originalEpoch); err != nil {
		return nil, Result{}, err
	}
	originalEpoch = proto.Clone(originalEpoch).(*k.Epoch)
	expectedOwner = proto.Clone(expectedOwner).(*a.Owner)
	expectation := ClockExpectation{request.Authority.Identity, request.Authority.Attempt, expectedOwner, request.Authority.GetExpectedGeneration(), ClockCommand{Speed: &ClockSpeed{Original: originalEpoch, Speed: request.GetSpeed()}}}
	return control.clockCall(ctx, "rimgovernor/clock_change_speed", request, request.Authority, expectedOwner, func(r *k.ControlReceipt) error { return ValidateClockReceipt(r, expectation) })
}
func clockSameEpoch(actual, original *k.Epoch, speed k.Speed) error {
	if actual == nil || !proto.Equal(actual.Owner, original.Owner) || !proto.Equal(actual.Origin, original.Origin) || !proto.Equal(actual.Policy, original.Policy) || actual.GetStartTick() != original.GetStartTick() || actual.GetTickDeadline() != original.GetTickDeadline() || actual.GetRequestedSpeed() != speed || actual.GetLastTick() < original.GetLastTick() {
		return contract("clock immutable epoch mismatch")
	}
	return nil
}
func (control *ClockControl) clockCall(ctx context.Context, name string, request proto.Message, pre *a.WritePrecondition, owner *a.Owner, validate func(*k.ControlReceipt) error) (*k.ControlReply, Result, error) {
	if control == nil || control.client == nil {
		return nil, Result{}, contract("clock capability required")
	}
	if err := ctx.Err(); err != nil {
		return nil, Result{}, err
	}
	reply := &k.ControlReply{}
	raw, err := control.client.protoCall(ctx, name, request, reply)
	if err != nil {
		return nil, raw, &ClockUncertain{err, raw}
	}
	switch v := reply.Outcome.(type) {
	case *k.ControlReply_Failure:
		err = failure(v.Failure, raw)
		if errors.Is(err, ErrRefused) {
			return reply, raw, err
		}
	case *k.ControlReply_LongEventPending:
		if v.LongEventPending != nil && diagnostic(v.LongEventPending.Detail) {
			return reply, raw, nil
		}
		err = contract("invalid clock pending result")
	case *k.ControlReply_Receipt:
		err = clockReceipt(v.Receipt, pre.Identity, pre.Attempt, owner, pre.GetExpectedGeneration())
		if err == nil && v.Receipt.GetUncertain() != nil {
			return reply, raw, &ClockUncertain{errors.New("native reported uncertain clock control"), raw}
		}
		if err == nil {
			err = validate(v.Receipt)
		}
	default:
		err = contract("clock control outcome missing")
	}
	if err != nil {
		return reply, raw, &ClockUncertain{err, raw}
	}
	return reply, raw, nil
}

// OwnedPause remains usable after authority revocation. Its exact original epoch
// prevents it from stopping a replacement owner. Inspect the returned pause facts;
// Stopping is an armed failed pause, not proof that the game stopped.
func (control *ClockControl) OwnedPause(ctx context.Context, request *k.OwnedRequest) (*k.StatusReply, Result, error) {
	if err := clockOwned(request); err != nil {
		return nil, Result{}, err
	}
	request = proto.Clone(request).(*k.OwnedRequest)
	if control == nil || control.client == nil {
		return nil, Result{}, contract("clock capability required")
	}
	if err := ctx.Err(); err != nil {
		return nil, Result{}, err
	}
	reply := &k.StatusReply{}
	raw, err := control.client.protoCall(ctx, "rimgovernor/clock_pause", request, reply)
	if err == nil {
		err = clockStatusReply(reply, request.Identity, raw)
	}
	if errors.Is(err, ErrRefused) {
		return reply, raw, err
	}
	if err == nil {
		e := clockStatusEpoch(reply.GetStatus())
		if e == nil || !proto.Equal(e.Owner, request.Owner) || !sameIdentity(e.Origin.Identity, request.Identity) || reply.GetStatus().GetRunning() != nil {
			err = contract("owned pause epoch/state mismatch")
		}
	}
	if err != nil {
		return reply, raw, &ClockUncertain{err, raw}
	}
	return reply, raw, nil
}

func (client *Client) ReadClockStatus(ctx context.Context, identity *c.Identity) (*k.StatusReply, Result, error) {
	if err := authorityIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &k.StatusReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/clock_read_status", &k.StatusRequest{Identity: identity}, reply)
	if err == nil {
		err = clockStatusReply(reply, identity, raw)
	}
	return reply, raw, err
}

// ReadClockAttempt never interprets Unknown as proof of no effect or permission
// to retry. The caller retains the original request for reconciliation.
func (client *Client) ReadClockAttempt(ctx context.Context, request *k.AttemptRequest) (*k.AttemptReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("clock attempt request required")
	}
	request = proto.Clone(request).(*k.AttemptRequest)
	if err := errors.Join(clockWire(request), ValidateIdentity(request.Identity), buildingAttempt(request.Attempt)); err != nil {
		return nil, Result{}, err
	}
	reply := &k.AttemptReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/clock_read_attempt", request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *k.AttemptReply_Failure:
		err = failure(v.Failure, raw)
	case *k.AttemptReply_Unknown:
		if v.Unknown == nil {
			err = contract("missing unknown attempt")
		}
	case *k.AttemptReply_Receipt:
		err = clockReceipt(v.Receipt, request.Identity, request.Attempt, nil, 0)
	default:
		err = contract("missing clock attempt outcome")
	}
	return reply, raw, err
}

func clockStatusReply(reply *k.StatusReply, identity *c.Identity, raw Result) error {
	switch v := reply.Outcome.(type) {
	case *k.StatusReply_Failure:
		return failure(v.Failure, raw)
	case *k.StatusReply_Status:
		if err := clockStatus(v.Status, identity); err != nil {
			return err
		}
		if v.Status.GetUnavailable() != nil {
			return unavailable(v.Status.GetUnavailable(), raw)
		}
		return nil
	default:
		return contract("missing clock status outcome")
	}
}

func clockPolicy(p *k.WatchPolicy, budget int64) error {
	if p == nil || p.Mode == nil || p.GetMode() < 1 || p.GetMode() > 2 || p.HealthDropFraction == nil || p.MinHealthFraction == nil || p.HostileWithin == nil || p.InjuryStopCooldownMs == nil || p.GetInjuryStopCooldownMs() > 1800000 {
		return contract("clock policy presence or bounds")
	}
	if err := clockWire(p); err != nil {
		return err
	}
	if p.GetHealthDropFraction() < 0.01 || p.GetHealthDropFraction() > 1 || p.GetMinHealthFraction() < 0.01 || p.GetMinHealthFraction() > 1 || p.GetHostileWithin() < 1 || p.GetHostileWithin() > 250 {
		return contract("clock policy numeric bounds")
	}
	for _, ids := range [][]string{p.AcknowledgedHostileIds, p.AcknowledgedDownedColonistIds, p.AcknowledgedInjuredColonistIds, p.SurgicalRecoveryIds, p.MedicalRestIds} {
		if len(ids) > 256 {
			return contract("clock policy ID bound")
		}
		seen := map[string]bool{}
		for _, id := range ids {
			if validID(id) != nil || seen[id] {
				return contract("invalid or duplicate clock pawn ID")
			}
			seen[id] = true
		}
	}
	if len(p.MedicalRestIds) > 0 && budget > 600 {
		return contract("medical rest clock budget exceeds 600 ticks")
	}
	return nil
}
func clockOwner(owner *k.EpochOwner) error {
	if owner == nil || owner.ControllerSessionId == nil || owner.Epoch == nil || owner.GetEpoch() <= 0 {
		return contract("clock epoch owner required")
	}
	return errors.Join(clockWire(owner), validID(owner.GetControllerSessionId()))
}
func clockEpoch(e *k.Epoch) error {
	if e == nil {
		return contract("clock epoch required")
	}
	if err := errors.Join(clockWire(e), clockOwner(e.Owner), ValidateContext(e.Origin)); err != nil {
		return err
	}
	if e.Origin.NativeGeneration == nil || e.StartTick == nil || e.GetStartTick() != e.Origin.GetTick() || e.TickDeadline == nil || e.GetTickDeadline() <= e.GetStartTick() || e.GetTickDeadline()-e.GetStartTick() > 1800000 || e.LastTick == nil || e.GetLastTick() < e.GetStartTick() || e.LeaseRemainingMs == nil || e.GetLeaseRemainingMs() > 30000 || e.RequestedSpeed == nil || e.GetRequestedSpeed() < 1 || e.GetRequestedSpeed() > 3 {
		return contract("clock epoch bounds or presence")
	}
	return clockPolicy(e.Policy, e.GetTickDeadline()-e.GetStartTick())
}
func clockStatusEpoch(s *k.Status) *k.Epoch {
	if v := s.GetRunning(); v != nil {
		return v.Epoch
	}
	if v := s.GetStopping(); v != nil {
		return v.Epoch
	}
	if v := s.GetStopped(); v != nil {
		return v.Epoch
	}
	return nil
}
func clockStatus(s *k.Status, identity *c.Identity) error {
	if s == nil {
		return contract("clock status required")
	}
	if err := errors.Join(clockWire(s), ValidateContext(s.Context)); err != nil {
		return err
	}
	if !sameIdentity(s.Context.Identity, identity) {
		return contract("clock status identity mismatch")
	}
	if s.GetUnavailable() != nil {
		return validateUnavailable(s.GetUnavailable())
	}
	if s.NativeTickBoundary == nil || s.DurableEvents == nil || s.NewestCursor == nil || s.GetNewestCursor() < 0 || s.ObservedSpeed == nil || s.GetObservedSpeed() < 1 || s.GetObservedSpeed() > 5 || s.ActualPaused == nil {
		return contract("clock status facts missing")
	}
	if !diagnostic(s.WatcherError) || !diagnostic(s.ForcePauseKind) || (s.MaxProbeTickGap != nil && s.GetMaxProbeTickGap() < 0) {
		return contract("clock status diagnostics")
	}
	switch v := s.State.(type) {
	case *k.Status_Running:
		if v == nil || v.Running == nil {
			return contract("missing running clock")
		}
	case *k.Status_Stopping:
		if v == nil || v.Stopping == nil || v.Stopping.PendingReason == nil || !diagnostic(v.Stopping.Detail) || v.Stopping.ActualPaused == nil {
			return contract("missing stopping facts")
		}
	case *k.Status_Stopped:
		if v == nil || v.Stopped == nil || v.Stopped.Reason == nil || !diagnostic(v.Stopped.Detail) || v.Stopped.ActualPaused == nil || v.Stopped.PauseVerified == nil || v.Stopped.PauseRequested == nil || v.Stopped.StoppedAtUnixMs == nil || v.Stopped.GetStoppedAtUnixMs() < 0 {
			return contract("missing stopped facts")
		}
	case *k.Status_NeverStarted:
		if v == nil || v.NeverStarted == nil {
			return contract("missing never-started state")
		}
	default:
		return contract("clock state required")
	}
	if e := clockStatusEpoch(s); e != nil {
		if err := clockEpoch(e); err != nil {
			return err
		}
	} else if s.GetNeverStarted() == nil {
		return contract("missing clock epoch")
	}
	page := s.EvidenceCompleteness
	if page == nil || page.Complete == nil || (page.GetComplete() && page.NextCursor != nil) {
		return contract("clock evidence completeness required")
	}
	if page.NextCursor != nil && validID(page.GetNextCursor()) != nil {
		return contract("clock evidence cursor")
	}
	for _, v := range s.BaselineAlerts {
		if v == nil || validID(v.GetKey()) != nil || !diagnostic(v.Label) || !diagnostic(v.Priority) {
			return contract("clock alert evidence")
		}
	}
	for _, v := range s.SuppressedInjuries {
		if v == nil || v.Pawn == nil || validID(v.Pawn.GetPawnId()) != nil || !diagnostic(v.Pawn.Name) || !diagnostic(v.Pawn.Reason) || v.Suppression == nil || v.NewWound == nil {
			return contract("clock injury evidence")
		}
		if p := v.Pawn.Position; p != nil && (p.X == nil || p.Z == nil || p.GetX() < 0 || p.GetZ() < 0) {
			return contract("clock injury position")
		}
		for _, h := range []*k.Health{v.Before, v.After} {
			if h != nil && ((h.InjuryCount != nil && h.GetInjuryCount() < 0) || (h.Severity != nil && h.GetSeverity() < 0) || (h.BleedRate != nil && h.GetBleedRate() < 0) || (h.BloodLoss != nil && (h.GetBloodLoss() < 0 || h.GetBloodLoss() > 1)) || (h.SummaryHealth != nil && (h.GetSummaryHealth() < 0 || h.GetSummaryHealth() > 1))) {
				return contract("clock injury health bounds")
			}
		}
	}
	return nil
}
func clockReceipt(r *k.ControlReceipt, identity *c.Identity, attempt *c.AttemptKey, owner *a.Owner, generation uint64) error {
	if r == nil {
		return contract("clock receipt required")
	}
	if err := errors.Join(clockWire(r), buildingAttempt(r.Attempt), ValidateContext(r.AdmittedContext), authorityOwner(r.AuthorizingOwner)); err != nil {
		return err
	}
	if !proto.Equal(r.Attempt, attempt) || !sameIdentity(r.AdmittedContext.Identity, identity) || r.AdmittedContext.NativeGeneration == nil || (generation != 0 && r.AdmittedContext.GetNativeGeneration() != generation) || r.AuthorizingOwner.GetControllerSessionId() != attempt.GetControllerSessionId() || (owner != nil && !proto.Equal(r.AuthorizingOwner, owner)) {
		return contract("clock receipt admission mismatch")
	}
	switch v := r.Outcome.(type) {
	case *k.ControlReceipt_Applied:
		if v == nil || v.Applied == nil {
			return contract("missing applied clock")
		}
		if err := clockStatus(v.Applied.Status, identity); err != nil {
			return err
		}
		if v.Applied.Status.Context.GetTick() < r.AdmittedContext.GetTick() {
			return contract("clock status before admission")
		}
		e := clockStatusEpoch(v.Applied.Status)
		if e == nil || e.Owner.GetControllerSessionId() != attempt.GetControllerSessionId() || !sameIdentity(e.Origin.Identity, identity) || e.Origin.GetTick() > r.AdmittedContext.GetTick() {
			return contract("clock applied epoch admission mismatch")
		}
	case *k.ControlReceipt_Uncertain:
		if v == nil || v.Uncertain == nil || !diagnostic(v.Uncertain.Detail) {
			return contract("missing uncertain clock")
		}
		if v.Uncertain.LastObserved != nil {
			if err := clockStatus(v.Uncertain.LastObserved, identity); err != nil {
				return err
			}
			if v.Uncertain.LastObserved.Context.GetTick() < r.AdmittedContext.GetTick() {
				return contract("clock uncertain status before admission")
			}
		}
	default:
		return contract("clock receipt outcome missing")
	}
	return nil
}

// Clock fields are typed at this boundary; reflection only rejects unknown wire
// fields, unsupported enums and nonfinite floats, including repeated evidence.
func clockWire(value proto.Message) error {
	if value == nil || !value.ProtoReflect().IsValid() {
		return contract("missing clock message")
	}
	var walk func(protoreflect.Message) error
	walk = func(m protoreflect.Message) error {
		if len(m.GetUnknown()) != 0 {
			return contract("unknown clock fields")
		}
		var err error
		m.Range(func(f protoreflect.FieldDescriptor, v protoreflect.Value) bool {
			check := func(v protoreflect.Value) error {
				switch f.Kind() {
				case protoreflect.MessageKind:
					return walk(v.Message())
				case protoreflect.EnumKind:
					if v.Enum() == 0 || f.Enum().Values().ByNumber(v.Enum()) == nil {
						return contract("unsupported clock enum")
					}
				case protoreflect.FloatKind, protoreflect.DoubleKind:
					if math.IsNaN(v.Float()) || math.IsInf(v.Float(), 0) {
						return contract("nonfinite clock value")
					}
				}
				return nil
			}
			if f.IsList() {
				for i := 0; i < v.List().Len(); i++ {
					if err = check(v.List().Get(i)); err != nil {
						break
					}
				}
			} else {
				err = check(v)
			}
			return err == nil
		})
		return err
	}
	return walk(value.ProtoReflect())
}
