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
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The snapshot stream (#858). Native publishes whole BundleSnapshot frames
// into a shared-memory ring (every PeriodTicks, on a pause edge and after
// every applied write), and the state families are read only from it: a
// read in a shape a frame carries (frameShaped) is served from the newest
// frame, waiting for one when the stream is not open yet, the frame
// predates this client's last write, or the subscription just grew to
// carry it. It never crosses GABP. A parameterized read no frame holds (a
// page cursor, an id list, a filter) still does.
//
// The subscription grows itself: a read of a resource's sources or of a
// planning band is added to it, and the stream is resubscribed so later
// frames carry it.

const methodOpenSnapshotStream = "rimgovernor/observations_open_snapshot_stream"

const (
	// frameWriteWait bounds one wait for the next frame.
	frameWriteWait = time.Second
	// frameRetry spaces attempts to open (or resubscribe) the stream.
	frameRetry = 5 * time.Second
	// frameOpenPoll spaces checks for the stream while it is opening.
	frameOpenPoll = 20 * time.Millisecond
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
	stale     bool   // the subscription grew since the stream was opened
	needs     int64  // a frame must be captured at or past this write count
	epoch     uint64 // bumped by close: an open begun before it is discarded
	number    uint64
	identity  *c.Identity
	table     map[readCacheKey][]byte
}

// readCacheKey names one reply a frame answers: the read's method and its
// deterministically encoded request.
type readCacheKey struct {
	method  string
	request string
}

func newFrameStream() *frameStream {
	return &frameStream{open: func(name string) (frameReader, error) { return snapshotshm.Open(name) }}
}

// frameServes reports whether a frame can ever carry method: the state
// families. The tick read stays live.
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

// scoped is every observation request: its scope names the world.
type scoped interface{ GetScope() *o.ReadScope }

// frameShaped reports whether request is the shape a frame carries for
// method (with the subscription grown to it). The routine pawn detail is
// shaped by the frame's own colonist roster, so it is judged against the
// frame instead (frameRead).
func frameShaped(method string, request proto.Message) bool {
	r, ok := request.(scoped)
	if !ok {
		return false
	}
	id := r.GetScope().GetExpectedIdentity()
	if id == nil {
		return false
	}
	switch v := request.(type) {
	case *o.StatusRequest:
		return proto.Equal(v, emergencyRequest(id))
	case *o.ColonyFactsRequest:
		return proto.Equal(v, colonyFactsRequest(id, true, nil))
	case *o.PopulationRequest:
		return proto.Equal(v, populationRequest(id))
	case *o.ResearchRequest:
		return proto.Equal(v, researchRequest(id))
	case *o.ListBuildingsRequest:
		return proto.Equal(v, buildingsListRequest(id, "")) || proto.Equal(v, constructionBuildingsRequest(id, nil))
	case *o.BillsRequest:
		return proto.Equal(v, billsListRequest(id, ""))
	case *o.ListZonesRequest:
		return proto.Equal(v, zoneSectionRequest(id, ""))
	case *o.TradersRequest:
		return proto.Equal(v, tradersRequest(id))
	case *o.WorldProgressionRequest:
		return proto.Equal(v, worldProgressionRequest(id, false))
	case *o.ResourceSourcesRequest:
		return validID(v.GetResource()) == nil && proto.Equal(v, resourceSourcesRequest(id, v.GetResource()))
	case *o.GetCellsRequest:
		band, ok := frameBand(v.GetRectangle())
		return ok && proto.Equal(v, planningBandRequest(id, band))
	}
	return false
}

// frameBand is the planning band a frame carries for rect: the whole
// rectangle when it fits one band.
func frameBand(rect *o.Rectangle) (policy.Rectangle, bool) {
	if rect == nil {
		return policy.Rectangle{}, false
	}
	band := policy.Rectangle{X: rect.GetMinimum().GetX(), Z: rect.GetMinimum().GetZ(), Width: rect.GetMaximum().GetX() - rect.GetMinimum().GetX() + 1, Height: rect.GetMaximum().GetZ() - rect.GetMinimum().GetZ() + 1}
	if band.X < 0 || band.Z < 0 || band.Width < 1 || band.Height < 1 || int64(band.Width)*int64(band.Height) > planningWindowPage {
		return policy.Rectangle{}, false
	}
	return band, true
}

// frameRead serves name/request from the snapshot stream. served is false
// only for a read no frame carries (no stream on this client, another
// method, another shape); that read goes over GABP. Otherwise the reply
// comes from a frame or the read fails: ErrRefused when the frames
// describe another world, ErrUnavailable when no frame carrying it arrives
// within the call timeout.
func (caller *Client) frameRead(ctx context.Context, name string, request, reply proto.Message) (served bool, err error) {
	s := caller.frames
	if s == nil || !frameServes(name) {
		return false, nil
	}
	shaped := frameShaped(name, request)
	if !shaped && name != "rimgovernor/observations_list_pawns" {
		return false, nil
	}
	encoded, err := proto.MarshalOptions{Deterministic: true}.Marshal(request)
	if err != nil {
		return true, contract("request encoding: %v", err)
	}
	key := readCacheKey{method: name, request: string(encoded)}
	identity := request.(scoped).GetScope().GetExpectedIdentity()
	if identity == nil {
		return false, nil
	}
	caller.frameSubscribe(name, request)
	return caller.frameReadKey(ctx, name, key, identity, shaped, reply)
}

// frameReadKey waits for a frame past this client's last write that
// answers key and decodes its reply (frameRead).
func (caller *Client) frameReadKey(ctx context.Context, name string, key readCacheKey, identity *c.Identity, shaped bool, reply proto.Message) (served bool, err error) {
	s := caller.frames
	ctx, cancel := context.WithTimeout(ctx, caller.timeout)
	defer cancel()
	miss := func(why string, err error) (bool, error) {
		if caller.recorder != nil {
			caller.recorder.Event("native_frame_miss", caller.snapshotRecordingContext(ctx), false, map[string]any{"native_tool": name, "why": why})
		}
		return true, err
	}
	for {
		reader := caller.frameReader(ctx)
		if reader == nil {
			select {
			case <-ctx.Done():
				return miss("no stream", fmt.Errorf("%w: snapshot stream not open for %s: %v", ErrUnavailable, name, ctx.Err()))
			case <-time.After(frameOpenPoll):
			}
			continue
		}
		s.mu.Lock()
		needs := s.needs
		s.mu.Unlock()
		frame, ok, err := reader.Latest()
		if err != nil {
			return miss("latest", fmt.Errorf("%w: snapshot frame: %v", ErrUnavailable, err))
		}
		after := uint64(0)
		if ok {
			after = frame.Number
			if frame.Writes >= needs {
				payload, world, found, decoded := s.lookup(frame, key)
				if decoded != nil && caller.recorder != nil {
					caller.recorder.Event("native_frame", caller.snapshotRecordingContext(ctx), false, decoded)
				}
				switch {
				case found:
					if err := proto.Unmarshal(payload, reply); err != nil {
						return true, contract("frame reply decoding: %v", err)
					}
					if caller.recorder != nil {
						caller.recorder.Event("native_frame_hit", caller.snapshotRecordingContext(ctx), false, map[string]any{"tool": "games_call_tool", "native_tool": name, "frame": frame.Number})
					}
					return true, nil
				case world != nil && !sameIdentity(world, identity):
					return miss("identity", fmt.Errorf("%w: %s asked for another world than the snapshot frames describe", ErrRefused, name))
				case world != nil && !shaped:
					// Pawn detail for another roster than the frame's.
					return false, nil
				}
			}
		}
		if !reader.Wait(ctx, after, frameWriteWait) && ctx.Err() != nil {
			return miss("wait", fmt.Errorf("%w: no snapshot frame carried %s: %v", ErrUnavailable, name, ctx.Err()))
		}
	}
}

// lookup finds key in frame's table, building the table the first time a
// frame is read, and names the world the frame describes (nil when it
// cannot be decoded). decoded is set when this call built the table: the
// frame's size and cost (#858), one native_frame row per frame the
// controller consumed. skipped counts the frames published since the last
// one consumed and never read.
func (s *frameStream) lookup(frame snapshotshm.Frame, key readCacheKey) (payload []byte, world *c.Identity, ok bool, decoded map[string]any) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if frame.Number != s.number {
		started := time.Now()
		table, identity, err := frameTable(frame.Payload, s.window)
		decoded = map[string]any{"frame": frame.Number, "bytes": len(frame.Payload), "capture_us": frame.CaptureMicros, "encode_us": frame.EncodeMicros,
			"write_us": frame.WriteMicros, "decode_us": time.Since(started).Microseconds(), "replies": len(table)}
		if s.number != 0 && frame.Number > s.number {
			decoded["skipped"] = frame.Number - s.number - 1
		}
		if err != nil {
			decoded["error"] = err.Error()
			return nil, nil, false, decoded
		}
		s.number, s.table, s.identity = frame.Number, table, identity
	}
	payload, ok = s.table[key]
	return payload, s.identity, ok, decoded
}

// frameTable decodes one frame into the replies its sections answer.
func frameTable(payload []byte, window *o.Rectangle) (map[readCacheKey][]byte, *c.Identity, error) {
	v := &o.BundleSnapshot{}
	if err := proto.Unmarshal(payload, v); err != nil {
		return nil, nil, err
	}
	if err := ValidateContext(v.Context); err != nil {
		return nil, nil, err
	}
	var emergency EmergencyObservation
	if v.Emergency != nil {
		var err error
		if emergency, err = DecodeEmergencyStatus(v.Emergency, v.Context.Identity); err != nil {
			return nil, nil, err
		}
	}
	table := map[readCacheKey][]byte{}
	frameReplies(v, emergency, window, func(method string, request, reply proto.Message) {
		var encoded []byte
		if request != nil {
			var err error
			encoded, err = proto.MarshalOptions{Deterministic: true}.Marshal(request)
			if err != nil {
				return
			}
		}
		if payload, err := proto.Marshal(reply); err == nil {
			table[readCacheKey{method: method, request: string(encoded)}] = payload
		}
	})
	return table, v.Context.Identity, nil
}

// frameReplies hands seed every section of v as the (method, request,
// reply) its dedicated read would have produced. window is the planning
// band the subscription asked for.
func frameReplies(v *o.BundleSnapshot, emergency EmergencyObservation, window *o.Rectangle, seed func(method string, request, reply proto.Message)) {
	seed("rimgovernor/lifecycle_read_tick", &l.TickRequest{}, &l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: v.Context, Paused: v.Paused}}})
	identity := v.Context.Identity
	if v.Emergency != nil {
		seed("rimgovernor/observations_read_status", emergencyRequest(identity), &o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: v.Emergency}})
	}
	if v.ColonyFacts != nil {
		seed("rimgovernor/observations_read_colony_facts", colonyFactsRequest(identity, true, nil), &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: v.ColonyFacts}})
	}
	if v.Population != nil {
		seed("rimgovernor/observations_read_population", populationRequest(identity), &o.PopulationReply{Outcome: &o.PopulationReply_Observed{Observed: v.Population}})
	}
	if v.Research != nil {
		seed("rimgovernor/observations_read_research", researchRequest(identity), &o.ResearchReply{Outcome: &o.ResearchReply_Observed{Observed: v.Research}})
	}
	if ids := routinePawnIDs(emergency); v.ColonistPawns != nil && len(ids) > 0 {
		seed("rimgovernor/observations_list_pawns", pawnDetailsRequest(identity, ids, pawnDetails{Combat: true, Work: true, Care: true, Schedule: true, Social: true}), &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: v.ColonistPawns}})
	}
	if v.Buildings != nil {
		seed("rimgovernor/observations_list_buildings", buildingsListRequest(identity, ""), &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: v.Buildings}})
	}
	if v.BuiltBuildings != nil {
		seed("rimgovernor/observations_list_buildings", constructionBuildingsRequest(identity, nil), &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: v.BuiltBuildings}})
	}
	if v.Bills != nil {
		seed("rimgovernor/observations_read_bills", billsListRequest(identity, ""), &o.BillsReply{Outcome: &o.BillsReply_Observed{Observed: v.Bills}})
	}
	if v.Zones != nil {
		seed("rimgovernor/observations_list_zones", zoneSectionRequest(identity, ""), &o.ListZonesReply{Outcome: &o.ListZonesReply_Observed{Observed: v.Zones}})
	}
	if v.Traders != nil {
		seed("rimgovernor/observations_list_traders", tradersRequest(identity), &o.TradersReply{Outcome: &o.TradersReply_Observed{Observed: v.Traders}})
	}
	if v.WorldProgression != nil {
		seed("rimgovernor/observations_read_world_progression", worldProgressionRequest(identity, false), &o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: v.WorldProgression}})
	}
	for _, sources := range v.ResourceSources {
		seed("rimgovernor/observations_list_resource_sources", resourceSourcesRequest(identity, sources.GetResource()), &o.ResourceSourcesReply{Outcome: &o.ResourceSourcesReply_Observed{Observed: sources}})
	}
	if band, ok := frameBand(window); ok && v.PlanningWindow != nil {
		seed("rimgovernor/observations_get_cells", planningBandRequest(identity, band), &o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: v.PlanningWindow}})
	}
	seed(combatFrameMethod, nil, &o.BundleSnapshot{Context: v.Context, CombatPawns: v.CombatPawns, CombatEvents: v.CombatEvents})
}

// BundleEmergency decodes a step snapshot's emergency section into the
// observation ReadEmergency returns.
func BundleEmergency(v *o.BundleSnapshot) (EmergencyObservation, error) {
	if v == nil || v.Emergency == nil {
		return EmergencyObservation{}, contract("emergency section missing")
	}
	return DecodeEmergencyStatus(v.Emergency, v.Context.GetIdentity())
}

// routinePawnIDs lists the colonists the routine census reads pawn detail
// for, in census order: every colonist of a complete emergency census, none
// otherwise (the bracket then reads no pawns at all).
func routinePawnIDs(emergency EmergencyObservation) []string {
	if complete, known := emergency.Facts.ColonistsComplete.Value(); !known || !complete {
		return nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	return ids
}

// frameReader is the open stream, starting an open or resubscription in
// the background when one is due; nil until the first open lands.
func (caller *Client) frameReader(ctx context.Context) frameReader {
	s := caller.frames
	s.mu.Lock()
	defer s.mu.Unlock()
	if (s.reader == nil || s.stale) && !s.opening && (s.stale || time.Since(s.attempted) >= frameRetry) {
		s.opening, s.attempted, s.stale = true, time.Now(), false
		request := &o.SnapshotStreamRequest{ResourceSources: slices.Clone(s.resources), PlanningWindow: s.window}
		go caller.openFrames(context.WithoutCancel(ctx), request, s.reader == nil, s.epoch)
	}
	return s.reader
}

// openFrames opens (or, with a reader already mapped, resubscribes) the
// stream. A failure (another host, an older native) is retried after
// frameRetry; reads wait for it.
func (caller *Client) openFrames(ctx context.Context, request *o.SnapshotStreamRequest, mapRing bool, epoch uint64) {
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
	if s.epoch != epoch {
		// The connection turned over while this open ran: its ring may
		// be the old game's.
		if next != nil {
			_ = next.Close()
		}
		return
	}
	s.opening = false
	if err != nil {
		return
	}
	if next != nil {
		s.reader = next
	}
	// Frames captured before the resubscription lack what it added.
	s.number, s.table, s.identity = 0, nil, nil
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

// frameSubscribe grows the subscription with a read a frame carries only
// when asked: a resource's sources, or a planning band (one at a time).
func (caller *Client) frameSubscribe(name string, request proto.Message) {
	s := caller.frames
	s.mu.Lock()
	defer s.mu.Unlock()
	switch r := request.(type) {
	case *o.ResourceSourcesRequest:
		if !slices.Contains(s.resources, r.GetResource()) {
			s.resources = append(s.resources, r.GetResource())
			s.stale = true
		}
	case *o.GetCellsRequest:
		if name == "rimgovernor/observations_get_cells" && !proto.Equal(r.GetRectangle(), s.window) {
			s.window = proto.Clone(r.GetRectangle()).(*o.Rectangle)
			s.stale = true
		}
	}
}

// close unmaps the ring and forgets everything tied to it (the write
// count frames must reach, the decoded frame), keeping the subscription:
// a reconnect may be to a new game process with a ring of its own, so the
// next read opens the stream afresh (#858).
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
	s.epoch++
	s.opening, s.attempted, s.stale = false, time.Time{}, false
	s.needs, s.number, s.table = 0, 0, nil
}

// combatFrameMethod keys a frame's combat sections in its table: a
// frames-only read with no GABP method behind it.
const combatFrameMethod = "rimgovernor/snapshot_frame_combat"

// Combat is a frame's combat state (#851): every colonist, hostile and
// colony animal while combat is active, and the native's retained event
// ring on the map, oldest first. Context is the frame's.
type Combat struct {
	Context *c.ObservationContext
	Pawns   []*mp.CombatPawn
	Events  []*mp.CombatEventRow
}

// ReadCombat reads the combat state from the newest frame past this
// client's last write. There is no GABP read behind it: without a stream
// it is ErrUnavailable.
func (caller *Client) ReadCombat(ctx context.Context, identity *c.Identity) (Combat, error) {
	if caller.frames == nil {
		return Combat{}, fmt.Errorf("%w: combat state is served only by the snapshot stream", ErrUnavailable)
	}
	if identity == nil {
		return Combat{}, contract("combat read without an identity")
	}
	reply := &o.BundleSnapshot{}
	if _, err := caller.frameReadKey(ctx, combatFrameMethod, readCacheKey{method: combatFrameMethod}, identity, true, reply); err != nil {
		return Combat{}, err
	}
	if err := validateCombat(reply); err != nil {
		return Combat{}, err
	}
	return Combat{Context: reply.Context, Pawns: reply.CombatPawns, Events: reply.CombatEvents}, nil
}

// validateCombat checks a frame's combat rows (#851).
func validateCombat(v *o.BundleSnapshot) error {
	for _, row := range v.CombatPawns {
		if validID(row.GetId()) != nil || row.GetSide() == mp.CombatSide_COMBAT_SIDE_UNSPECIFIED || row.Cell == nil {
			return contract("combat pawn without id, side or cell")
		}
		for _, n := range []*float64{row.Health, row.BleedRate, row.Pain, row.MoveSpeed, row.ShieldEnergy, row.WeaponRange} {
			if !combatNumber(n, true) {
				return contract("combat pawn number")
			}
		}
		if row.GetHealth() > 1 || row.GetShieldEnergy() > 1 {
			return contract("combat pawn fraction")
		}
	}
	for i, row := range v.CombatEvents {
		if row.At == nil || row.GetKind() == mp.CombatLogKind_COMBAT_LOG_KIND_UNSPECIFIED {
			return contract("combat event without watermark or kind")
		}
		if DropPodArrival(row) && (int64(row.GetOpenTick()) <= row.GetAt().GetTick() || len(row.GetLandingCells()) == 0) {
			return contract("drop-pod arrival without an open tick after it or landing cells")
		}
		if i > 0 && !CombatBefore(v.CombatEvents[i-1].At, row.At) {
			return contract("combat events out of order")
		}
	}
	return nil
}

// CombatBefore reports whether watermark a is strictly before b.
func CombatBefore(a, b *mp.Watermark) bool {
	if a.GetTick() != b.GetTick() {
		return a.GetTick() < b.GetTick()
	}
	return a.GetSeq() < b.GetSeq()
}

// PodsStrategy is the raid strategy of a drop-pod arrival row (#870).
const PodsStrategy = "pods"

// DropPodArrival reports whether row is a drop-pod raid's arrival (#870):
// a hostile arrived row with strategy pods, landing cells and open tick.
func DropPodArrival(row *mp.CombatEventRow) bool {
	return row.GetKind() == mp.CombatLogKind_COMBAT_LOG_KIND_HOSTILE_ARRIVED && row.GetRaidStrategy() == PodsStrategy
}

// CombatEventID is a combat event row's key: its own watermark.
func CombatEventID(row *mp.CombatEventRow) string {
	return fmt.Sprintf("%d.%d", row.GetAt().GetTick(), row.GetAt().GetSeq())
}
