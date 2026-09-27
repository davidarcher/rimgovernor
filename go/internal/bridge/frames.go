package bridge

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshotshm"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The snapshot stream (#858). Native publishes whole BundleSnapshot frames
// into a shared-memory ring (every PeriodTicks, on a pause edge and after
// every applied write) and protoRead serves a read from the newest frame
// when the frame holds that exact (method, request): the keys the bundle
// seeds (bundleReplies). Any other shape, a stream that cannot be opened
// here, or a frame that predates this client's last write goes over GABP
// as before.
//
// The subscription grows itself: a GABP read of a resource's sources or of
// a planning band in the shape a frame can carry is added to it, and the
// stream is resubscribed so later frames carry it.

const methodOpenSnapshotStream = "rimgovernor/observations_open_snapshot_stream"

const (
	// frameWriteWait bounds how long a read after a write waits for a frame
	// captured past it before reading over GABP.
	frameWriteWait = time.Second
	// frameRetry spaces attempts to open (or resubscribe) the stream.
	frameRetry = 5 * time.Second
)

// frameReader is the ring snapshotshm.Reader maps; tests substitute one.
type frameReader interface {
	Head() uint64
	Writes() int64
	Latest() (snapshotshm.Frame, bool, error)
	Wait(ctx context.Context, after uint64, timeout time.Duration) bool
	Close() error
}

type frameStream struct {
	// open maps the ring native named; snapshotshm.Open unless a test
	// replaced it.
	open func(name string) (frameReader, error)

	mu        sync.Mutex
	reader    frameReader
	opening   bool
	attempted time.Time
	resources []string
	window    *o.Rectangle
	stale     bool  // the subscription grew since the stream was opened
	needs     int64 // a frame must be captured at or past this write count
	number    uint64
	table     map[readCacheKey][]byte
}

func newFrameStream() *frameStream {
	return &frameStream{open: func(name string) (frameReader, error) { return snapshotshm.Open(name) }}
}

// frameServes reports whether a frame can ever carry method: the bundle's
// state families. The tick read stays live.
func frameServes(method string) bool {
	switch method {
	case "rimgovernor/observations_read_status", "rimgovernor/observations_read_colony_facts", "rimgovernor/observations_read_population",
		"rimgovernor/observations_read_research", "rimgovernor/observations_list_pawns", "rimgovernor/observations_list_buildings",
		"rimgovernor/observations_read_bills", "rimgovernor/observations_list_zones", "rimgovernor/observations_list_traders",
		"rimgovernor/observations_read_world_progression", "rimgovernor/observations_list_resource_sources", "rimgovernor/observations_get_cells":
		return true
	}
	return false
}

// frameRead serves name/request from the newest frame into reply, reporting
// whether it did.
func (caller *Client) frameRead(ctx context.Context, name string, request, reply proto.Message) bool {
	s := caller.frames
	if s == nil || !frameServes(name) {
		return false
	}
	encoded, err := proto.MarshalOptions{Deterministic: true}.Marshal(request)
	if err != nil {
		return false
	}
	reader := caller.frameReader(ctx)
	miss := func(why string) bool {
		if caller.recorder != nil {
			caller.recorder.Event("native_frame_miss", caller.snapshotRecordingContext(ctx), false, map[string]any{"native_tool": name, "why": why})
		}
		return false
	}
	if reader == nil {
		return miss("no reader")
	}
	s.mu.Lock()
	needs := s.needs
	s.mu.Unlock()
	frame, ok, err := reader.Latest()
	for err == nil && ok && frame.Writes < needs {
		if !reader.Wait(ctx, frame.Number, frameWriteWait) {
			return miss("write wait")
		}
		frame, ok, err = reader.Latest()
	}
	if err != nil || !ok {
		return miss(fmt.Sprint("latest ", ok, err))
	}
	payload, ok := s.lookup(frame, readCacheKey{method: name, request: string(encoded)})
	if !ok || proto.Unmarshal(payload, reply) != nil {
		return miss(fmt.Sprint("lookup ", ok, " table ", len(s.table)))
	}
	if caller.recorder != nil {
		caller.recorder.Event("native_frame_hit", caller.snapshotRecordingContext(ctx), false, map[string]any{"tool": "games_call_tool", "native_tool": name, "frame": frame.Number})
	}
	return true
}

// lookup finds key in frame's table, building the table the first time a
// frame is read.
func (s *frameStream) lookup(frame snapshotshm.Frame, key readCacheKey) ([]byte, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if frame.Number != s.number {
		table, err := frameTable(frame.Payload, s.resources, s.window)
		if err != nil {
			return nil, false
		}
		s.number, s.table = frame.Number, table
	}
	payload, ok := s.table[key]
	return payload, ok
}

// frameTable decodes one frame into the replies its sections answer.
func frameTable(payload []byte, resources []string, window *o.Rectangle) (map[readCacheKey][]byte, error) {
	v := &o.BundleSnapshot{}
	if err := proto.Unmarshal(payload, v); err != nil {
		return nil, err
	}
	if err := ValidateContext(v.Context); err != nil {
		return nil, err
	}
	var emergency EmergencyObservation
	if v.Emergency != nil {
		var err error
		if emergency, err = DecodeEmergencyStatus(v.Emergency, v.Context.Identity); err != nil {
			return nil, err
		}
	}
	// The request a bundle carrying every family the frame holds would
	// have been: whole families, the subscription's resources and band.
	request := &o.BundleRequest{ResourceSources: resources}
	if window != nil {
		request.PlanningWindow = &o.BundlePlanningWindowRequest{Region: window}
	}
	table := map[readCacheKey][]byte{}
	bundleReplies(request, v, emergency, func(method string, request, reply proto.Message) {
		if !frameServes(method) {
			return
		}
		encoded, err := proto.MarshalOptions{Deterministic: true}.Marshal(request)
		if err != nil {
			return
		}
		if payload, err := proto.Marshal(reply); err == nil {
			table[readCacheKey{method: method, request: string(encoded)}] = payload
		}
	})
	return table, nil
}

// frameReader is the open stream, starting an open or resubscription in
// the background when one is due; reads go over GABP until it lands, so no
// read ever waits on the open.
func (caller *Client) frameReader(ctx context.Context) frameReader {
	s := caller.frames
	s.mu.Lock()
	defer s.mu.Unlock()
	if (s.reader == nil || s.stale) && !s.opening && time.Since(s.attempted) >= frameRetry {
		s.opening, s.attempted, s.stale = true, time.Now(), false
		request := &o.SnapshotStreamRequest{ResourceSources: slices.Clone(s.resources), PlanningWindow: s.window}
		go caller.openFrames(context.WithoutCancel(ctx), request, s.reader == nil)
	}
	return s.reader
}

// openFrames opens (or, with a reader already mapped, resubscribes) the
// stream. A failure (another host, an older native) leaves reads on GABP
// and the open is retried after frameRetry.
func (caller *Client) openFrames(ctx context.Context, request *o.SnapshotStreamRequest, mapRing bool) {
	s := caller.frames
	ctx, cancel := context.WithTimeout(ctx, caller.timeout)
	defer cancel()
	opened, err := caller.openSnapshotStream(ctx, request)
	var next frameReader
	if err == nil && mapRing {
		next, err = s.open(opened.GetName())
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.opening = false
	if err != nil {
		return
	}
	if next != nil {
		s.reader = next
	}
	// Frames captured before the resubscription lack what it added.
	s.number, s.table = 0, nil
}

func (caller *Client) openSnapshotStream(ctx context.Context, request *o.SnapshotStreamRequest) (*o.SnapshotStreamOpened, error) {
	reply := &o.SnapshotStreamReply{}
	raw, err := caller.protoCall(ctx, methodOpenSnapshotStream, request, reply)
	if err != nil {
		return nil, err
	}
	switch v := reply.Outcome.(type) {
	case *o.SnapshotStreamReply_Opened:
		if v.Opened.GetName() == "" {
			return nil, contract("snapshot stream name missing")
		}
		return v.Opened, nil
	case *o.SnapshotStreamReply_Unavailable:
		return nil, unavailable(v.Unavailable, raw)
	case *o.SnapshotStreamReply_Failure:
		return nil, failure(v.Failure, raw)
	}
	return nil, errors.New("snapshot stream outcome missing")
}

// noteFrameWrite records that a write returned: later reads wait for a
// frame captured past it.
func (caller *Client) noteFrameWrite() {
	s := caller.frames
	if s == nil {
		return
	}
	s.mu.Lock()
	reader := s.reader
	s.mu.Unlock()
	if reader == nil {
		return
	}
	writes := reader.Writes()
	s.mu.Lock()
	s.needs = max(s.needs, writes)
	s.mu.Unlock()
}

// noteFrameMiss grows the subscription with a GABP read a frame could
// carry: a resource's sources, or a whole planning band.
func (caller *Client) noteFrameMiss(name string, request proto.Message) {
	s := caller.frames
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	switch name {
	case "rimgovernor/observations_list_resource_sources":
		r, ok := request.(*o.ResourceSourcesRequest)
		if !ok || validID(r.GetResource()) != nil || slices.Contains(s.resources, r.GetResource()) ||
			!proto.Equal(r, resourceSourcesRequest(r.GetScope().GetExpectedIdentity(), r.GetResource())) {
			return
		}
		s.resources = append(s.resources, r.GetResource())
		s.stale = true
	case "rimgovernor/observations_get_cells":
		r, ok := request.(*o.GetCellsRequest)
		if !ok || r.GetRectangle() == nil || r.ChangedSinceTick != nil || proto.Equal(r.GetRectangle(), s.window) {
			return
		}
		band := BundlePlanningWindowRequest(&BundlePlanningWindow{Region: rectangleRegion(r.GetRectangle())})
		if band == nil || !proto.Equal(r, planningBandRequest(r.GetScope().GetExpectedIdentity(), rectangleRegion(r.GetRectangle()))) {
			return
		}
		s.window = proto.Clone(r.GetRectangle()).(*o.Rectangle)
		s.stale = true
	}
}

func rectangleRegion(rect *o.Rectangle) policy.Rectangle {
	return policy.Rectangle{X: rect.GetMinimum().GetX(), Z: rect.GetMinimum().GetZ(), Width: rect.GetMaximum().GetX() - rect.GetMinimum().GetX() + 1, Height: rect.GetMaximum().GetZ() - rect.GetMinimum().GetZ() + 1}
}

func (s *frameStream) close() {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.reader != nil {
		_ = s.reader.Close()
		s.reader = nil
	}
}
