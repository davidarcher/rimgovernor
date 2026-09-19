package bridge

import (
	"context"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

// readCacheServer answers identity and world reads from a mutable
// generation, counting the native calls each method receives, and accepts a
// clock pause as the step's write.
type readCacheServer struct {
	generation  atomic.Uint64
	tick        atomic.Int64
	calls       map[string]*atomic.Int64
	release     chan struct{}
	unavailable atomic.Bool
}

func newReadCacheServer() *readCacheServer {
	s := &readCacheServer{calls: map[string]*atomic.Int64{}}
	for _, name := range []string{"rimgovernor/lifecycle_read_identity", "rimgovernor/lifecycle_read_tick", "rimgovernor/observations_read_world", "rimgovernor/observations_list_rooms", "rimgovernor/clock_pause"} {
		s.calls[name] = &atomic.Int64{}
	}
	s.generation.Store(1)
	s.tick.Store(1000)
	return s
}

func (s *readCacheServer) context() *c.ObservationContext {
	return &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(s.tick.Load()), NativeGeneration: proto.Uint64(s.generation.Load())}
}

func (s *readCacheServer) handle(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
	if counter := s.calls[arg.Tool]; counter != nil {
		counter.Add(1)
	}
	if s.release != nil {
		<-s.release
	}
	switch arg.Tool {
	case "rimgovernor/lifecycle_read_identity":
		return pbResult(&l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: s.context(), Paused: proto.Bool(true)}}}), nil
	case "rimgovernor/lifecycle_read_tick":
		return pbResult(&l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: s.context(), Paused: proto.Bool(true)}}}), nil
	case "rimgovernor/observations_read_world":
		if s.unavailable.Load() {
			return pbResult(&o.WorldReply{Outcome: &o.WorldReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum()}}}), nil
		}
		snapshot := worldFixture()
		snapshot.Context = s.context()
		snapshot.Tile = &o.WorldTile{Longitude: proto.Float64(-73.5)}
		return pbResult(&o.WorldReply{Outcome: &o.WorldReply_Observed{Observed: snapshot}}), nil
	case "rimgovernor/observations_list_rooms":
		snapshot := temperatureTestSnapshot()
		snapshot.Context = s.context()
		return pbResult(&o.ListRoomsReply{Outcome: &o.ListRoomsReply_Observed{Observed: snapshot}}), nil
	case "rimgovernor/clock_pause":
		s.generation.Add(1)
		status := clockTestStopped()
		status.Context = s.context()
		return pbResult(&k.StatusReply{Outcome: &k.StatusReply_Status{Status: status}}), nil
	}
	return &mcp.CallToolResult{IsError: true}, nil
}

func (s *readCacheServer) count(name string) int64 { return s.calls[name].Load() }

// TestStepReadCacheServesRepeatedReadsOnce is the contract the scheduler
// relies on: within one cached context the same read crosses the bridge
// once, distinct requests are distinct rows, a write discards the rows, and
// a context without the cache is unaffected.
func TestStepReadCacheServesRepeatedReadsOnce(t *testing.T) {
	server := newReadCacheServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	cache := NewStepReadCache()
	ctx := WithStepReadCache(context.Background(), cache)

	for i := 0; i < 3; i++ {
		reply, _, err := client.Identity(ctx)
		if err != nil || reply.GetLoaded().GetContext().GetNativeGeneration() != 1 {
			t.Fatal(reply, err)
		}
		world, _, err := client.ReadWorld(ctx, pbIdentity(), 42, 0)
		if err != nil {
			t.Fatal(err)
		}
		if lon, known := world.Longitude.Value(); !known || lon != -73.5 {
			t.Fatalf("cached world lost its facts: %+v", world)
		}
	}
	if _, _, err := client.ReadWorld(ctx, pbIdentity(), 43, 0); err != nil {
		t.Fatal(err)
	}
	if got := server.count("rimgovernor/lifecycle_read_identity"); got != 1 {
		t.Fatalf("identity read natively %d times", got)
	}
	if got := server.count("rimgovernor/observations_read_world"); got != 2 {
		t.Fatalf("world read natively %d times (two distinct requests expected)", got)
	}
	stats := cache.Stats()
	if stats.Hits != 4 || stats.Misses != 3 || stats.Invalidations != 0 {
		t.Fatalf("stats %+v", stats)
	}

	// The bare tick read is its own row in the same scope: one native call,
	// then hits.
	for i := 0; i < 2; i++ {
		tick, _, err := client.Tick(ctx)
		if err != nil || !tick.GetLoaded().GetPaused() {
			t.Fatal(tick, err)
		}
	}
	if got := server.count("rimgovernor/lifecycle_read_tick"); got != 1 {
		t.Fatalf("tick read natively %d times", got)
	}

	// The same reads outside the cached context still go to native.
	if _, _, err := client.Identity(context.Background()); err != nil {
		t.Fatal(err)
	}
	if got := server.count("rimgovernor/lifecycle_read_identity"); got != 2 {
		t.Fatalf("uncached identity read natively %d times", got)
	}

	// A write through the cached context discards every row; the next read
	// sees the new generation.
	if _, err := client.protoCall(ctx, "rimgovernor/clock_pause", clockTestOwned(), &k.StatusReply{}); err != nil {
		t.Fatal(err)
	}
	reply, _, err := client.Identity(ctx)
	if err != nil || reply.GetLoaded().GetContext().GetNativeGeneration() != 2 {
		t.Fatal(reply, err)
	}
	if got := server.count("rimgovernor/lifecycle_read_identity"); got != 3 {
		t.Fatalf("identity after write read natively %d times", got)
	}
	if stats := cache.Stats(); stats.Invalidations < 1 {
		t.Fatalf("write did not invalidate: %+v", stats)
	}
	if _, _, err = client.Identity(ctx); err != nil || server.count("rimgovernor/lifecycle_read_identity") != 3 {
		t.Fatal("post-write identity not cached", err)
	}
}

// TestStepReadCacheCoalescesConcurrentReads: planners run in parallel, so
// the first read of a fact typically has several identical readers waiting
// on it; they must all share the one round trip.
func TestStepReadCacheCoalescesConcurrentReads(t *testing.T) {
	server := newReadCacheServer()
	server.release = make(chan struct{})
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, 5*time.Second)
	cache := NewStepReadCache()
	ctx := WithStepReadCache(context.Background(), cache)
	const readers = 6
	var wg sync.WaitGroup
	errs := make(chan error, readers)
	for i := 0; i < readers; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			_, _, err := client.ReadWorld(ctx, pbIdentity(), 42, 0)
			errs <- err
		}()
	}
	deadline := time.Now().Add(2 * time.Second)
	for server.count("rimgovernor/observations_read_world") == 0 && time.Now().Before(deadline) {
		time.Sleep(time.Millisecond)
	}
	// Let the followers queue behind the in-flight leader before it answers.
	time.Sleep(20 * time.Millisecond)
	close(server.release)
	wg.Wait()
	close(errs)
	for err := range errs {
		if err != nil {
			t.Fatal(err)
		}
	}
	if got := server.count("rimgovernor/observations_read_world"); got != 1 {
		t.Fatalf("concurrent identical reads crossed the bridge %d times", got)
	}
	if stats := cache.Stats(); stats.Hits != readers-1 || stats.Misses != 1 {
		t.Fatalf("stats %+v", stats)
	}
}

// TestStepReadCacheSkipsRefusalsAndScopeChanges: an unavailable reply is
// never memoized, and a reply from a newer observation scope than the rows
// discards them.
func TestStepReadCacheSkipsRefusalsAndScopeChanges(t *testing.T) {
	server := newReadCacheServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	cache := NewStepReadCache()
	ctx := WithStepReadCache(context.Background(), cache)

	server.unavailable.Store(true)
	for i := 0; i < 2; i++ {
		if _, _, err := client.ReadWorld(ctx, pbIdentity(), 42, 0); err == nil {
			t.Fatal("unavailable read succeeded")
		}
	}
	if got := server.count("rimgovernor/observations_read_world"); got != 2 {
		t.Fatalf("unavailable reply was cached: %d native reads", got)
	}
	server.unavailable.Store(false)
	if _, _, err := client.ReadWorld(ctx, pbIdentity(), 42, 0); err != nil {
		t.Fatal(err)
	}

	// The generation moves under the step without a write through this
	// context (another controller, the player); the next fresh reply
	// reports the new scope and the older world row must not survive it.
	server.generation.Add(1)
	if _, _, err := client.Identity(ctx); err != nil {
		t.Fatal(err)
	}
	world, _, err := client.ReadWorld(ctx, pbIdentity(), 42, 0)
	if err != nil {
		t.Fatal(err)
	}
	if got := server.count("rimgovernor/observations_read_world"); got != 4 {
		t.Fatalf("stale-scope world row served: %d native reads", got)
	}
	if world.Context.GetNativeGeneration() != 2 {
		t.Fatalf("world context %v", world.Context)
	}
	if stats := cache.Stats(); stats.Invalidations != 1 {
		t.Fatalf("stats %+v", stats)
	}
}

// TestStepReadCacheHitsReachThePhaseReport: a cache hit leaves a
// native_cache_hit row attributed to the method so the throughput sampler
// can show the reads the cache absorbed.
func TestStepReadCacheHitsReachThePhaseReport(t *testing.T) {
	server := newReadCacheServer()
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	rec, err := NewFlightRecorder(path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { rec.Close() })
	client, err := open(context.Background(), "fixture-game", time.Second, rec, nil, (&testServer{schema: protoSchema, handler: server.handle}).factory(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })
	ctx := WithStepReadCache(context.Background(), NewStepReadCache())
	for i := 0; i < 3; i++ {
		if _, _, err := client.Identity(ctx); err != nil {
			t.Fatal(err)
		}
	}
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	summary := SummarizePhases(rows)
	found := false
	for _, tool := range summary.Tools {
		if tool.NativeTool == "rimgovernor/lifecycle_read_identity" && tool.Wrapper == "games_call_tool" {
			found = true
			if tool.Calls != 1 || tool.CacheHits != 2 {
				t.Fatalf("identity phases %+v", tool)
			}
		}
	}
	if !found {
		t.Fatalf("identity not in summary: %+v", summary.Tools)
	}
}

// TestStepReadCacheAnchorsAtTheFirstReply: under a running clock the
// replies of one step arrive at different ticks. The first reply anchors
// the scope; a later reply ahead of it within its family's tolerance keeps
// the rows already held, and one past the tolerance discards them and
// re-anchors, exactly as a new generation does (#243).
func TestStepReadCacheAnchorsAtTheFirstReply(t *testing.T) {
	server := newReadCacheServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	parent := NewFactCache()
	cache := NewChildReadCache(parent)
	ctx := WithStepReadCache(context.Background(), cache)

	if _, _, err := client.Identity(ctx); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadWorld(ctx, pbIdentity(), 42, 0); err != nil {
		t.Fatal(err)
	}
	// Rooms answer a little later than the anchor: within tolerance, so
	// the world row survives and the rooms row is filed at its own tick.
	server.tick.Add(FactTickToleranceRooms)
	rooms, _, err := client.ReadTemperatureRooms(ctx, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if rooms.GetObserved().GetContext().GetTick() != 1000+FactTickToleranceRooms {
		t.Fatal(rooms.GetObserved().GetContext())
	}
	if _, _, err := client.ReadWorld(ctx, pbIdentity(), 42, 0); err != nil {
		t.Fatal(err)
	}
	if got := server.count("rimgovernor/observations_read_world"); got != 1 {
		t.Fatalf("world re-read after a tolerated advance: %d", got)
	}
	if stats := cache.Stats(); stats.Invalidations != 0 || stats.Hits != 1 {
		t.Fatalf("stats %+v", stats)
	}
	if cache.scope.tick != 1000 {
		t.Fatalf("anchor moved to %d", cache.scope.tick)
	}
	// The next step anchors at the new tick and takes rooms from the
	// parent: the row is fresh for its family relative to the anchor.
	cache = NewChildReadCache(parent)
	ctx = WithStepReadCache(context.Background(), cache)
	if _, _, err := client.Identity(ctx); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadTemperatureRooms(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if got := server.count("rimgovernor/observations_list_rooms"); got != 1 {
		t.Fatalf("rooms re-read at its own tick: %d", got)
	}
	// A reply past the tolerance of its family re-anchors and drops the
	// step's rows: the bare tick read (identity family, same tick only)
	// answers past the anchor, and the rooms row the parent still holds is
	// no longer fresh for the new one.
	server.tick.Add(FactTickToleranceRooms + 1)
	if _, _, err := client.Tick(ctx); err != nil {
		t.Fatal(err)
	}
	if stats := cache.Stats(); stats.Invalidations != 1 {
		t.Fatalf("stats %+v", stats)
	}
	if cache.scope.tick != 1000+2*FactTickToleranceRooms+1 {
		t.Fatalf("anchor %d", cache.scope.tick)
	}
	if _, _, err := client.ReadTemperatureRooms(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if got := server.count("rimgovernor/observations_list_rooms"); got != 2 {
		t.Fatalf("stale rooms served: %d", got)
	}
}
