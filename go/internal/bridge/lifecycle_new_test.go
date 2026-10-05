package bridge

import (
	"context"
	"errors"
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

func pbNewColonyRequest() *l.NewColonyRequest {
	return &l.NewColonyRequest{
		RequestId: proto.String("new-req-1"),
		TimeoutMs: proto.Uint32(600000),
		Spec: &l.NewColonySpec{
			Scenario:       proto.String("Crashlanded"),
			ColonistCount:  proto.Uint32(8),
			Seed:           proto.String("tribal8"),
			Biomes:         []string{"TemperateForest", "BorealForest"},
			FlatTile:       proto.Bool(true),
			Difficulty:     proto.String("Rough"),
			Storyteller:    proto.String("RimGovernorQuiet"),
			MinTemperature: proto.Float32(-10),
			MaxTemperature: proto.Float32(30),
			MapSize:        proto.Uint32(250),
			PlanetCoverage: proto.Float32(0.3),
			SaveName:       proto.String("tribal8"),
		},
	}
}

func pbNewColonyCompleted(request *l.NewColonyRequest) *l.NewColonyReply {
	return &l.NewColonyReply{Outcome: &l.NewColonyReply_Completed{Completed: &l.NewColonyCompleted{
		RequestId: request.RequestId, SaveName: request.Spec.SaveName, Context: pbContext(),
		Paused: proto.Bool(true), ByteLength: proto.Uint64(4096), Seed: proto.String("tribal8"),
	}}}
}

func newColonyClient(t *testing.T, reply *l.NewColonyReply) (*LifecycleNewColony, *testServer) {
	t.Helper()
	s := &testServer{schema: protoSchema}
	if reply != nil {
		s.handler = func(context.Context, nativeArgument) (*callResult, error) { return pbResult(reply), nil }
	}
	n, err := NewLifecycleNewColony(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	return n, s
}

func TestNewColonyHappyPathValidatesEchoedFields(t *testing.T) {
	request := pbNewColonyRequest()
	n, _ := newColonyClient(t, pbNewColonyCompleted(request))
	reply, raw, err := n.NewColony(context.Background(), request)
	if err != nil || reply.GetCompleted() == nil || len(raw.Envelope) == 0 {
		t.Fatalf("new colony %v %v", reply, err)
	}
	if reply.GetCompleted().GetSaveName() != "tribal8" || reply.GetCompleted().GetByteLength() != 4096 {
		t.Fatal("completed fields not preserved")
	}
}

func TestNewColonyRejectsInvalidRequestBeforeDispatch(t *testing.T) {
	for name, change := range map[string]func(*l.NewColonyRequest){
		"no request id":    func(r *l.NewColonyRequest) { r.RequestId = nil },
		"blank request id": func(r *l.NewColonyRequest) { r.RequestId = proto.String(" ") },
		"no timeout":       func(r *l.NewColonyRequest) { r.TimeoutMs = nil },
		"timeout low":      func(r *l.NewColonyRequest) { r.TimeoutMs = proto.Uint32(10) },
		"timeout high":     func(r *l.NewColonyRequest) { r.TimeoutMs = proto.Uint32(NewColonyMaxTimeoutMs + 1) },
		"no spec":          func(r *l.NewColonyRequest) { r.Spec = nil },
		"no scenario":      func(r *l.NewColonyRequest) { r.Spec.Scenario = nil },
		"no seed":          func(r *l.NewColonyRequest) { r.Spec.Seed = nil },
		"no difficulty":    func(r *l.NewColonyRequest) { r.Spec.Difficulty = nil },
		"no storyteller":   func(r *l.NewColonyRequest) { r.Spec.Storyteller = nil },
		"zero colonists":   func(r *l.NewColonyRequest) { r.Spec.ColonistCount = proto.Uint32(0) },
		"eleven colonists": func(r *l.NewColonyRequest) { r.Spec.ColonistCount = proto.Uint32(11) },
		"no colonists":     func(r *l.NewColonyRequest) { r.Spec.ColonistCount = nil },
		"duplicate biome":  func(r *l.NewColonyRequest) { r.Spec.Biomes = []string{"A", "A"} },
		"blank biome":      func(r *l.NewColonyRequest) { r.Spec.Biomes = []string{""} },
		"map too small":    func(r *l.NewColonyRequest) { r.Spec.MapSize = proto.Uint32(99) },
		"map too large":    func(r *l.NewColonyRequest) { r.Spec.MapSize = proto.Uint32(401) },
		"no map size":      func(r *l.NewColonyRequest) { r.Spec.MapSize = nil },
		"coverage low":     func(r *l.NewColonyRequest) { r.Spec.PlanetCoverage = proto.Float32(0.04) },
		"coverage high":    func(r *l.NewColonyRequest) { r.Spec.PlanetCoverage = proto.Float32(1.01) },
		"coverage nan":     func(r *l.NewColonyRequest) { r.Spec.PlanetCoverage = proto.Float32(float32(math.NaN())) },
		"no coverage":      func(r *l.NewColonyRequest) { r.Spec.PlanetCoverage = nil },
		"min above max":    func(r *l.NewColonyRequest) { r.Spec.MinTemperature = proto.Float32(31) },
		"temperature nan":  func(r *l.NewColonyRequest) { r.Spec.MaxTemperature = proto.Float32(float32(math.NaN())) },
		"temperature inf":  func(r *l.NewColonyRequest) { r.Spec.MaxTemperature = proto.Float32(float32(math.Inf(1))) },
		"no save name":     func(r *l.NewColonyRequest) { r.Spec.SaveName = nil },
		"save name slash":  func(r *l.NewColonyRequest) { r.Spec.SaveName = proto.String("../x") },
		"save name space":  func(r *l.NewColonyRequest) { r.Spec.SaveName = proto.String("my save") },
		"save name long":   func(r *l.NewColonyRequest) { r.Spec.SaveName = proto.String(string(make([]byte, 65))) },
		"blank world temp": func(r *l.NewColonyRequest) { r.Spec.WorldTemperature = proto.String("") },
		"too many biomes":  func(r *l.NewColonyRequest) { r.Spec.Biomes = make([]string, 33) },
	} {
		t.Run(name, func(t *testing.T) {
			request := pbNewColonyRequest()
			change(request)
			n, s := newColonyClient(t, nil)
			if _, _, err := n.NewColony(context.Background(), request); !errors.Is(err, ErrContract) {
				t.Fatalf("invalid request accepted: %v", err)
			}
			if len(s.calls) != 0 {
				t.Fatal("invalid request dispatched")
			}
		})
	}
}

func TestNewColonyAcceptsBoundaryValues(t *testing.T) {
	request := pbNewColonyRequest()
	spec := request.Spec
	spec.ColonistCount, spec.MapSize, spec.PlanetCoverage = proto.Uint32(1), proto.Uint32(100), proto.Float32(0.05)
	spec.MinTemperature, spec.MaxTemperature, spec.Biomes = nil, nil, nil
	request.TimeoutMs = proto.Uint32(NewColonyMinTimeoutMs)
	if err := ValidateNewColonyRequest(request); err != nil {
		t.Fatal(err)
	}
	spec.ColonistCount, spec.MapSize, spec.PlanetCoverage = proto.Uint32(10), proto.Uint32(400), proto.Float32(1)
	request.TimeoutMs = proto.Uint32(NewColonyMaxTimeoutMs)
	if err := ValidateNewColonyRequest(request); err != nil {
		t.Fatal(err)
	}
}

func TestNewColonyRejectsMismatchedCompleted(t *testing.T) {
	for name, change := range map[string]func(*l.NewColonyReply){
		"missing context":     func(r *l.NewColonyReply) { r.GetCompleted().Context = nil },
		"identity invalid":    func(r *l.NewColonyReply) { r.GetCompleted().Context.Identity.LoadToken = nil },
		"request id mismatch": func(r *l.NewColonyReply) { r.GetCompleted().RequestId = proto.String("other") },
		"save name mismatch":  func(r *l.NewColonyReply) { r.GetCompleted().SaveName = proto.String("other") },
		"missing paused":      func(r *l.NewColonyReply) { r.GetCompleted().Paused = nil },
		"missing byte length": func(r *l.NewColonyReply) { r.GetCompleted().ByteLength = nil },
		"missing seed":        func(r *l.NewColonyReply) { r.GetCompleted().Seed = nil },
		"empty outcome":       func(r *l.NewColonyReply) { r.Outcome = nil },
	} {
		t.Run(name, func(t *testing.T) {
			request := pbNewColonyRequest()
			reply := pbNewColonyCompleted(request)
			change(reply)
			n, _ := newColonyClient(t, reply)
			if _, _, err := n.NewColony(context.Background(), request); err == nil {
				t.Fatalf("invalid completed reply accepted (%s)", name)
			}
		})
	}
}

func TestNewColonyPendingOutcomeReturnsTypedRefusal(t *testing.T) {
	request := pbNewColonyRequest()
	pending := &l.NewColonyReply{Outcome: &l.NewColonyReply_Pending{Pending: &l.NewColonyPending{
		RequestId: request.RequestId, Phase: l.NewColonyPhase_NEW_COLONY_PHASE_ROLLING_COLONISTS.Enum(),
		ElapsedMs: proto.Uint64(1500), RerollCount: proto.Uint32(12),
	}}}
	n, _ := newColonyClient(t, pending)
	reply, raw, err := n.NewColony(context.Background(), request)
	var typed *NewColonyPending
	if !errors.As(err, &typed) || !errors.Is(err, ErrNewColonyPending) || reply != nil || len(raw.Envelope) == 0 {
		t.Fatalf("pending outcome not preserved: %v", err)
	}
	if typed.Value.GetRerollCount() != 12 || typed.Value.GetElapsedMs() != 1500 {
		t.Fatal(typed.Value)
	}
}

func TestNewColonyRejectsInvalidPending(t *testing.T) {
	request := pbNewColonyRequest()
	for name, pending := range map[string]*l.NewColonyPending{
		"no phase":          {RequestId: request.RequestId},
		"unspecified phase": {RequestId: request.RequestId, Phase: l.NewColonyPhase_NEW_COLONY_PHASE_UNSPECIFIED.Enum()},
		"unknown phase":     {RequestId: request.RequestId, Phase: l.NewColonyPhase(99).Enum()},
		"request mismatch":  {RequestId: proto.String("other"), Phase: l.NewColonyPhase_NEW_COLONY_PHASE_SAVING.Enum()},
	} {
		t.Run(name, func(t *testing.T) {
			n, _ := newColonyClient(t, &l.NewColonyReply{Outcome: &l.NewColonyReply_Pending{Pending: pending}})
			_, _, err := n.NewColony(context.Background(), request)
			if !errors.Is(err, ErrContract) {
				t.Fatalf("invalid pending accepted: %v", err)
			}
		})
	}
}

func TestNewColonySupersededOutcomeReturnsTypedRefusal(t *testing.T) {
	request := pbNewColonyRequest()
	superseded := &l.NewColonyReply{Outcome: &l.NewColonyReply_Superseded{Superseded: &l.NewColonySuperseded{
		RequestId: request.RequestId, Detail: proto.String("a newer request preempted this one"),
	}}}
	n, _ := newColonyClient(t, superseded)
	reply, _, err := n.NewColony(context.Background(), request)
	var typed *NewColonySuperseded
	if !errors.As(err, &typed) || !errors.Is(err, ErrNewColonySuperseded) || reply != nil {
		t.Fatalf("superseded outcome not preserved: %v", err)
	}
}

func TestNewColonyFailureOutcomeReturnsNativeFailure(t *testing.T) {
	request := pbNewColonyRequest()
	failed := &l.NewColonyReply{Outcome: &l.NewColonyReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Detail: proto.String("unknown scenario")}}}
	n, _ := newColonyClient(t, failed)
	reply, _, err := n.NewColony(context.Background(), request)
	var refusal *NativeFailure
	if !errors.As(err, &refusal) || reply != nil {
		t.Fatalf("native failure not preserved: %v", err)
	}
}

func TestReadNewColonyRequiresRequestID(t *testing.T) {
	n, s := newColonyClient(t, nil)
	if _, _, err := n.ReadNewColony(context.Background(), ""); !errors.Is(err, ErrContract) {
		t.Fatalf("empty request id accepted: %v", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid request dispatched")
	}
}

func TestReadNewColonyHappyPath(t *testing.T) {
	request := pbNewColonyRequest()
	n, _ := newColonyClient(t, pbNewColonyCompleted(request))
	reply, _, err := n.ReadNewColony(context.Background(), request.GetRequestId())
	if err != nil || reply.GetCompleted() == nil {
		t.Fatalf("read new colony %v %v", reply, err)
	}
}
