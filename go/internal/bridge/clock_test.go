package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"testing"
	"time"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func clockTestPolicy() *k.WatchPolicy {
	return &k.WatchPolicy{Mode: k.WatchMode_WATCH_MODE_COLONY.Enum(), HealthDropFraction: proto.Float32(.1), MinHealthFraction: proto.Float32(.2), HostileWithin: proto.Float32(20), InjuryStopCooldownMs: proto.Uint32(0)}
}
func clockTestPre() *a.WritePrecondition {
	return &a.WritePrecondition{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(7), LeaseId: proto.String("lease"), Attempt: &c.AttemptKey{ControllerSessionId: proto.String("controller"), ActionId: proto.String("clock-start"), AttemptId: proto.Uint64(1)}}
}
func clockTestStart() *k.StartRequest {
	return &k.StartRequest{Authority: clockTestPre(), Speed: k.Speed_SPEED_NORMAL.Enum(), Policy: clockTestPolicy(), LeaseMs: proto.Uint32(1000), MaxTicks: proto.Uint32(100)}
}
func clockTestEpoch() *k.Epoch {
	return &k.Epoch{Owner: &k.EpochOwner{ControllerSessionId: proto.String("controller"), Epoch: proto.Int64(1)}, Origin: authorityTestContext(7), RequestedSpeed: k.Speed_SPEED_NORMAL.Enum(), Policy: clockTestPolicy(), StartTick: proto.Int64(12), TickDeadline: proto.Int64(112), LeaseRemainingMs: proto.Uint32(900), LastTick: proto.Int64(12)}
}
func clockTestStatus() *k.Status {
	return &k.Status{Context: authorityTestContext(7), State: &k.Status_Running{Running: &k.Running{Epoch: clockTestEpoch()}}, NativeTickBoundary: proto.Bool(true), DurableEvents: proto.Bool(true), NewestCursor: proto.Int64(0), ObservedSpeed: k.ObservedSpeed_OBSERVED_SPEED_NORMAL.Enum(), ActualPaused: proto.Bool(false), EvidenceCompleteness: &c.PageInfo{Complete: proto.Bool(true)}}
}
func clockTestReceipt() *k.ControlReceipt {
	return &k.ControlReceipt{Attempt: clockTestPre().Attempt, AdmittedContext: authorityTestContext(7), AuthorizingOwner: authorityTestOwner(), Outcome: &k.ControlReceipt_Applied{Applied: &k.AppliedControl{Status: clockTestStatus()}}}
}
func clockTestOwned() *k.OwnedRequest {
	return &k.OwnedRequest{Identity: pbIdentity(), Owner: clockTestEpoch().Owner}
}
func clockTestStopped() *k.Status {
	s := clockTestStatus()
	s.ActualPaused = proto.Bool(true)
	s.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum()
	s.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: clockTestEpoch(), Reason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(true), StoppedAtUnixMs: proto.Int64(123)}}
	return s
}
func TestClockFixedSDKOperations(t *testing.T) {
	var methods []string
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		methods = append(methods, arg.Tool)
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		var req proto.Message
		switch arg.Tool {
		case "rimgovernor/clock_read_status":
			req = &k.StatusRequest{Identity: pbIdentity()}
		case "rimgovernor/clock_read_attempt":
			req = &k.AttemptRequest{Identity: pbIdentity(), Attempt: clockTestPre().Attempt}
		case "rimgovernor/clock_start":
			req = clockTestStart()
		case "rimgovernor/clock_renew":
			req = &k.RenewRequest{Epoch: clockTestOwned(), Authority: clockTestPre(), LeaseMs: proto.Uint32(2000)}
		case "rimgovernor/clock_change_speed":
			req = &k.SpeedRequest{Epoch: clockTestOwned(), Authority: clockTestPre(), Speed: k.Speed_SPEED_FAST.Enum()}
		case "rimgovernor/clock_pause":
			req = clockTestOwned()
		default:
			t.Fatal(arg.Tool)
		}
		parsed := req.ProtoReflect().New().Interface()
		if err := protojson.Unmarshal([]byte(outer.Request), parsed); err != nil || !proto.Equal(parsed, req) {
			t.Fatal(parsed, req, err)
		}
		switch arg.Tool {
		case "rimgovernor/clock_read_status":
			return pbResult(&k.StatusReply{Outcome: &k.StatusReply_Status{Status: clockTestStatus()}}), nil
		case "rimgovernor/clock_read_attempt":
			return pbResult(&k.AttemptReply{Outcome: &k.AttemptReply_Receipt{Receipt: clockTestReceipt()}}), nil
		case "rimgovernor/clock_pause":
			s := clockTestStopped()
			s.Context.NativeGeneration = proto.Uint64(8)
			return pbResult(&k.StatusReply{Outcome: &k.StatusReply_Status{Status: s}}), nil
		default:
			r := clockTestReceipt()
			if arg.Tool == "rimgovernor/clock_renew" {
				r.GetApplied().Status.GetRunning().Epoch.LeaseRemainingMs = proto.Uint32(1900)
			}
			if arg.Tool == "rimgovernor/clock_change_speed" {
				r.GetApplied().Status.GetRunning().Epoch.RequestedSpeed = k.Speed_SPEED_FAST.Enum()
			}
			return pbResult(&k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: r}}), nil
		}
	}}, time.Second)
	control, _ := NewClockControl(client)
	ctx := context.Background()
	if _, _, err := client.ReadClockStatus(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadClockAttempt(ctx, &k.AttemptRequest{Identity: pbIdentity(), Attempt: clockTestPre().Attempt}); err != nil {
		t.Fatal(err)
	}
	if _, raw, err := control.Start(ctx, clockTestStart(), authorityTestOwner()); err != nil || len(raw.Envelope) == 0 {
		t.Fatal(err, raw)
	}
	if _, _, err := control.Renew(ctx, &k.RenewRequest{Epoch: clockTestOwned(), Authority: clockTestPre(), LeaseMs: proto.Uint32(2000)}, clockTestEpoch(), authorityTestOwner()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := control.ChangeSpeed(ctx, &k.SpeedRequest{Epoch: clockTestOwned(), Authority: clockTestPre(), Speed: k.Speed_SPEED_FAST.Enum()}, clockTestEpoch(), authorityTestOwner()); err != nil {
		t.Fatal(err)
	}
	if reply, _, err := control.OwnedPause(ctx, clockTestOwned()); err != nil || !reply.GetStatus().GetStopped().GetPauseVerified() {
		t.Fatal(reply, err)
	}
	for _, name := range []string{"rimgovernor/clock_start", "rimgovernor/clock_renew", "rimgovernor/clock_change_speed", "rimgovernor/clock_pause"} {
		if _, err := client.protoRead(ctx, name, clockTestStart(), &k.ControlReply{}); !errors.Is(err, ErrContract) {
			t.Fatal(name, err)
		}
	}
	if len(methods) != 6 {
		t.Fatal(methods)
	}
}

func TestClockInvalidStartNeverCalls(t *testing.T) {
	for name, edit := range map[string]func(*k.StartRequest){
		"missing policy": func(r *k.StartRequest) { r.Policy = nil }, "missing fraction": func(r *k.StartRequest) { r.Policy.MinHealthFraction = nil }, "NaN": func(r *k.StartRequest) { r.Policy.HostileWithin = proto.Float32(float32(math.NaN())) }, "zero budget": func(r *k.StartRequest) { r.MaxTicks = proto.Uint32(0) }, "large budget": func(r *k.StartRequest) { r.MaxTicks = proto.Uint32(1800001) }, "short lease": func(r *k.StartRequest) { r.LeaseMs = proto.Uint32(999) }, "long lease": func(r *k.StartRequest) { r.LeaseMs = proto.Uint32(30001) }, "foreign owner": func(r *k.StartRequest) { r.Authority.Attempt.ControllerSessionId = proto.String("other") }, "zero generation": func(r *k.StartRequest) { r.Authority.ExpectedGeneration = proto.Uint64(0) }, "no attempt": func(r *k.StartRequest) { r.Authority.Attempt = nil }, "fixture speed": func(r *k.StartRequest) { r.Speed = k.Speed(4).Enum() }, "duplicate ID": func(r *k.StartRequest) { r.Policy.AcknowledgedHostileIds = []string{"pawn", "pawn"} }, "medical rest budget": func(r *k.StartRequest) { r.Policy.MedicalRestIds = []string{"pawn"}; r.MaxTicks = proto.Uint32(601) }, "unknown wire": func(r *k.StartRequest) { r.ProtoReflect().SetUnknown([]byte{0x78, 1}) },
	} {
		t.Run(name, func(t *testing.T) {
			r := clockTestStart()
			edit(r)
			control := &ClockControl{}
			if _, _, err := control.Start(context.Background(), r, authorityTestOwner()); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
}
func TestClockStatusPreservesStoppedOriginAndUnknownFacts(t *testing.T) {
	s := clockTestStopped()
	s.Context.Identity.LoadToken = proto.String("new-load")
	if err := clockStatus(s, s.Context.Identity); err != nil {
		t.Fatal(err)
	}
	s.SuppressedInjuries = []*k.Injury{{Pawn: &k.PawnEvent{PawnId: proto.String("pawn")}, Before: &k.Health{}, After: &k.Health{}, Suppression: k.InjurySuppression_INJURY_SUPPRESSION_ACKNOWLEDGED.Enum(), NewWound: proto.Bool(true)}}
	s.EvidenceCompleteness.Complete = proto.Bool(false)
	if err := clockStatus(s, s.Context.Identity); err != nil {
		t.Fatal(err)
	}
	if s.SuppressedInjuries[0].Before.InjuryCount != nil {
		t.Fatal("unknown health fabricated")
	}
	for name, edit := range map[string]func(*k.Status){"missing state": func(s *k.Status) { s.State = nil }, "wrong identity": func(s *k.Status) { s.Context.Identity.LoadToken = proto.String("other") }, "negative cursor": func(s *k.Status) { s.NewestCursor = proto.Int64(-1) }, "missing pause": func(s *k.Status) { s.ActualPaused = nil }, "deadline": func(s *k.Status) { s.GetRunning().Epoch.TickDeadline = proto.Int64(12) }, "missing epoch": func(s *k.Status) { s.GetRunning().Epoch = nil }, "unknown enum": func(s *k.Status) { s.ObservedSpeed = k.ObservedSpeed(99).Enum() }, "false complete": func(s *k.Status) { s.EvidenceCompleteness.NextCursor = proto.String("more") }} {
		t.Run(name, func(t *testing.T) {
			s := clockTestStatus()
			edit(s)
			if err := clockStatus(s, pbIdentity()); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
}
func TestClockControlRejectsMismatchedAppliedReceipt(t *testing.T) {
	for name, edit := range map[string]func(*k.ControlReceipt){"attempt": func(r *k.ControlReceipt) { r.Attempt.AttemptId = proto.Uint64(2) }, "direction": func(r *k.ControlReceipt) { r.AuthorizingOwner.PlayerDirection = proto.Uint64(4) }, "generation": func(r *k.ControlReceipt) { r.AdmittedContext.NativeGeneration = proto.Uint64(8) }, "identity": func(r *k.ControlReceipt) { r.GetApplied().Status.Context.Identity.LoadToken = proto.String("other") }, "deadline": func(r *k.ControlReceipt) { r.GetApplied().Status.GetRunning().Epoch.TickDeadline = proto.Int64(113) }, "owner": func(r *k.ControlReceipt) {
		r.GetApplied().Status.GetRunning().Epoch.Owner.ControllerSessionId = proto.String("other")
	}, "long lease": func(r *k.ControlReceipt) {
		r.GetApplied().Status.GetRunning().Epoch.LeaseRemainingMs = proto.Uint32(1001)
	}} {
		t.Run(name, func(t *testing.T) {
			calls := 0
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				calls++
				r := clockTestReceipt()
				edit(r)
				return pbResult(&k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: r}}), nil
			}}, time.Second)
			control, _ := NewClockControl(client)
			_, raw, err := control.Start(context.Background(), clockTestStart(), authorityTestOwner())
			var uncertain *ClockUncertain
			if !errors.As(err, &uncertain) || !errors.Is(err, ErrContract) || calls != 1 || len(raw.Envelope) == 0 {
				t.Fatal(err, calls)
			}
		})
	}
}
func TestClockImmutableEpochAndRenewal(t *testing.T) {
	original := clockTestEpoch()
	changed := proto.Clone(original).(*k.Epoch)
	changed.LeaseRemainingMs = proto.Uint32(2000)
	changed.LastTick = proto.Int64(13)
	if err := clockSameEpoch(changed, original, k.Speed_SPEED_NORMAL); err != nil {
		t.Fatal(err)
	}
	changed.TickDeadline = proto.Int64(113)
	if err := clockSameEpoch(changed, original, k.Speed_SPEED_NORMAL); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
func TestClockTypedFailureUnknownAndUncertainty(t *testing.T) {
	for _, kind := range []string{"failure", "uncertain", "pending", "malformed", "unknown"} {
		t.Run(kind, func(t *testing.T) {
			calls := 0
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				calls++
				switch kind {
				case "failure":
					r := pbResult(&k.ControlReply{Outcome: &k.ControlReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum()}}})
					r.IsError = true
					return r, nil
				case "uncertain":
					r := clockTestReceipt()
					r.Outcome = &k.ControlReceipt_Uncertain{Uncertain: &k.UncertainControl{Detail: proto.String("inspection needed")}}
					return pbResult(&k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: r}}), nil
				case "pending":
					return pbResult(&k.ControlReply{Outcome: &k.ControlReply_LongEventPending{LongEventPending: &k.LongEventPending{}}}), nil
				case "unknown":
					return pbResult(&k.AttemptReply{Outcome: &k.AttemptReply_Unknown{Unknown: &k.AttemptUnknown{}}}), nil
				default:
					return pbResult(&k.ControlReply{}), nil
				}
			}}, time.Second)
			control, _ := NewClockControl(client)
			if kind == "unknown" {
				r, _, err := client.ReadClockAttempt(context.Background(), &k.AttemptRequest{Identity: pbIdentity(), Attempt: clockTestPre().Attempt})
				if err != nil || r.GetUnknown() == nil {
					t.Fatal(r, err)
				}
				return
			}
			r, raw, err := control.Start(context.Background(), clockTestStart(), authorityTestOwner())
			var uncertain *ClockUncertain
			switch kind {
			case "failure":
				if !errors.Is(err, ErrRefused) || errors.As(err, &uncertain) {
					t.Fatal(err)
				}
			case "pending":
				if err != nil || r.GetLongEventPending() == nil {
					t.Fatal(r, err)
				}
			default:
				if !errors.As(err, &uncertain) {
					t.Fatal(err)
				}
			}
			if kind == "uncertain" && r.GetReceipt().GetUncertain() == nil {
				t.Fatal("lost native uncertainty")
			}
			if calls != 1 || len(raw.Envelope) == 0 {
				t.Fatal(calls)
			}
		})
	}
}
func TestClockCancellationDoesNotDispatch(t *testing.T) {
	calls := 0
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		return nil, errors.New("unexpected")
	}}, time.Second)
	control, _ := NewClockControl(client)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_, _, err := control.Start(ctx, clockTestStart(), authorityTestOwner())
	var uncertain *ClockUncertain
	if !errors.Is(err, context.Canceled) || errors.As(err, &uncertain) || calls != 0 {
		t.Fatal(err, calls)
	}
}

func TestClockTransportDeadlineIsUncertainWithoutRetry(t *testing.T) {
	entered := make(chan struct{}, 1)
	client := testClient(t, &testServer{schema: protoSchema, handler: func(ctx context.Context, _ nativeArgument) (*mcp.CallToolResult, error) {
		entered <- struct{}{}
		<-ctx.Done()
		return nil, ctx.Err()
	}}, time.Second)
	control, _ := NewClockControl(client)
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	_, _, err := control.Start(ctx, clockTestStart(), authorityTestOwner())
	var uncertain *ClockUncertain
	if !errors.As(err, &uncertain) || !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal(err)
	}
	if len(entered) != 1 {
		t.Fatal("clock dispatch was missing or repeated", len(entered))
	}
}

func TestClockPauseRequiresOriginalEpochAndRetainsFailedPause(t *testing.T) {
	for _, kind := range []string{"stopping", "replacement", "running"} {
		t.Run(kind, func(t *testing.T) {
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				s := clockTestStopped()
				if kind == "replacement" {
					s.GetStopped().Epoch.Owner.Epoch = proto.Int64(2)
				}
				if kind == "running" {
					s = clockTestStatus()
				}
				if kind == "stopping" {
					s.State = &k.Status_Stopping{Stopping: &k.Stopping{Epoch: clockTestEpoch(), PendingReason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), ActualPaused: proto.Bool(false)}}
					s.ActualPaused = proto.Bool(false)
					s.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_NORMAL.Enum()
				}
				return pbResult(&k.StatusReply{Outcome: &k.StatusReply_Status{Status: s}}), nil
			}}, time.Second)
			control, _ := NewClockControl(client)
			r, _, err := control.OwnedPause(context.Background(), clockTestOwned())
			if kind == "stopping" {
				if err != nil || r.GetStatus().GetStopping() == nil || r.GetStatus().GetActualPaused() {
					t.Fatal(r, err)
				}
			} else {
				var uncertain *ClockUncertain
				if !errors.As(err, &uncertain) {
					t.Fatal(err)
				}
			}
		})
	}
}

func TestClockAppliedStartCanImmediatelyStop(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		r := clockTestReceipt()
		s := clockTestStopped()
		s.GetStopped().Reason = k.StopReason_STOP_REASON_COLONIST_DOWNED.Enum()
		r.GetApplied().Status = s
		return pbResult(&k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: r}}), nil
	}}, time.Second)
	control, _ := NewClockControl(client)
	r, _, err := control.Start(context.Background(), clockTestStart(), authorityTestOwner())
	if err != nil || r.GetReceipt().GetApplied().GetStatus().GetStopped() == nil {
		t.Fatal(r, err)
	}
}

func TestClockLookupRejectsForeignAttemptAndPreservesTypedReadFailure(t *testing.T) {
	for _, kind := range []string{"foreign", "failure", "status failure"} {
		t.Run(kind, func(t *testing.T) {
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				if kind == "foreign" {
					r := clockTestReceipt()
					r.Attempt.AttemptId = proto.Uint64(2)
					return pbResult(&k.AttemptReply{Outcome: &k.AttemptReply_Receipt{Receipt: r}}), nil
				}
				failure := &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum()}
				var value proto.Message = &k.AttemptReply{Outcome: &k.AttemptReply_Failure{Failure: failure}}
				if kind == "status failure" {
					value = &k.StatusReply{Outcome: &k.StatusReply_Failure{Failure: failure}}
				}
				r := pbResult(value)
				r.IsError = true
				return r, nil
			}}, time.Second)
			var err error
			if kind == "status failure" {
				_, _, err = client.ReadClockStatus(context.Background(), pbIdentity())
			} else {
				_, _, err = client.ReadClockAttempt(context.Background(), &k.AttemptRequest{Identity: pbIdentity(), Attempt: clockTestPre().Attempt})
			}
			expected := ErrRefused
			if kind == "foreign" {
				expected = ErrContract
			}
			if !errors.Is(err, expected) {
				t.Fatal(err)
			}
		})
	}
}
