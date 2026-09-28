package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/snapshotshm"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// bundleTestSnapshot is a frame at generation 7, tick 12: every section is
// the fixture the dedicated read's tests use.
func bundleTestSnapshot() *o.BundleSnapshot {
	emergency := emergencyFixture()
	emergency.Context = authorityTestContext(7)
	emergency.Colonists.Context = authorityTestContext(7)
	return &o.BundleSnapshot{Context: authorityTestContext(7), Paused: proto.Bool(false), ClockStatus: clockTestStatus(), Emergency: emergency}
}

// bundleServer answers the step's dedicated reads from its snapshot and
// counts every native call by tool.
type bundleServer struct {
	snapshot *o.BundleSnapshot
	calls    map[string]*atomic.Int64
}

func newBundleServer() *bundleServer {
	s := &bundleServer{snapshot: bundleTestSnapshot(), calls: map[string]*atomic.Int64{}}
	for _, name := range []string{"rimgovernor/lifecycle_read_tick", "rimgovernor/observations_read_status", "rimgovernor/clock_read_status"} {
		s.calls[name] = &atomic.Int64{}
	}
	return s
}

func (s *bundleServer) handle(_ context.Context, arg nativeArgument) (*callResult, error) {
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
	case "rimgovernor/lifecycle_read_tick":
		return pbResult(&l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: s.snapshot.Context, Paused: s.snapshot.Paused}}}), nil
	case "rimgovernor/observations_read_status":
		return pbResult(&o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: s.snapshot.Emergency}}), nil
	case "rimgovernor/clock_read_status":
		return pbResult(&k.StatusReply{Outcome: &k.StatusReply_Status{Status: s.snapshot.ClockStatus}}), nil
	}
	return &callResult{IsError: true}, nil
}

// retagContexts sets every ObservationContext inside message, at any depth,
// to context, so a fixture read under another tick joins a frame's.
func retagContexts(message proto.Message, context *c.ObservationContext) {
	var walk func(m protoreflect.Message)
	walk = func(m protoreflect.Message) {
		m.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
			if fd.Kind() != protoreflect.MessageKind || fd.IsMap() {
				return true
			}
			if fd.IsList() {
				for i := 0; i < v.List().Len(); i++ {
					walk(v.List().Get(i).Message())
				}
				return true
			}
			if _, ok := v.Message().Interface().(*c.ObservationContext); ok {
				m.Set(fd, protoreflect.ValueOfMessage(proto.Clone(context).ProtoReflect()))
				return true
			}
			walk(v.Message())
			return true
		})
	}
	walk(message.ProtoReflect())
}

// bundleFamilyServer's snapshot carries every census family, and it answers
// the dedicated family reads with the same rows, counting each.
type bundleFamilyServer struct {
	*bundleServer
	colony     *o.ColonyFactsReply
	population *o.PopulationReply
	research   *o.ResearchReply
	pawns      *o.ListPawnsReply
}

var bundleFamilyTools = []string{"rimgovernor/observations_read_colony_facts", "rimgovernor/observations_read_population", "rimgovernor/observations_read_research", "rimgovernor/observations_list_pawns"}

func newBundleFamilyServer(t *testing.T) *bundleFamilyServer {
	t.Helper()
	context := authorityTestContext(7)
	s := &bundleFamilyServer{bundleServer: newBundleServer()}
	for _, name := range bundleFamilyTools {
		s.calls[name] = &atomic.Int64{}
	}
	s.colony = colonyFixture(t)
	retagContexts(s.colony, context)
	s.population = populationReply(prisonerPerson("prisoner-1", ""))
	retagContexts(s.population, context)
	s.research = &o.ResearchReply{Outcome: &o.ResearchReply_Observed{Observed: &o.ResearchSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Snapshot: &o.SnapshotRef{Token: proto.String("research-token")}}}}
	pawns := combatPawnsFixture()
	pawns.Pawns[0].Settings = &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(false), Work: []*o.WorkSetting{{DefName: proto.String("Construction"), Priority: proto.Int32(3), Disabled: proto.Bool(false)}}}
	s.pawns = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: pawns}}
	s.snapshot.Emergency.Colonists.Pawns = []*o.PawnState{emergencyRow("pawn-1")}
	s.snapshot.Emergency.Colonists.Completeness = emergencyCounts(1)
	s.snapshot.ColonyFacts = proto.Clone(s.colony.GetObserved()).(*o.ColonyFactsSnapshot)
	s.snapshot.Population = proto.Clone(s.population.GetObserved()).(*o.PopulationSnapshot)
	s.snapshot.Research = proto.Clone(s.research.GetObserved()).(*o.ResearchSnapshot)
	s.snapshot.ColonistPawns = proto.Clone(pawns).(*o.PawnSnapshot)
	return s
}

func (s *bundleFamilyServer) handle(ctx context.Context, arg nativeArgument) (*callResult, error) {
	switch arg.Tool {
	case "rimgovernor/observations_read_colony_facts":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.colony), nil
	case "rimgovernor/observations_read_population":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.population), nil
	case "rimgovernor/observations_read_research":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.research), nil
	case "rimgovernor/observations_list_pawns":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.pawns), nil
	}
	return s.bundleServer.handle(ctx, arg)
}

// familyReads issues the routine census's family reads under ctx and
// reports how many crossed the bridge.
func (s *bundleFamilyServer) familyReads(t *testing.T, ctx context.Context, client *Client) int64 {
	t.Helper()
	before := s.familyCalls()
	if _, _, err := client.ReadColonyFacts(ctx, pbIdentity(), true); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadRoutinePopulation(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadResearch(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadRoutinePawns(ctx, pbIdentity(), []string{"pawn-1"}); err != nil {
		t.Fatal(err)
	}
	return s.familyCalls() - before
}

func (s *bundleFamilyServer) familyCalls() int64 {
	var n int64
	for _, name := range bundleFamilyTools {
		n += s.calls[name].Load()
	}
	return n
}

// fakeRing is a snapshot ring in memory.
type fakeRing struct {
	mu     sync.Mutex
	frames []snapshotshm.Frame
	writes int64
}

func (r *fakeRing) publish(t *testing.T, v *o.BundleSnapshot, writes int64) {
	payload, err := proto.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.frames = append(r.frames, snapshotshm.Frame{Number: uint64(len(r.frames) + 1), Writes: writes, Payload: payload})
}

func (r *fakeRing) Head() uint64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return uint64(len(r.frames))
}

func (r *fakeRing) Writes() int64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.writes
}

func (r *fakeRing) Latest() (snapshotshm.Frame, bool, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if len(r.frames) == 0 {
		return snapshotshm.Frame{}, false, nil
	}
	return r.frames[len(r.frames)-1], true, nil
}

func (r *fakeRing) Wait(ctx context.Context, after uint64, timeout time.Duration) bool {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) && ctx.Err() == nil {
		if r.Head() > after {
			return true
		}
		time.Sleep(time.Millisecond)
	}
	return false
}

func (r *fakeRing) Close() error { return nil }

// frameServer is the census family server that also opens the stream.
type frameServer struct {
	*bundleFamilyServer
	opens chan *o.SnapshotStreamRequest
}

func (s *frameServer) handle(ctx context.Context, arg nativeArgument) (*callResult, error) {
	if arg.Tool == methodOpenSnapshotStream {
		var wrapper struct {
			Request string `json:"request"`
		}
		request := &o.SnapshotStreamRequest{}
		if err := json.Unmarshal(arg.Arguments, &wrapper); err != nil {
			return nil, err
		}
		if err := protojson.Unmarshal([]byte(wrapper.Request), request); err != nil {
			return nil, err
		}
		s.opens <- request
		return pbResult(&o.SnapshotStreamReply{Outcome: &o.SnapshotStreamReply_Opened{Opened: &o.SnapshotStreamOpened{Name: "ring", Slots: 3, SlotBytes: 1 << 20}}}), nil
	}
	return s.bundleFamilyServer.handle(ctx, arg)
}

func frameClient(t *testing.T) (*Client, *frameServer, *fakeRing) {
	t.Helper()
	server := &frameServer{bundleFamilyServer: newBundleFamilyServer(t), opens: make(chan *o.SnapshotStreamRequest, 8)}
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	ring := &fakeRing{}
	client.frames = newFrameStream()
	client.frames.open = func(name string) (frameReader, error) {
		if name != "ring" {
			t.Errorf("mapped %q", name)
		}
		return ring, nil
	}
	return client, server, ring
}

// TestFramesServeTheStateFamilies: with a stream, the census family reads
// are served from the newest frame and never cross the bridge; a family no
// frame carries fails unavailable rather than reading natively.
func TestFramesServeTheStateFamilies(t *testing.T) {
	client, server, ring := frameClient(t)
	ring.publish(t, server.snapshot, 0)
	if n := server.familyReads(t, context.Background(), client); n != 0 {
		t.Fatalf("from a frame: %d native family reads, want 0", n)
	}
	lacking := proto.Clone(server.snapshot).(*o.BundleSnapshot)
	lacking.Research = nil
	ring.publish(t, lacking, 0)
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	if _, _, err := client.ReadResearch(ctx, pbIdentity()); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("frame without research: %v, want unavailable", err)
	}
	if n := server.familyCalls(); n != 0 {
		t.Fatalf("%d native family reads, want 0", n)
	}
}

// TestFramesServeColonyFactsWithoutPlanning (#984): a colony facts read
// without planning (acquisition, blight, resource, trade) is served from the
// frame with the planning section unrequested, never a native hop.
func TestFramesServeColonyFactsWithoutPlanning(t *testing.T) {
	client, server, ring := frameClient(t)
	ring.publish(t, server.snapshot, 0)
	for range 3 {
		reply, _, err := client.ReadColonyFacts(context.Background(), pbIdentity(), false)
		if err != nil {
			t.Fatal(err)
		}
		if got := reply.GetObserved().GetPlanning().GetUnavailable().GetReason(); got != c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED {
			t.Fatalf("planning section %v, want not requested", got)
		}
	}
	if n := server.calls["rimgovernor/observations_read_colony_facts"].Load(); n != 0 {
		t.Fatalf("%d native colony facts reads, want 0", n)
	}
}

// TestSnapshotFramesSummarize (#858): the native_frame rows fold into the
// phases report's frame size and cost line.
func TestSnapshotFramesSummarize(t *testing.T) {
	rows := []TimelineRecord{
		{Kind: "native_frame", Payload: map[string]any{"bytes": 1000.0, "capture_us": 300.0, "encode_us": 40.0, "write_us": 5.0, "decode_us": 90.0}},
		{Kind: "native_frame", Payload: map[string]any{"bytes": 3000.0, "capture_us": 500.0, "encode_us": 60.0, "write_us": 7.0, "decode_us": 110.0, "skipped": 2.0}},
	}
	summary := SummarizePhases(rows)
	if s := summary.Snapshots; s.Frames != 2 || s.Bytes != 4000 || s.MaxBytes != 3000 || s.MaxCapUs != 500 || s.Skipped != 2 {
		t.Fatalf("%+v", s)
	}
	var report strings.Builder
	WritePhaseReport(&report, summary)
	if !strings.Contains(report.String(), "snapshot frames: 2 read (2 skipped), bytes mean 2000 max 3000, capture us mean 400 max 500") {
		t.Fatal(report.String())
	}
}

// TestFramesReopenAfterClose: a reconnect (close) forgets the old ring and
// its write count, so the next read opens the new game's stream and serves
// its frames instead of waiting on writes the old ring counted.
func TestFramesReopenAfterClose(t *testing.T) {
	client, server, old := frameClient(t)
	old.publish(t, server.snapshot, 0)
	server.familyReads(t, context.Background(), client)
	<-server.opens
	old.mu.Lock()
	old.writes = 5
	old.mu.Unlock()
	client.noteFrameWrite()
	fresh := &fakeRing{}
	fresh.publish(t, server.snapshot, 0)
	client.frames.close()
	client.frames.open = func(string) (frameReader, error) { return fresh, nil }
	started := time.Now()
	if n := server.familyReads(t, context.Background(), client); n != 0 || time.Since(started) >= frameWriteWait {
		t.Fatalf("after reopen: %d native reads in %s", n, time.Since(started))
	}
}

// TestFramesWaitPastAWrite: after a write returns, a frame captured before
// it is not served; a frame captured past it is, and a write no frame
// catches up with fails the read unavailable.
func TestFramesWaitPastAWrite(t *testing.T) {
	client, server, ring := frameClient(t)
	ring.publish(t, server.snapshot, 0)
	server.familyReads(t, context.Background(), client)
	<-server.opens
	ring.mu.Lock()
	ring.writes = 1
	ring.mu.Unlock()
	client.noteFrameWrite()
	go func() {
		time.Sleep(20 * time.Millisecond)
		ring.publish(t, server.snapshot, 1)
	}()
	if n := server.familyReads(t, context.Background(), client); n != 0 {
		t.Fatalf("after a write: %d native reads, want 0 (the later frame)", n)
	}
	ring.mu.Lock()
	ring.writes = 2
	ring.mu.Unlock()
	client.noteFrameWrite()
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	if _, _, err := client.ReadResearch(ctx, pbIdentity()); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("stale frame past a write: %v, want unavailable", err)
	}
}

// TestFramesServeCombat (#851): the combat sections are read from the
// newest frame, whole, and a frame's rows must be valid; without a stream
// the read is unavailable.
func TestFramesServeCombat(t *testing.T) {
	client, server, ring := frameClient(t)
	v := proto.Clone(server.snapshot).(*o.BundleSnapshot)
	v.CombatPawns = []*mp.CombatPawn{{Id: proto.String("Human1"), Side: mp.CombatSide_COMBAT_SIDE_COLONIST.Enum(), Cell: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}}}
	v.CombatEvents = []*mp.CombatEventRow{
		{At: &mp.Watermark{Tick: proto.Int64(10)}, Kind: mp.CombatLogKind_COMBAT_LOG_KIND_SHOT_FIRED.Enum()},
		{At: &mp.Watermark{Tick: proto.Int64(10), Seq: proto.Uint32(1)}, Kind: mp.CombatLogKind_COMBAT_LOG_KIND_DOWNED.Enum()},
	}
	ring.publish(t, v, 0)
	state, err := client.ReadCombat(context.Background(), pbIdentity())
	if err != nil || len(state.Pawns) != 1 || len(state.Events) != 2 || CombatEventID(state.Events[1]) != "10.1" {
		t.Fatalf("%+v %v", state, err)
	}
	// Enemy drug facts (#1056) ride the row: absent reads false.
	if state.Pawns[0].GoJuiceHigh != nil || state.Pawns[0].GetLuciferiumAddicted() {
		t.Fatalf("drug facts on a clean row: %v", state.Pawns[0])
	}
	v.CombatPawns[0].GoJuiceHigh, v.CombatPawns[0].LuciferiumAddicted = proto.Bool(true), proto.Bool(true)
	ring.publish(t, v, 0)
	if state, err = client.ReadCombat(context.Background(), pbIdentity()); err != nil || !state.Pawns[0].GetGoJuiceHigh() || !state.Pawns[0].GetLuciferiumAddicted() {
		t.Fatalf("drug facts lost: %+v %v", state.Pawns, err)
	}
	v.CombatEvents[0], v.CombatEvents[1] = v.CombatEvents[1], v.CombatEvents[0]
	ring.publish(t, v, 0)
	if _, err := client.ReadCombat(context.Background(), pbIdentity()); !errors.Is(err, ErrContract) {
		t.Fatalf("out-of-order events: %v", err)
	}
	client.frames = nil
	if _, err := client.ReadCombat(context.Background(), pbIdentity()); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("no stream: %v", err)
	}
}

// TestFramesServeSubscribedDefinitions (#944): a routine read naming a
// planning definition subscribes the stream to it and waits for a frame
// carrying its row; the frame's rows reach the routine frame.
func TestFramesServeSubscribedDefinitions(t *testing.T) {
	client, server, ring := frameClient(t)
	ring.publish(t, server.snapshot, 0)
	if _, err := client.ReadRoutineFrame(context.Background(), pbIdentity(), nil); err != nil {
		t.Fatal(err)
	}
	if first := <-server.opens; len(first.GetDefinitions()) != 0 {
		t.Fatalf("first subscription %v", first.GetDefinitions())
	}
	carrying := proto.Clone(server.snapshot).(*o.BundleSnapshot)
	carrying.ProjectDefinitions = []*o.PlanningDefinition{{Definition: &o.DefinitionRef{DefName: proto.String("Hopper")}, Available: proto.Bool(true)}}
	go func() {
		request := <-server.opens
		if len(request.GetDefinitions()) != 1 || request.GetDefinitions()[0] != "Hopper" {
			t.Errorf("resubscription %v", request.GetDefinitions())
		}
		ring.publish(t, carrying, 0)
	}()
	frame, err := client.ReadRoutineFrame(context.Background(), pbIdentity(), []string{"Hopper"})
	if err != nil || len(frame.Definitions) != 1 || frame.Definitions[0].GetDefinition().GetDefName() != "Hopper" {
		t.Fatalf("%v %v", frame.Definitions, err)
	}
	if n := server.familyCalls(); n != 0 {
		t.Fatalf("%d native family reads, want 0", n)
	}
}
