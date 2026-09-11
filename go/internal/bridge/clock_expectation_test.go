package bridge

import (
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func clockExpectationFixture() ClockExpectation {
	p := clockTestPre()
	return ClockExpectation{Identity: p.Identity, Attempt: p.Attempt, Owner: authorityTestOwner(), NativeGeneration: 7, Command: ClockCommand{Start: &ClockStart{Speed: k.Speed_SPEED_NORMAL, Policy: clockTestPolicy(), LeaseMS: 1000, MaxTicks: 100}}}
}
func TestClockExpectationInvalidEvidence(t *testing.T) {
	for name, edit := range map[string]func(*ClockExpectation){
		"no arm":           func(e *ClockExpectation) { e.Command = ClockCommand{} },
		"two arms":         func(e *ClockExpectation) { e.Command.Renew = &ClockRenew{Original: clockTestEpoch(), LeaseMS: 1000} },
		"missing identity": func(e *ClockExpectation) { e.Identity = nil },
		"missing attempt":  func(e *ClockExpectation) { e.Attempt = nil },
		"zero attempt":     func(e *ClockExpectation) { e.Attempt.AttemptId = proto.Uint64(0) },
		"zero generation":  func(e *ClockExpectation) { e.NativeGeneration = 0 },
		"foreign owner":    func(e *ClockExpectation) { e.Owner.ControllerSessionId = proto.String("foreign") },
		"zero direction":   func(e *ClockExpectation) { e.Owner.PlayerDirection = proto.Uint64(0) },
		"unknown owner":    func(e *ClockExpectation) { e.Owner.ProtoReflect().SetUnknown([]byte{0x78, 1}) },
		"zero budget":      func(e *ClockExpectation) { e.Command.Start.MaxTicks = 0 },
		"long budget":      func(e *ClockExpectation) { e.Command.Start.MaxTicks = 1800001 },
		"short lease":      func(e *ClockExpectation) { e.Command.Start.LeaseMS = 999 },
		"unknown speed":    func(e *ClockExpectation) { e.Command.Start.Speed = k.Speed(4) },
		"missing policy":   func(e *ClockExpectation) { e.Command.Start.Policy = nil },
		"nonfinite":        func(e *ClockExpectation) { e.Command.Start.Policy.HostileWithin = proto.Float32(float32(math.NaN())) },
		"medical budget": func(e *ClockExpectation) {
			e.Command.Start.Policy.MedicalRestIds = []string{"pawn"}
			e.Command.Start.MaxTicks = 601
		},
		"renew foreign epoch": func(e *ClockExpectation) {
			o := clockTestEpoch()
			o.Owner.ControllerSessionId = proto.String("foreign")
			e.Command = ClockCommand{Renew: &ClockRenew{Original: o, LeaseMS: 1000}}
		},
		"speed foreign world": func(e *ClockExpectation) {
			o := clockTestEpoch()
			o.Origin.Identity.LoadToken = proto.String("other")
			e.Command = ClockCommand{Speed: &ClockSpeed{Original: o, Speed: k.Speed_SPEED_FAST}}
		},
	} {
		t.Run(name, func(t *testing.T) {
			e := clockExpectationFixture()
			edit(&e)
			if ValidateClockExpectation(e) == nil {
				t.Fatal("invalid expectation accepted")
			}
		})
	}
}
func TestClockReceiptRecoveryCorrelatesFullAdmission(t *testing.T) {
	for name, edit := range map[string]func(*k.ControlReceipt){
		"direction":  func(r *k.ControlReceipt) { r.AuthorizingOwner.PlayerDirection = proto.Uint64(99) },
		"generation": func(r *k.ControlReceipt) { r.AdmittedContext.NativeGeneration = proto.Uint64(8) },
		"attempt":    func(r *k.ControlReceipt) { r.Attempt.AttemptId = proto.Uint64(2) },
		"policy": func(r *k.ControlReceipt) {
			r.GetApplied().Status.GetRunning().Epoch.Policy.HostileWithin = proto.Float32(25)
		},
		"speed": func(r *k.ControlReceipt) {
			r.GetApplied().Status.GetRunning().Epoch.RequestedSpeed = k.Speed_SPEED_FAST.Enum()
		},
		"budget": func(r *k.ControlReceipt) { r.GetApplied().Status.GetRunning().Epoch.TickDeadline = proto.Int64(113) },
		"lease": func(r *k.ControlReceipt) {
			r.GetApplied().Status.GetRunning().Epoch.LeaseRemainingMs = proto.Uint32(1001)
		},
		"origin": func(r *k.ControlReceipt) {
			r.GetApplied().Status.GetRunning().Epoch.Origin.NativeGeneration = proto.Uint64(8)
		},
	} {
		t.Run(name, func(t *testing.T) {
			r := clockTestReceipt()
			edit(r)
			if ValidateClockReceipt(r, clockExpectationFixture()) == nil {
				t.Fatal("uncorrelated receipt accepted")
			}
		})
	}
}
func TestClockExpectationOutcomesAndReadPurity(t *testing.T) {
	e := clockExpectationFixture()
	r := clockTestReceipt()
	before := proto.Clone(r)
	if err := ValidateClockReceipt(r, e); err != nil {
		t.Fatal(err)
	}
	if !proto.Equal(before, r) {
		t.Fatal("validator mutated receipt")
	}
	r.GetApplied().Status = clockTestStopped()
	if err := ValidateClockReceipt(r, e); err != nil {
		t.Fatal("immediately stopped start", err)
	}
	r.Outcome = &k.ControlReceipt_Uncertain{Uncertain: &k.UncertainControl{Detail: proto.String("lost observation")}}
	if err := ValidateClockReceipt(r, e); err != nil || r.GetUncertain() == nil {
		t.Fatal("uncertainty lost", err)
	}
	r.AuthorizingOwner.PlayerDirection = proto.Uint64(99)
	if ValidateClockReceipt(r, e) == nil {
		t.Fatal("uncertain foreign admission")
	}
	e = clockExpectationFixture()
	e.Attempt.AttemptId = proto.Uint64(math.MaxUint64)
	e.Owner.PlayerDirection = proto.Uint64(math.MaxUint64)
	e.NativeGeneration = math.MaxUint64
	if err := ValidateClockExpectation(e); err != nil {
		t.Fatal("uint64 precision", err)
	}
	if err := ValidateClockStatus(clockTestStopped(), pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if err := ValidateClockEpoch(clockTestEpoch()); err != nil {
		t.Fatal(err)
	}
	if ValidateClockStatus(nil, pbIdentity()) == nil || ValidateClockEpoch(nil) == nil {
		t.Fatal("missing status accepted")
	}
}
func TestClockRenewSpeedRecoveryImmutableEpoch(t *testing.T) {
	for _, speed := range []bool{false, true} {
		e := clockExpectationFixture()
		original := clockTestEpoch()
		r := clockTestReceipt()
		if speed {
			e.Command = ClockCommand{Speed: &ClockSpeed{Original: original, Speed: k.Speed_SPEED_FAST}}
			r.GetApplied().Status.GetRunning().Epoch.RequestedSpeed = k.Speed_SPEED_FAST.Enum()
		} else {
			e.Command = ClockCommand{Renew: &ClockRenew{Original: original, LeaseMS: 2000}}
			r.GetApplied().Status.GetRunning().Epoch.LeaseRemainingMs = proto.Uint32(1900)
		}
		if err := ValidateClockReceipt(r, e); err != nil {
			t.Fatal(err)
		}
		for _, edit := range []func(*k.Epoch){func(v *k.Epoch) { v.Owner.Epoch = proto.Int64(2) }, func(v *k.Epoch) { v.TickDeadline = proto.Int64(113) }, func(v *k.Epoch) { v.Policy.MinHealthFraction = proto.Float32(.4) }, func(v *k.Epoch) { v.Origin.NativeGeneration = proto.Uint64(8) }} {
			changed := proto.Clone(r).(*k.ControlReceipt)
			edit(changed.GetApplied().Status.GetRunning().Epoch)
			if ValidateClockReceipt(changed, e) == nil {
				t.Fatal("immutable epoch changed")
			}
		}
	}
}

func TestClockControlReplyEvidence(t *testing.T) {
	e := clockExpectationFixture()
	for _, reply := range []*k.ControlReply{
		{Outcome: &k.ControlReply_Receipt{Receipt: clockTestReceipt()}},
		{Outcome: &k.ControlReply_LongEventPending{LongEventPending: &k.LongEventPending{}}},
	} {
		if err := ValidateClockControlReply(reply, e); err != nil {
			t.Fatal(err)
		}
	}
	for _, code := range []c.FailureCode{c.FailureCode_FAILURE_CODE_INVALID_REQUEST, c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT} {
		reply := &k.ControlReply{Outcome: &k.ControlReply_Failure{Failure: &c.Failure{Code: code.Enum()}}}
		if err := ValidateClockControlReply(reply, e); err != nil {
			t.Fatal(err)
		}
	}
	for _, reply := range []*k.ControlReply{nil, {}, {Outcome: &k.ControlReply_Receipt{}}, {Outcome: &k.ControlReply_Failure{Failure: &c.Failure{}}}, {Outcome: &k.ControlReply_LongEventPending{}}} {
		if ValidateClockControlReply(reply, e) == nil {
			t.Fatal("missing evidence accepted", reply)
		}
	}
	reply := &k.ControlReply{Outcome: &k.ControlReply_LongEventPending{LongEventPending: &k.LongEventPending{}}}
	reply.GetLongEventPending().ProtoReflect().SetUnknown([]byte{0x78, 1})
	if ValidateClockControlReply(reply, e) == nil {
		t.Fatal("unknown pending fields")
	}
}
