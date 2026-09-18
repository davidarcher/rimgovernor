package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func bundleTestRequest() *o.BundleRequest {
	return &o.BundleRequest{ClockStatus: proto.Bool(true), Emergency: proto.Bool(true), Events: &o.BundleEventsRequest{AfterCursor: proto.Int64(0), Limit: proto.Uint32(128)}}
}

// bundleTestSnapshot is a full bundle at generation 7, tick 12: every
// section is the fixture the dedicated read's tests use.
func bundleTestSnapshot() *o.BundleSnapshot {
	emergency := emergencyFixture()
	emergency.Context = authorityTestContext(7)
	emergency.Colonists.Context = authorityTestContext(7)
	return &o.BundleSnapshot{Context: authorityTestContext(7), Paused: proto.Bool(false), ClockStatus: clockTestStatus(), Emergency: emergency, Events: clockEventPage(2)}
}

// bundleServer answers the bundle read with its snapshot and counts every
// native call by tool; the dedicated reads answer too, so a seeded step
// cache can be told apart from a round trip.
type bundleServer struct {
	snapshot *o.BundleSnapshot
	calls    map[string]*atomic.Int64
	request  *o.BundleRequest
}

func newBundleServer() *bundleServer {
	s := &bundleServer{snapshot: bundleTestSnapshot(), calls: map[string]*atomic.Int64{}}
	for _, name := range []string{bundleMethod, "rimgovernor/lifecycle_read_tick", "rimgovernor/observations_read_status", "rimgovernor/clock_read_status"} {
		s.calls[name] = &atomic.Int64{}
	}
	return s
}

func (s *bundleServer) handle(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
	if counter := s.calls[arg.Tool]; counter != nil {
		counter.Add(1)
	}
	var wrapper struct {
		Request string `json:"request"`
	}
	if err := json.Unmarshal(arg.Arguments, &wrapper); err != nil {
		return nil, err
	}
	switch arg.Tool {
	case bundleMethod:
		s.request = &o.BundleRequest{}
		if err := protojson.Unmarshal([]byte(wrapper.Request), s.request); err != nil {
			return nil, err
		}
		return pbResult(&o.BundleReply{Outcome: &o.BundleReply_Observed{Observed: s.snapshot}}), nil
	case "rimgovernor/lifecycle_read_tick":
		return pbResult(&l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: s.snapshot.Context, Paused: s.snapshot.Paused}}}), nil
	case "rimgovernor/observations_read_status":
		return pbResult(&o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: s.snapshot.Emergency}}), nil
	case "rimgovernor/clock_read_status":
		return pbResult(&k.StatusReply{Outcome: &k.StatusReply_Status{Status: s.snapshot.ClockStatus}}), nil
	}
	return &mcp.CallToolResult{IsError: true}, nil
}

func TestBundleReadCarriesEverySectionOfOneTick(t *testing.T) {
	server := newBundleServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	reply, raw, err := client.ReadBundle(context.Background(), bundleTestRequest())
	if err != nil || len(raw.Envelope) == 0 || !proto.Equal(server.request, bundleTestRequest()) {
		t.Fatal(reply, err, server.request)
	}
	observed := reply.GetObserved()
	if observed.GetContext().GetTick() != 12 || observed.GetPaused() || observed.GetClockStatus().GetRunning() == nil || len(observed.GetEvents().Events) != 2 {
		t.Fatal(observed)
	}
	emergency, err := BundleEmergency(observed)
	if err != nil || !proto.Equal(emergency.Context, authorityTestContext(7)) {
		t.Fatal(emergency, err)
	}
	if complete, known := emergency.Facts.ColonistsComplete.Value(); !complete || !known {
		t.Fatal("emergency section lost its completeness")
	}
	request := BundleEventsRequest(observed.Context.Identity, bundleTestRequest().Events)
	if !proto.Equal(request, clockEventsRequest()) {
		t.Fatal(request)
	}
	// A scoped bundle names the identity, and the sections the request left
	// out are absent.
	server.snapshot = &o.BundleSnapshot{Context: authorityTestContext(7), Paused: proto.Bool(true)}
	reply, _, err = client.ReadBundle(context.Background(), &o.BundleRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}})
	if err != nil || !reply.GetObserved().GetPaused() || reply.GetObserved().ClockStatus != nil || server.request.Scope == nil {
		t.Fatal(reply, err)
	}
	if _, err = BundleEmergency(reply.GetObserved()); err == nil {
		t.Fatal("emergency decoded from a bundle without the section")
	}
}

func TestBundleReadValidatesRequestAndSections(t *testing.T) {
	server := newBundleServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	for name, request := range map[string]*o.BundleRequest{
		"nil":            nil,
		"scope identity": {Scope: &o.ReadScope{ExpectedIdentity: &c.Identity{ColonyId: proto.String("colony")}}},
		"events cursor":  {Events: &o.BundleEventsRequest{AfterCursor: proto.Int64(-1), Limit: proto.Uint32(1)}},
		"events limit":   {Events: &o.BundleEventsRequest{AfterCursor: proto.Int64(0), Limit: proto.Uint32(129)}},
		"events wait":    {Events: &o.BundleEventsRequest{AfterCursor: proto.Int64(0), Limit: proto.Uint32(1), WaitMs: proto.Uint32(ClockEventsMaxWaitMs + 1)}},
	} {
		if _, _, err := client.ReadBundle(context.Background(), request); !errors.Is(err, ErrContract) {
			t.Fatal(name, err)
		}
	}
	if server.calls[bundleMethod].Load() != 0 {
		t.Fatal("an invalid request crossed the bridge")
	}
	cases := map[string]func(*o.BundleSnapshot){
		"pause missing":         func(s *o.BundleSnapshot) { s.Paused = nil },
		"section unrequested":   func(s *o.BundleSnapshot) { s.Events = nil },
		"clock status tick":     func(s *o.BundleSnapshot) { s.ClockStatus.Context.Tick = proto.Int64(13) },
		"clock status identity": func(s *o.BundleSnapshot) { s.ClockStatus.Context.Identity.LoadToken = proto.String("other") },
		"emergency tick": func(s *o.BundleSnapshot) {
			s.Emergency.Context.Tick = proto.Int64(13)
			s.Emergency.Colonists.Context.Tick = proto.Int64(13)
		},
		"emergency identity":       func(s *o.BundleSnapshot) { s.Emergency.Context.Identity.LoadToken = proto.String("other") },
		"emergency section":        func(s *o.BundleSnapshot) { s.Emergency.Threats = nil },
		"events tick":              func(s *o.BundleSnapshot) { s.Events.Context.Tick = proto.Int64(13) },
		"events page":              func(s *o.BundleSnapshot) { s.Events.NextCursor = nil },
		"scope identity mismatch":  func(s *o.BundleSnapshot) { s.Context.Identity.LoadToken = proto.String("other") },
		"context invalid":          func(s *o.BundleSnapshot) { s.Context.Tick = proto.Int64(-1) },
		"clock status unavailable": func(s *o.BundleSnapshot) { s.ClockStatus.State = nil },
	}
	for name, mutate := range cases {
		server.snapshot = bundleTestSnapshot()
		mutate(server.snapshot)
		request := bundleTestRequest()
		request.Scope = &o.ReadScope{ExpectedIdentity: pbIdentity()}
		reply, _, err := client.ReadBundle(context.Background(), request)
		if err == nil || reply != nil {
			t.Fatal(name, reply, err)
		}
	}
	// A clock status the native reports unavailable is the same refusal
	// ReadClockStatus reports, with the reply kept for the raw result.
	server.snapshot = bundleTestSnapshot()
	server.snapshot.ClockStatus = &k.Status{Context: authorityTestContext(7), State: &k.Status_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum()}}}
	if reply, _, err := client.ReadBundle(context.Background(), bundleTestRequest()); !errors.Is(err, ErrUnavailable) || reply == nil {
		t.Fatal(reply, err)
	}
	for _, outcome := range []*o.BundleReply{
		{Outcome: &o.BundleReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum()}}},
		{Outcome: &o.BundleReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum()}}},
		{},
	} {
		refusing := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) { return pbResult(outcome), nil }}, time.Second)
		if _, _, err := refusing.ReadBundle(context.Background(), bundleTestRequest()); err == nil {
			t.Fatal(outcome)
		}
	}
}

// TestBundleReadSeedsTheStepCache: the bundle itself is never memoized, but
// its tick and emergency sections serve the step's Tick and ReadEmergency
// reads and the parent's identity and emergency families, which their
// invalidation drops as if the dedicated reads had filled them.
func TestBundleReadSeedsTheStepCache(t *testing.T) {
	server := newBundleServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	parent := NewFactCache()
	cache := NewChildReadCache(parent)
	ctx := WithStepReadCache(context.Background(), cache)
	for i := 0; i < 2; i++ {
		if _, _, err := client.ReadBundle(ctx, bundleTestRequest()); err != nil {
			t.Fatal(err)
		}
	}
	if server.calls[bundleMethod].Load() != 2 {
		t.Fatal("bundle memoized", server.calls[bundleMethod].Load())
	}
	tick, _, err := client.Tick(ctx)
	if err != nil || tick.GetLoaded().GetContext().GetTick() != 12 || tick.GetLoaded().GetPaused() {
		t.Fatal(tick, err)
	}
	emergency, _, err := client.ReadEmergency(ctx, pbIdentity())
	if err != nil || emergency.Context.GetTick() != 12 {
		t.Fatal(emergency, err)
	}
	if _, _, err = client.ReadClockStatus(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if n := server.calls["rimgovernor/lifecycle_read_tick"].Load() + server.calls["rimgovernor/observations_read_status"].Load(); n != 0 {
		t.Fatal("seeded sections crossed the bridge", n)
	}
	if server.calls["rimgovernor/clock_read_status"].Load() != 1 {
		t.Fatal("live clock status served from the bundle")
	}
	if stats := cache.Stats(); stats.Hits != 2 {
		t.Fatalf("%+v", stats)
	}
	if parent.Len() != 2 {
		t.Fatal(parent.Len())
	}
	// A later step at the same scope fixes its scope natively (a bundle in
	// the scheduler, the tick here) and then reads the seeded emergency
	// row from the parent; the emergency family's invalidation drops it.
	step := func(t *testing.T) {
		cache = NewChildReadCache(parent)
		ctx = WithStepReadCache(context.Background(), cache)
		if _, _, err = client.Tick(ctx); err != nil {
			t.Fatal(err)
		}
		if _, _, err = client.ReadEmergency(ctx, pbIdentity()); err != nil {
			t.Fatal(err)
		}
	}
	step(t)
	if tick, status := server.calls["rimgovernor/lifecycle_read_tick"].Load(), server.calls["rimgovernor/observations_read_status"].Load(); tick != 1 || status != 0 {
		t.Fatal(tick, status)
	}
	parent.InvalidateFamilies(FactEmergency)
	step(t)
	if tick, status := server.calls["rimgovernor/lifecycle_read_tick"].Load(), server.calls["rimgovernor/observations_read_status"].Load(); tick != 2 || status != 1 {
		t.Fatal(tick, status)
	}
	// Without a step cache the bundle seeds nothing.
	if _, _, err = client.ReadBundle(context.Background(), bundleTestRequest()); err != nil {
		t.Fatal(err)
	}
	if _, _, err = client.Tick(context.Background()); err != nil || server.calls["rimgovernor/lifecycle_read_tick"].Load() != 3 {
		t.Fatal(err, "tick served without a native read")
	}
}
