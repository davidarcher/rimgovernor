package bridge

import (
	"context"
	"errors"
	"fmt"
	"maps"
	"slices"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshotshm"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The snapshot stream. Native publishes whole BundleSnapshot frames
// into a shared-memory ring (every PeriodTicks, on a pause edge and after
// every applied write), and the state families are read from it: a read
// is looked up by its encoded request in the newest frame, waiting for one
// when the stream is not open yet, the frame predates this client's last
// write, or the subscription is growing to carry it. A read the frame
// does not answer (a page cursor, an id list, a filter) crosses GABP.
//
// The subscription grows itself: a read of a resource's sources is added
// to it, and the stream is resubscribed so later frames carry it.

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
	stale     bool   // the subscription grew since the stream was opened
	needs     int64  // a frame must be captured at or past this write count
	epoch     uint64 // bumped by close: an open begun before it is discarded
	number    uint64
	identity  *c.Identity
	table     map[readCacheKey]*frameReply
	context   *c.ObservationContext // the decoded frame's
	held      heldTables            // the keyed tables as of the decoded frame
	census    Memo                  // the routine census readers derive from the building table
	// hold keeps the sections native omits while unchanged;
	// keyframe asks native for a frame carrying every section after a
	// seq gap.
	hold     sectionHold
	keyframe bool
	// gridHold keeps the grid keyframe deltas apply to; grid is
	// the newest decoded frame's grid.
	gridHold gridHold
	grid     frameGrid
}

// frameReply is one reply a frame answers, encoded when first read: a
// frame nobody reads costs no encoding. build runs under the stream's
// lock before the next frame is applied, so it may read the hold's live
// tables.
type frameReply struct {
	build   func() proto.Message
	payload []byte
	ok      bool
}

func (r *frameReply) bytes() ([]byte, bool) {
	if r.build != nil {
		if m := r.build(); m != nil {
			if payload, err := proto.Marshal(m); err == nil {
				r.payload, r.ok = payload, true
			}
		}
		r.build = nil
	}
	return r.payload, r.ok
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
		"rimgovernor/observations_read_world_progression", "rimgovernor/observations_list_resource_sources":
		return true
	}
	return false
}

// scoped is every observation request: its scope names the world.
type scoped interface{ GetScope() *o.ReadScope }

// frameRead serves name/request from the snapshot stream. served is false
// for a read no frame answers (no stream on this client, another method, a
// request shape the newest frame does not carry); that read goes over GABP.
// Otherwise the reply comes from a frame or the read fails: ErrRefused
// when the frames describe another world, ErrUnavailable when no frame
// arrives within the call timeout.
func (caller *Client) frameRead(ctx context.Context, name string, request, reply proto.Message) (served bool, err error) {
	s := caller.frames
	if s == nil || !frameServes(name) {
		return false, nil
	}
	r, ok := request.(scoped)
	if !ok {
		return false, nil
	}
	identity := r.GetScope().GetExpectedIdentity()
	if identity == nil {
		return false, nil
	}
	encoded, err := proto.MarshalOptions{Deterministic: true}.Marshal(request)
	if err != nil {
		return true, contract("request encoding: %v", err)
	}
	key := readCacheKey{method: name, request: string(encoded)}
	caller.frameSubscribe(name, request)
	return caller.frameReadKey(ctx, name, key, identity, true, reply)
}

// frameReadKey waits for a frame past this client's last write that
// answers key and decodes its reply (frameRead). With fallback, a frame of
// the same world that answers the method only in other shapes, while no
// subscription change is pending, sends the read over GABP instead.
func (caller *Client) frameReadKey(ctx context.Context, name string, key readCacheKey, identity *c.Identity, fallback bool, reply proto.Message) (served bool, err error) {
	return caller.frameReadView(ctx, name, key, identity, fallback, reply, nil)
}

// frameReadView is frameReadKey that also runs pick under the stream's
// lock on the frame that answers, for the readers that take the hold's
// keyed tables rather than an encoded reply; a nil reply decodes nothing.
func (caller *Client) frameReadView(ctx context.Context, name string, key readCacheKey, identity *c.Identity, fallback bool, reply proto.Message, pick func(*frameStream)) (served bool, err error) {
	s := caller.frames
	ctx, cancel := context.WithTimeout(ctx, caller.timeout)
	defer cancel()
	miss := func(why string, err error) (bool, error) {
		if caller.recorder != nil {
			caller.recorder.Event("native_frame", caller.snapshotRecordingContext(ctx), false, map[string]any{"outcome": "miss", "native_tool": name, "why": why})
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
		needs, pending := s.needs, s.stale || s.opening
		s.mu.Unlock()
		if AnyFrame(ctx) {
			needs = 0
		}
		frame, ok, err := reader.Latest()
		if err != nil {
			return miss("latest", fmt.Errorf("%w: snapshot frame: %v", ErrUnavailable, err))
		}
		after := uint64(0)
		if ok {
			after = frame.Number
			if frame.Writes >= needs {
				payload, world, found, carries, decoded, refusal := s.lookup(frame, key, reply != nil, pick)
				if refusal != nil {
					return miss("section failed", refusal)
				}
				if decoded["gap"] == true {
					caller.frameReader(ctx) // asks for the keyframe now
				}
				if decoded != nil && caller.recorder != nil {
					caller.recorder.Event("native_frame", caller.snapshotRecordingContext(ctx), false, decoded)
				}
				switch {
				case found:
					if reply != nil {
						if err := proto.Unmarshal(payload, reply); err != nil {
							return true, contract("frame reply decoding: %v", err)
						}
					}
					if caller.recorder != nil {
						caller.recorder.Event("native_frame", caller.snapshotRecordingContext(ctx), false, map[string]any{"outcome": "hit", "tool": "games_call_tool", "native_tool": name, "frame": frame.Number})
					}
					return true, nil
				case world != nil && !sameIdentity(world, identity):
					return miss("identity", fmt.Errorf("%w: %s asked for another world than the snapshot frames describe", ErrRefused, name))
				case world != nil && fallback && carries && !pending:
					// The frame answers the method, in another shape.
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
// cannot be decoded). carries reports whether the frame answers the
// method in any request shape. decoded is set when this call built the table: the
// frame's size and cost, one native_frame row per frame the
// controller consumed. skipped counts the frames published since the last
// one consumed and never read.
func (s *frameStream) lookup(frame snapshotshm.Frame, key readCacheKey, wantPayload bool, pick func(*frameStream)) (payload []byte, world *c.Identity, ok, carries bool, decoded map[string]any, refusal *Refusal) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if frame.Number != s.number {
		started := time.Now()
		table, identity, context, tables, held, gap, grid, gridKind, err := s.frameTable(frame.Payload)
		decoded = map[string]any{"outcome": "decoded", "frame": frame.Number, "bytes": len(frame.Payload), "capture_us": frame.CaptureMicros, "encode_us": frame.EncodeMicros,
			"write_us": frame.WriteMicros, "decode_us": time.Since(started).Microseconds(), "replies": len(table)}
		if s.number != 0 && frame.Number > s.number {
			decoded["skipped"] = frame.Number - s.number - 1
		}
		if held > 0 {
			decoded["held"] = held
		}
		if gridKind != "" {
			decoded["grid"] = gridKind
		}
		if gap {
			decoded["gap"] = true
			s.keyframe = true
		}
		if err != nil {
			decoded["error"] = err.Error()
			var refused *Refusal
			errors.As(err, &refused)
			return nil, nil, false, false, decoded, refused
		}
		s.number, s.table, s.identity, s.context, s.held = frame.Number, table, identity, context, tables
		if grid.grid != nil {
			s.grid = grid
		}
	}
	if entry, present := s.table[key]; present {
		ok = true
		if wantPayload {
			payload, ok = entry.bytes()
		}
		if ok && pick != nil {
			pick(s)
		}
	}
	for held := range s.table {
		carries = carries || held.method == key.method
	}
	return payload, s.identity, ok, carries, decoded, nil
}

// frameTable decodes one frame, its omitted sections filled from the
// hold, into the replies its sections answer. held and gap are the hold's
// (sectionHold.fill); grid is the frame's map grid and gridKind what it
// carried (gridHold.apply), a grid gap also setting gap.
func (s *frameStream) frameTable(payload []byte) (table map[readCacheKey]*frameReply, world *c.Identity, ctx *c.ObservationContext, tables heldTables, held int, gap bool, grid frameGrid, gridKind string, err error) {
	v := &o.BundleSnapshot{}
	if err := proto.Unmarshal(payload, v); err != nil {
		return nil, nil, nil, heldTables{}, 0, false, frameGrid{}, "", err
	}
	// A frame carrying a Failure is native's account that a section read
	// threw: a refusal naming the section, never a frame without it.
	if f := v.GetFailure(); f != nil {
		return nil, nil, nil, heldTables{}, 0, false, frameGrid{}, "", &Refusal{Tool: "snapshot_frame", Cause: f.GetDetail()}
	}
	if err := ValidateContext(v.Context); err != nil {
		return nil, nil, nil, heldTables{}, 0, false, frameGrid{}, "", err
	}
	held, gap = s.hold.fill(v)
	decodedGrid, gridKind, gridGap, gridErr := s.gridHold.apply(v)
	gap = gap || gridGap
	if gridErr != nil {
		gridKind += ": " + gridErr.Error()
	}
	tables = s.hold.heldAt(v.Context)
	var emergency EmergencyObservation
	if v.Emergency != nil {
		if tables.pawnErr != nil {
			return nil, nil, nil, heldTables{}, held, gap, frameGrid{}, gridKind, tables.pawnErr
		}
		if emergency, err = DecodeEmergencyStatus(v.Emergency, tables.pawns, v.Context.Identity); err != nil {
			return nil, nil, nil, heldTables{}, held, gap, frameGrid{}, gridKind, err
		}
	}
	table = map[readCacheKey]*frameReply{}
	frameRepliesWith(v, emergency, &tables, func(method string, request proto.Message, reply func() proto.Message) {
		var encoded []byte
		if request != nil {
			var err error
			encoded, err = proto.MarshalOptions{Deterministic: true}.Marshal(request)
			if err != nil {
				return
			}
		}
		table[readCacheKey{method: method, request: string(encoded)}] = &frameReply{build: reply}
	})
	if decodedGrid != nil {
		grid = frameGrid{context: v.Context, grid: decodedGrid, sky: v.GetSkyGlow()}
		if payload, err := proto.Marshal(v.Context); err == nil {
			table[readCacheKey{method: gridFrameMethod}] = &frameReply{payload: payload, ok: true}
		}
	}
	return table, v.Context.Identity, v.Context, tables, held, gap, grid, gridKind, nil
}

// frameReplies hands seed every section of v as the (method, request,
// reply) its dedicated read would have produced, whole: a bundle read from
// a recording or a step has its keyed tables as lists.
func frameReplies(v *o.BundleSnapshot, emergency EmergencyObservation, seed func(method string, request, reply proto.Message)) {
	frameRepliesWith(v, emergency, nil, func(method string, request proto.Message, reply func() proto.Message) {
		seed(method, request, reply())
	})
}

// frameRepliesWith is frameReplies for the live stream. Replies
// are built when read, so a frame no consumer reads costs none. With held
// (the hold's persistent table versions at this frame), v's keyed tables
// carry their envelope only: the typed table reads and the routine frame
// read the versions directly, and the replies derived from them (the
// routine colonists, the combat census) look rows up by id. The list
// replies that remain (the plain pawn and building lists, a step snapshot)
// build a whole list from the version, and only when read. Combat rows
// (CombatPawns) are not keyed and native sends them whole.
func frameRepliesWith(v *o.BundleSnapshot, emergency EmergencyObservation, held *heldTables, seedLazy func(method string, request proto.Message, reply func() proto.Message)) {
	seed := func(method string, request, reply proto.Message) {
		seedLazy(method, request, func() proto.Message { return reply })
	}
	seed("rimgovernor/lifecycle_read_tick", &l.TickRequest{}, &l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: v.Context, Paused: v.Paused}}})
	identity := v.Context.Identity
	if v.Emergency != nil {
		seed("rimgovernor/observations_read_status", emergencyRequest(identity), &o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: v.Emergency}})
	}
	if v.ColonyFacts != nil {
		seed("rimgovernor/observations_read_colony_facts", colonyFactsRequest(identity, true), &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: v.ColonyFacts}})
		// A read without planning is the same facts with the planning
		// section the native answers it with: without this shape
		// every non-planning read missed the frame and hopped the game
		// thread.
		seedLazy("rimgovernor/observations_read_colony_facts", colonyFactsRequest(identity, false), func() proto.Message {
			bare := proto.Clone(v.ColonyFacts).(*o.ColonyFactsSnapshot)
			bare.Planning = NotRequestedPlanning()
			return &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: bare}}
		})
	}
	if v.Population != nil {
		seed("rimgovernor/observations_read_population", populationRequest(identity), &o.PopulationReply{Outcome: &o.PopulationReply_Observed{Observed: v.Population}})
	}
	if v.Research != nil {
		seed("rimgovernor/observations_read_research", researchRequest(identity), &o.ResearchReply{Outcome: &o.ResearchReply_Observed{Observed: v.Research}})
	}
	if v.Things != nil {
		seedLazy(frameThingsMethod, nil, func() proto.Message { return v.Things })
	}
	if v.Pawns != nil {
		seedLazy(framePawnsMethod, nil, func() proto.Message { return held.wholePawns(v) })
		seedLazy("rimgovernor/observations_list_pawns", pawnDetailsRequest(identity, roundsPawnIDs(emergency), pawnDetails{Combat: true, Work: true, Care: true, Schedule: true, Social: true}), func() proto.Message {
			var colonists *o.PawnSnapshot
			var ok bool
			if held != nil {
				colonists, ok = roundsPawnRows(held.pawnMeta, held.pawns, emergency)
			} else {
				colonists, ok = roundsPawns(v.Pawns, emergency)
			}
			if !ok {
				return nil
			}
			return &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: colonists}}
		})
	}
	if v.Buildings != nil {
		seedLazy(frameBuildingsMethod, nil, func() proto.Message { return v.Buildings })
		seedLazy("rimgovernor/observations_list_buildings", buildingsListRequest(identity), func() proto.Message {
			return &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: held.wholeBuildings(v)}}
		})
		seedLazy("rimgovernor/observations_list_buildings", constructionBuildingsRequest(identity, nil), func() proto.Message {
			return &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: builtBuildings(held.wholeBuildings(v))}}
		})
	}
	if v.Bills != nil {
		seed("rimgovernor/observations_read_bills", billsListRequest(identity), &o.BillsReply{Outcome: &o.BillsReply_Observed{Observed: v.Bills}})
	}
	if v.Zones != nil {
		seed("rimgovernor/observations_list_zones", zoneSectionRequest(identity), &o.ListZonesReply{Outcome: &o.ListZonesReply_Observed{Observed: v.Zones}})
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
	seedLazy(combatFrameMethod, nil, func() proto.Message { return combatFrameHeld(v, held) })
	seedLazy(roundsFrameMethod, nil, func() proto.Message {
		routine := &o.BundleSnapshot{Context: v.Context, Emergency: v.Emergency, ColonyFacts: v.ColonyFacts, Population: v.Population, Research: v.Research,
			Zones: v.Zones, Traders: v.Traders, WorldProgression: v.WorldProgression, Ideology: v.Ideology, IdeologyActive: v.IdeologyActive, Rooms: v.Rooms, CombatEvents: podArrivals(v.CombatEvents)}
		if held == nil {
			// A whole bundle carries its tables; the stream's routine frame
			// takes them from the hold (ReadRoundsFrame).
			routine.Pawns, routine.Things, routine.Buildings = v.Pawns, v.Things, v.Buildings
		}
		return routine
	})
}

// frameTablesMethods are the typed table reads' keys.
const frameBuildingsMethod = "rimgovernor/snapshot_frame_buildings"

// roundsFrameMethod keys a frame's routine census sections in its table,
// frames-only like combatFrameMethod.
const roundsFrameMethod = "rimgovernor/snapshot_frame_routine"

// RoundsFrame is the routine census of one frame, each section
// decoded: every row has the frame's tick, so no section is checked
// against another. A nil section is one the frame does not carry.
type RoundsFrame struct {
	Context   *c.ObservationContext
	Colony    *o.ColonyFactsSnapshot
	Emergency EmergencyObservation
	// Pawns is the census colonists' rows of the pawn table, as the
	// routine list read answers them; nil without a complete census.
	Pawns      *o.PawnSnapshot
	Population *PrisonerCensus
	Research   *ResearchRead
	Traders    *TradersRead
	Quests     *WorldProgressionRead
	// Ideology is the primary ideoligion with the catalog's defs;
	// nil when the frame carries no ideology section.
	Ideology *policy.Ideoligion
	// IdeologyActive is whether the Ideology expansion is installed;
	// unknown when the frame does not say.
	IdeologyActive domain.Fact[bool]
	// Buildings is the frame's all-status player building table, whose
	// blueprint and frame rows carry their undelivered material; nil when
	// the frame carries none.
	Buildings *BuildingCensus
	// Tables are the frame's keyed tables every other section's
	// references resolve against: Sites by id and the pawn table.
	Tables Tables
	Zones  *ZonesRead
	// Catalog is the load's definition catalog, which the
	// research section and the planning definitions resolve against.
	Catalog *DefinitionCatalog
	// Rooms is the indoor room census; RoomCells are its rooms'
	// cells resolved against the newest frame grid (RoomCells).
	Rooms     *o.RoomsSnapshot
	RoomCells map[string][]domain.Cell
	// Bills is the bench bill census, whose rows carry live bill jobs'
	// ingredient reservations.
	Bills *o.BillsSnapshot
}

// ReadRoundsFrame decodes the routine census of the newest frame past
// this client's last write against the load's definition catalog; without
// a stream it is ErrUnavailable.
func (caller *Client) ReadRoundsFrame(ctx context.Context, identity *c.Identity) (RoundsFrame, error) {
	if caller.frames == nil {
		return RoundsFrame{}, fmt.Errorf("%w: the routine census is served only by the snapshot stream", ErrUnavailable)
	}
	if err := ValidateIdentity(identity); err != nil {
		return RoundsFrame{}, err
	}
	catalog, err := caller.DefinitionCatalog(ctx, identity)
	if err != nil {
		return RoundsFrame{}, err
	}
	var tables heldTables
	reply := &o.BundleSnapshot{}
	if _, err := caller.frameReadView(ctx, roundsFrameMethod, readCacheKey{method: roundsFrameMethod}, identity, true, reply, func(s *frameStream) { tables = s.held }); err != nil {
		return RoundsFrame{}, err
	}
	if err := tables.err(); err != nil {
		return RoundsFrame{}, err
	}
	frame, err := decodeRoundsFrame(reply, catalog, tables)
	if err != nil {
		return RoundsFrame{}, err
	}
	s := caller.frames
	if frame.Buildings != nil {
		frame.Buildings.Memo = &s.census
	}
	s.mu.Lock()
	held := s.grid
	s.mu.Unlock()
	if held.grid != nil && sameIdentity(held.context.GetIdentity(), identity) {
		frame.RoomCells = RoomCells(frame.Rooms, held.grid)
	}
	return frame, nil
}

// DecodeRoundsFrame validates and decodes a frame's routine sections
// against the load's definition catalog.
func DecodeRoundsFrame(v *o.BundleSnapshot, catalog *DefinitionCatalog) (RoundsFrame, error) {
	if v == nil || ValidateContext(v.Context) != nil {
		return RoundsFrame{}, contract("routine frame without a context")
	}
	identity := v.Context.Identity
	pawns, err := PawnTable(v.Pawns, identity)
	if err != nil {
		return RoundsFrame{}, err
	}
	things, err := ThingTable(v.Things, identity)
	if err != nil {
		return RoundsFrame{}, err
	}
	tables := heldTables{pawns: pawns, pawnMeta: v.Pawns, buildings: BuildingTable(v.Buildings), buildMeta: v.Buildings, things: things}
	if census := BuildingCensusOf(v.Buildings); census != nil {
		tables.buildErr = census.Invalid
	}
	return decodeRoundsFrame(v, catalog, tables)
}

// decodeRoundsFrame decodes v's routine sections; its keyed tables are
// tables, never v's lists.
func decodeRoundsFrame(v *o.BundleSnapshot, catalog *DefinitionCatalog, tables heldTables) (RoundsFrame, error) {
	identity := v.Context.Identity
	out := RoundsFrame{Context: v.Context, Colony: v.ColonyFacts, Catalog: catalog, Rooms: v.Rooms, Bills: v.Bills}
	if tables.buildMeta != nil {
		out.Buildings = &BuildingCensus{Context: tables.buildMeta.Context, Rows: tables.buildings, Invalid: tables.buildErr}
	}
	pawns := tables.pawns
	var err error
	out.Tables = Tables{Buildings: tables.buildings, Pawns: pawns, Things: tables.things, Catalog: catalog}
	if v.Emergency != nil {
		if out.Emergency, err = DecodeEmergencyStatus(v.Emergency, pawns, identity); err != nil {
			return RoundsFrame{}, err
		}
		podsPending(&out.Emergency, v.CombatEvents)
		if tables.pawnMeta != nil {
			if colonists, ok := roundsPawnRows(tables.pawnMeta, pawns, out.Emergency); ok {
				out.Pawns = colonists
			}
		}
	}
	if v.Population != nil {
		population, err := decodePopulation(v.Population, pawns, catalog)
		if err != nil {
			return RoundsFrame{}, err
		}
		out.Population = &population
	}
	if v.Research != nil {
		research, err := readResearchSnapshot(v.Research, identity, catalog)
		if err != nil {
			return RoundsFrame{}, err
		}
		out.Research = &research
	}
	if v.Traders != nil {
		traders, err := decodeTraders(v.Traders, identity)
		if err != nil {
			return RoundsFrame{}, err
		}
		out.Traders = &traders
	}
	if v.WorldProgression != nil {
		quests, err := worldProgressionSelected(v.WorldProgression, identity)
		if err != nil {
			return RoundsFrame{}, err
		}
		out.Quests = &quests
	}
	if v.Ideology != nil {
		ideology, err := DecodeIdeology(v.Ideology, identity, catalog)
		if err != nil {
			return RoundsFrame{}, err
		}
		out.Ideology = ideology
		if v.IdeologyActive != nil && !v.GetIdeologyActive() {
			return RoundsFrame{}, contract("ideology section without the Ideology expansion")
		}
	}
	if v.IdeologyActive != nil {
		out.IdeologyActive = domain.Known(v.GetIdeologyActive())
	}
	if v.Zones != nil {
		zones, err := decodeZones(v.Zones, identity)
		if err != nil {
			return RoundsFrame{}, err
		}
		out.Zones = &zones
	}
	return out, nil
}

// BundleEmergency decodes a step snapshot's emergency section into the
// observation ReadEmergency returns, with the step's pending drop pods.
func BundleEmergency(v *o.BundleSnapshot) (EmergencyObservation, error) {
	if v == nil || v.Emergency == nil {
		return EmergencyObservation{}, contract("emergency section missing")
	}
	pawns, err := PawnTable(v.Pawns, v.Context.GetIdentity())
	if err != nil {
		return EmergencyObservation{}, err
	}
	out, err := DecodeEmergencyStatus(v.Emergency, pawns, v.Context.GetIdentity())
	if err != nil {
		return EmergencyObservation{}, err
	}
	podsPending(&out, v.CombatEvents)
	return out, nil
}

// podArrivals are the drop-pod arrival rows among a frame's combat events:
// what the routine frame and the clock's step carry of them.
func podArrivals(events []*mp.CombatEventRow) []*mp.CombatEventRow {
	var out []*mp.CombatEventRow
	for _, row := range events {
		if DropPodArrival(row) {
			out = append(out, row)
		}
	}
	return out
}

// podsPending sets e's PodsOpen to the latest open tick of a drop-pod
// arrival among events whose pods are still closed at e's census tick:
// its raiders are in their pods, not in the census.
func podsPending(e *EmergencyObservation, events []*mp.CombatEventRow) {
	for _, row := range events {
		if open := int64(row.GetOpenTick()); DropPodArrival(row) && open >= e.Context.GetTick() && domain.Tick(open) > e.Facts.PodsOpen {
			e.Facts.PodsOpen = domain.Tick(open)
		}
	}
}

// roundsPawns is the census colonists' rows of table as the routine
// list read answers them, false without a complete census or when the
// table misses one.
func roundsPawns(table *o.PawnSnapshot, emergency EmergencyObservation) (*o.PawnSnapshot, bool) {
	if table == nil {
		return nil, false
	}
	return roundsPawnRows(table, NewPawns(table.Pawns...), emergency)
}

// roundsPawnRows answers the routine list read from table's envelope and
// the rows census colonists resolve against.
func roundsPawnRows(table *o.PawnSnapshot, rows pawnLookup, emergency EmergencyObservation) (*o.PawnSnapshot, bool) {
	ids := roundsPawnIDs(emergency)
	if len(ids) == 0 {
		return nil, false
	}
	out, ok := pawnSnapshot(table.Context, ids, rows)
	if ok {
		out.MeditateAssignmentAvailable = table.MeditateAssignmentAvailable
	}
	return out, ok
}

// roundsPawnIDs lists the colonists the routine census reads pawn detail
// for, in census order: every colonist of a complete emergency census, none
// otherwise (the bracket then reads no pawns at all).
func roundsPawnIDs(emergency EmergencyObservation) []string {
	if complete, known := emergency.Facts.ColonistsComplete.Value(); !known || !complete {
		return nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	return ids
}

// RoutePathBudgetMS is the main-thread time one frame may spend pathing
// facility x colonist pairs for the routes census (about 0.4 ms a pair, so
// roughly 250 pairs: the cost the former 256-pair cap paid at realistic size).
// Pairs past it arrive as RouteTravel.path_skipped.
const RoutePathBudgetMS uint32 = 100

// HuntRouteBudgetMS is the main-thread time one colony facts read may spend on
// hunter x prey RouteSafe pairs (0.2-0.45 ms each, so 100-250 pairs); pairs
// past it arrive as HuntRoute.skipped. HerdRadius is the cell radius of the
// same-race herd count on a hunt row.
const (
	HuntRouteBudgetMS uint32 = 50
	HerdRadius        uint32 = 25
)

// frameReader is the open stream, starting an open or resubscription in
// the background when one is due; nil until the first open lands.
func (caller *Client) frameReader(ctx context.Context) frameReader {
	s := caller.frames
	s.mu.Lock()
	defer s.mu.Unlock()
	if (s.reader == nil || s.stale || s.keyframe) && !s.opening && (s.stale || s.keyframe || time.Since(s.attempted) >= frameRetry) {
		request := &o.SnapshotStreamRequest{ResourceSources: slices.Clone(s.resources), RoutePathBudgetMs: proto.Uint32(RoutePathBudgetMS), HuntRouteBudgetMs: proto.Uint32(HuntRouteBudgetMS), HerdRadius: proto.Uint32(HerdRadius)}
		if s.reader != nil && !s.stale {
			// Only a keyframe: the subscription stands.
			request = &o.SnapshotStreamRequest{Keyframe: proto.Bool(true)}
		}
		s.opening, s.attempted, s.stale, s.keyframe = true, time.Now(), false, false
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
	// Frames captured before the resubscription lack what it added; a
	// keyframe request added nothing.
	if !request.GetKeyframe() {
		s.number, s.table, s.identity = 0, nil, nil
	}
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
// when asked: a resource's sources.
func (caller *Client) frameSubscribe(name string, request proto.Message) {
	s := caller.frames
	s.mu.Lock()
	defer s.mu.Unlock()
	switch r := request.(type) {
	case *o.ResourceSourcesRequest:
		if validID(r.GetResource()) == nil && !slices.Contains(s.resources, r.GetResource()) {
			s.resources = append(s.resources, r.GetResource())
			s.stale = true
		}
	}
}

// close unmaps the ring and forgets everything tied to it (the write
// count frames must reach, the decoded frame), keeping the subscription:
// a reconnect may be to a new game process with a ring of its own, so the
// next read opens the stream afresh.
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
	s.hold, s.keyframe = sectionHold{}, false
	s.gridHold, s.grid = gridHold{}, frameGrid{}
}

// combatFrameMethod keys a frame's combat sections in its table: a
// frames-only read with no GABP method behind it.
const combatFrameMethod = "rimgovernor/snapshot_frame_combat"

// Combat is a frame's combat state and the defense planner's other
// inputs from the same frame: every colonist, hostile and colony
// animal while combat is active, the native's retained event ring on the
// map, oldest first, the emergency census, the combat pawn detail rows for
// its colonists, hostiles and hunting predators, and the lines of fire
// from ranged colonists to hostile buildings. Context is the frame's; Frame
// is the frame's combat part as read, which a combat recording keeps.
type Combat struct {
	World     *WorldProgressionRead
	Context   *c.ObservationContext
	Pawns     []*mp.CombatPawn
	Events    []*mp.CombatEventRow
	Emergency EmergencyObservation
	// Detail is the frame's pawn table cut to the emergency census:
	// colonists, hostiles and predators with their combat detail.
	Detail Pawns
	Lines  []LineOfFire
	// Rooms are the map's standing rectangular rooms.
	Rooms      []policy.CombatRoom
	DoorStates domain.Fact[[]policy.RoomDoor]
	// Doors are the damaged player doors.
	Doors []*mp.CombatDoorRow
	// Mortars are the unroofed player mortars.
	Mortars []policy.CombatMortar
	// OutdoorTemperatureC is the colony facts outdoor temperature.
	OutdoorTemperatureC domain.Fact[float64]
	// HiveTemperatureC is the hottest live hive's temperature.
	HiveTemperatureC domain.Fact[float64]
	Frame            *o.BundleSnapshot
	// Catalog is the load's definition catalog: the mech kinds, the
	// weapons' def rows and the race rows resolve against it.
	Catalog *DefinitionCatalog
	// Shells are the load's mortar shells by kind, read off Catalog.
	Shells policy.MortarShells
	// Things is the frame's things table cut to the census pawns' primary
	// weapons: a gear reference carries no def name, so the weapons resolve
	// through it.
	Things Things
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
	combat, err := DecodeCombat(reply)
	if err != nil {
		return Combat{}, err
	}
	// The weapons' facts, the mech guard orders and a fight's
	// race flags (mech, insect, body size: the catalog's race rows)
	// resolve against the catalog of the load.
	if combat.Catalog, err = caller.DefinitionCatalog(ctx, identity); err != nil {
		return Combat{}, err
	}
	// An unbuildable race table fails the read here, so the fight's race
	// lookups (which reuse the built table) never see the error.
	if _, err = combat.Catalog.AnimalRaces(); err != nil {
		return Combat{}, err
	}
	if combat.Shells, err = combat.Catalog.MortarShells(policy.MortarSafeRadius); err != nil {
		return Combat{}, err
	}
	return combat, nil
}

// combatFrame is the part of frame v a combat read answers: its pawn
// table cut to the rows the emergency census references.
func combatFrame(v *o.BundleSnapshot) *o.BundleSnapshot { return combatFrameHeld(v, nil) }

// combatFrameHeld is combatFrame with the census rows looked up in the
// hold's pawn table, when there is one, instead of walking v's list.
func combatFrameHeld(v *o.BundleSnapshot, held *heldTables) *o.BundleSnapshot {
	out := &o.BundleSnapshot{Context: v.Context, Emergency: v.Emergency, Pawns: censusPawns(v, held), CombatPawns: v.CombatPawns, CombatEvents: v.CombatEvents, CombatLinesOfFire: v.CombatLinesOfFire, Rooms: v.Rooms, CombatDoors: v.CombatDoors, CombatMortars: v.CombatMortars, CombatHiveTemperatureC: v.CombatHiveTemperatureC, WorldProgression: v.WorldProgression}
	if t := frameOutdoorC(v); t != nil {
		out.ColonyFacts = &o.ColonyFactsSnapshot{OutdoorTemperatureC: t}
	}
	out.Things = primaryWeaponThings(v, held, out.Pawns)
	return out
}

// primaryWeaponThings is the rows of things the census pawns' primary
// weapons name, so a fight's frame resolves their defs on its own.
// A stream frame carries only the things its re-read sections referenced, so
// a hold's table is the source when there is one: the pawn rows come from
// it too.
func primaryWeaponThings(v *o.BundleSnapshot, held *heldTables, pawns *o.PawnSnapshot) *o.ThingsSnapshot {
	if pawns == nil || held == nil && v.Things == nil {
		return nil
	}
	out := &o.ThingsSnapshot{Context: v.Context}
	if held != nil {
		for _, pawn := range pawns.Pawns {
			if id := pawn.GetEquipment().GetPrimaryId(); id != "" {
				if row, ok := held.things.Get(id); ok && row != nil {
					out.Things = append(out.Things, row)
				}
			}
		}
		return out
	}
	ids := map[string]bool{}
	for _, pawn := range pawns.Pawns {
		if id := pawn.GetEquipment().GetPrimaryId(); id != "" {
			ids[id] = true
		}
	}
	out.Context = v.Things.Context
	for _, row := range v.Things.Things {
		if ids[row.GetThing().GetId()] {
			out.Things = append(out.Things, row)
		}
	}
	return out
}

// censusPawns is v's pawn table rows the emergency census references.
func censusPawns(v *o.BundleSnapshot, held *heldTables) *o.PawnSnapshot {
	if v.Pawns == nil || v.Emergency == nil {
		return nil
	}
	ids := map[string]bool{}
	for _, ref := range v.Emergency.Colonists {
		ids[ref.GetId()] = true
	}
	t := v.Emergency.GetThreats()
	for _, group := range [][]*o.ThreatPawn{t.GetPawns()} {
		for _, row := range group {
			ids[row.GetPawn().GetId()] = true
		}
	}
	// A squad hunt's prey: the open hunt rows of the colony census.
	for _, row := range v.GetColonyFacts().GetAcquisition() {
		if row.GetHunt() && !row.GetDesignated() && !row.GetTaken() {
			ids[row.GetSource().GetId()] = true
		}
	}
	// A living mechanitor's or mech's row rides with the census:
	// the guard orders read the mechs from the combat frame.
	if held != nil {
		for id, row := range held.pawns.All() {
			if row != nil && isMechRow(row) {
				ids[id] = true
			}
		}
	} else {
		for _, row := range v.Pawns.Pawns {
			if isMechRow(row) {
				ids[row.GetPawn().GetId()] = true
			}
		}
	}
	out := &o.PawnSnapshot{Context: v.Pawns.Context, Completeness: v.Pawns.Completeness, MeditateAssignmentAvailable: v.Pawns.MeditateAssignmentAvailable}
	if held != nil {
		for _, id := range slices.Sorted(maps.Keys(ids)) {
			if row, ok := held.pawns.Get(id); ok && row != nil {
				out.Pawns = append(out.Pawns, row)
			}
		}
		return out
	}
	for _, row := range v.Pawns.Pawns {
		if ids[row.GetPawn().GetId()] {
			out.Pawns = append(out.Pawns, row)
		}
	}
	return out
}

// isMechRow reports a living pawn carrying a mechanitor or mech block.
func isMechRow(row *o.PawnState) bool {
	b := row.GetBiotech()
	return !row.GetDead() && (b.GetMechanitor() != nil || b.GetMech() != nil)
}

// DecodeCombat validates and decodes a frame's combat part (ReadCombat,
// and a combat recording's replay).
func DecodeCombat(v *o.BundleSnapshot) (Combat, error) {
	if v == nil || ValidateContext(v.Context) != nil {
		return Combat{}, contract("combat frame without a context")
	}
	if err := validateCombat(v); err != nil {
		return Combat{}, err
	}
	out := Combat{Context: v.Context, Pawns: v.CombatPawns, Events: v.CombatEvents, Doors: v.CombatDoors, Frame: v}
	identity := v.Context.Identity
	if v.WorldProgression != nil {
		world, err := worldProgressionSelected(v.WorldProgression, identity)
		if err != nil {
			return Combat{}, err
		}
		out.World = &world
	}
	pawns, err := PawnTable(v.Pawns, identity)
	if err != nil {
		return Combat{}, err
	}
	out.Detail = pawns
	if out.Things, err = ThingTable(v.Things, identity); err != nil {
		return Combat{}, err
	}
	if v.Emergency != nil {
		emergency, err := DecodeEmergencyStatus(v.Emergency, pawns, identity)
		if err != nil {
			return Combat{}, err
		}
		podsPending(&emergency, v.CombatEvents)
		out.Emergency = emergency
	}
	if v.CombatLinesOfFire != nil {
		var firing, approach []domain.Cell
		seen := map[domain.Cell]int{}
		for _, row := range v.CombatLinesOfFire.Lines {
			from, _ := protoCell(row.GetFrom())
			to, _ := protoCell(row.GetTo())
			if seen[from]&1 == 0 {
				seen[from] |= 1
				firing = append(firing, from)
			}
			if seen[to]&2 == 0 {
				seen[to] |= 2
				approach = append(approach, to)
			}
		}
		lines, err := validateLinesOfFire(v.CombatLinesOfFire, identity, firing, approach)
		if err != nil {
			return Combat{}, err
		}
		out.Lines = lines
	}
	for _, row := range v.CombatMortars {
		c, _ := protoCell(row.GetCell())
		out.Mortars = append(out.Mortars, policy.CombatMortar{ID: row.GetId(), Cell: c, MinRange: float64(row.GetMinRange()), MaxRange: float64(row.GetMaxRange()), Loaded: row.GetLoadedShell()})
	}
	if t := frameOutdoorC(v); t != nil {
		out.OutdoorTemperatureC = domain.Known(*t)
	}
	if v.CombatHiveTemperatureC != nil {
		out.HiveTemperatureC = domain.Known(float64(v.GetCombatHiveTemperatureC()))
	}
	var doorStates []policy.RoomDoor
	for _, row := range v.GetRooms().GetRooms() {
		for _, door := range row.GetDoors() {
			doorStates = append(doorStates, RoomDoorFacts(door))
		}
		if room, ok := combatRoom(row); ok {
			out.Rooms = append(out.Rooms, room)
		}
	}
	if v.Rooms != nil {
		out.DoorStates = domain.Known(doorStates)
	}
	return out, nil
}

// maxCombatRoomCells bounds a combat room: a larger enclosure is not one.
const maxCombatRoomCells = 1024

// combatRoom is a frame room census row as the pods tactic's
// room: a proper room of at most maxCombatRoomCells whose cells fill its
// extents, with the doors in its boundary. A room of any other shape is
// left out.
func combatRoom(row *o.RoomState) (policy.CombatRoom, bool) {
	if !row.GetProperRoom() || row.GetDoorway() || row.GetCellCount() == 0 || row.GetCellCount() > maxCombatRoomCells {
		return policy.CombatRoom{}, false
	}
	lo, okLo := protoCell(row.GetExtents().GetMinimum())
	hi, okHi := protoCell(row.GetExtents().GetMaximum())
	if !okLo || !okHi || hi.X < lo.X || hi.Z < lo.Z {
		return policy.CombatRoom{}, false
	}
	w, h := hi.X-lo.X+1, hi.Z-lo.Z+1
	if int64(w)*int64(h) != int64(row.GetCellCount()) {
		return policy.CombatRoom{}, false
	}
	room := policy.CombatRoom{Interior: policy.Rectangle{X: lo.X, Z: lo.Z, Width: w, Height: h}, Roofed: row.OpenRoofCount != nil && row.GetOpenRoofCount() == 0}
	if row.Role != nil {
		room.Role = domain.Known(policy.RoomRole(row.GetRole()))
	}
	if row.Burning != nil {
		room.Burning = domain.Known(row.GetBurning())
	}
	for _, d := range row.GetDoors() {
		if c, ok := protoCell(d.GetCell()); ok {
			room.Doors = append(room.Doors, c)
		}
	}
	return room, true
}

// validateCombat checks a frame's combat rows.
func validateCombat(v *o.BundleSnapshot) error {
	if t := frameOutdoorC(v); t != nil && (*t != *t || *t < -300 || *t > 300) {
		return contract("combat outdoor temperature")
	}
	if t := v.CombatHiveTemperatureC; t != nil && (*t != *t || *t < -300 || *t > 1000) {
		return contract("combat hive temperature")
	}
	for _, row := range v.CombatMortars {
		if validID(row.GetId()) != nil || movementCell(row.Cell) != nil || row.MinRange == nil || row.MaxRange == nil || row.GetMinRange() < 0 || row.GetMaxRange() < row.GetMinRange() {
			return contract("combat mortar without id, cell or range")
		}
	}
	for _, row := range v.CombatDoors {
		if validID(row.GetId()) != nil || movementCell(row.Cell) != nil || row.HitPoints == nil || row.MaxHitPoints == nil || row.GetHitPoints() < 0 || row.GetHitPoints() > row.GetMaxHitPoints() {
			return contract("combat door without id, cell or hit points")
		}
	}
	for _, row := range v.CombatPawns {
		if validID(row.GetId()) != nil || row.GetSide() == mp.CombatSide_COMBAT_SIDE_UNSPECIFIED || row.Cell == nil {
			return contract("combat pawn without id, side or cell")
		}
		for _, n := range []*float64{row.Health, row.BleedRate, row.Pain, row.MoveSpeed, row.ShieldEnergy, row.MeleePower} {
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

// PodsStrategy is the raid strategy of a drop-pod arrival row.
const PodsStrategy = "pods"

// DropPodArrival reports whether row is a drop-pod raid's arrival:
// a hostile arrived row with strategy pods, landing cells and open tick.
func DropPodArrival(row *mp.CombatEventRow) bool {
	return row.GetKind() == mp.CombatLogKind_COMBAT_LOG_KIND_HOSTILE_ARRIVED && row.GetRaidStrategy() == PodsStrategy
}

// CombatEventID is a combat event row's key: its own watermark.
func CombatEventID(row *mp.CombatEventRow) string {
	return fmt.Sprintf("%d.%d", row.GetAt().GetTick(), row.GetAt().GetSeq())
}

// builtBuildings is the construction census carried by a frame's single
// buildings family: its rows whose status is built. The
// blueprints and frames reach the census from the unfiltered read
// (observation.WithSites).
func builtBuildings(v *o.BuildingsSnapshot) *o.BuildingsSnapshot {
	if v == nil {
		return nil
	}
	out := &o.BuildingsSnapshot{Context: v.Context, Completeness: v.Completeness}
	for _, row := range v.Buildings {
		if row.GetStatus() == o.BuildingStatus_BUILDING_STATUS_BUILT {
			out.Buildings = append(out.Buildings, row)
		}
	}
	return out
}

// frameOutdoorC is frame v's colony facts outdoor temperature, nil when
// the frame carries none.
func frameOutdoorC(v *o.BundleSnapshot) *float64 {
	if v.GetColonyFacts() == nil {
		return nil
	}
	return v.ColonyFacts.OutdoorTemperatureC
}
