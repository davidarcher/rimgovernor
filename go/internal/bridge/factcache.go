package bridge

import (
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// FactFamily groups the cacheable observation reads that go stale together.
// Each family tolerates a bounded tick advance (TickTolerance): a row read
// at one tick is served under a later scope while the advance is within the
// family's tolerance, so a step can plan against facts a running clock has
// moved past by a little, and never against facts it has outrun.
type FactFamily string

const (
	FactDefinitions FactFamily = "definitions"
	FactWorld       FactFamily = "world"
	FactIdentity    FactFamily = "identity"
	FactColony      FactFamily = "colony"
	FactPawns       FactFamily = "pawns"
	FactEmergency   FactFamily = "emergency"
	FactRooms       FactFamily = "rooms"
	FactResearch    FactFamily = "research"
)

// FactFamilies lists every family, in wire order.
func FactFamilies() []FactFamily {
	return []FactFamily{FactDefinitions, FactWorld, FactIdentity, FactColony, FactPawns, FactEmergency, FactRooms, FactResearch}
}

// FactTickUnbounded is the tolerance of a family whose rows survive any
// tick advance within one (load, generation) scope.
const FactTickUnbounded int64 = -1

// Tick tolerances, in game ticks (60 ticks a second at Normal speed, 2500
// a game hour). Definitions and world facts do not change with the map's
// ticks. The research page moves on the scale of a day; buildings, zones,
// stock and rooms on the scale of an hour; pawns and the emergency census
// (positions, health, hostiles) within minutes. The identity family's
// payload is the tick itself, so an older row is a wrong fact by
// definition: it serves the same tick only, and every step's bundle seeds
// it afresh (FactCache.Context serves the last one held regardless).
const (
	FactTickToleranceIdentity  int64 = 0
	FactTickToleranceResearch  int64 = 60000
	FactTickToleranceColony    int64 = 2500
	FactTickToleranceRooms     int64 = 2500
	FactTickTolerancePawns     int64 = int64(domain.PlanningTickTolerance)
	FactTickToleranceEmergency int64 = int64(domain.PlanningTickTolerance)
)

// PlanningTickTolerance is how far a planning step's facts may predate the
// tick a window is admitted at: the tightest bounded family, since a plan
// is only as fresh as the pawn and emergency facts it read.
func PlanningTickTolerance() int64 {
	tolerance := FactTickUnbounded
	for _, family := range FactFamilies() {
		if t := family.TickTolerance(); t != FactTickUnbounded && t > 0 && (tolerance == FactTickUnbounded || t < tolerance) {
			tolerance = t
		}
	}
	return tolerance
}

// TickTolerance is the greatest tick advance a row of the family stays
// valid across, or FactTickUnbounded.
func (f FactFamily) TickTolerance() int64 {
	switch f {
	case FactDefinitions, FactWorld:
		return FactTickUnbounded
	case FactIdentity:
		return FactTickToleranceIdentity
	case FactResearch:
		return FactTickToleranceResearch
	case FactColony:
		return FactTickToleranceColony
	case FactRooms:
		return FactTickToleranceRooms
	case FactPawns:
		return FactTickTolerancePawns
	case FactEmergency:
		return FactTickToleranceEmergency
	}
	return 0
}

// Fresh reports whether a row of the family read at rowTick still serves a
// scope at scopeTick: the scope is never behind the row (a tick rewind is a
// new world) and not ahead of it by more than the tolerance.
func (f FactFamily) Fresh(rowTick, scopeTick int64) bool {
	advance := scopeTick - rowTick
	if advance < 0 {
		return false
	}
	tolerance := f.TickTolerance()
	return tolerance == FactTickUnbounded || advance <= tolerance
}

// FactFamilyOf names the family of a cacheable read (cacheableRead) and
// reports false for every other method.
func FactFamilyOf(method string) (FactFamily, bool) {
	switch method {
	case "rimgovernor/observations_read_recipes":
		return FactDefinitions, true
	case "rimgovernor/observations_read_world":
		return FactWorld, true
	case "rimgovernor/lifecycle_read_identity", "rimgovernor/lifecycle_read_tick":
		return FactIdentity, true
	case "rimgovernor/observations_list_supplies", "rimgovernor/observations_read_colony_facts", "rimgovernor/observations_list_buildings",
		"rimgovernor/observations_list_wall_upgrade_sites", "rimgovernor/observations_list_zones", "rimgovernor/observations_read_spatial_access",
		"rimgovernor/observations_get_cells", "rimgovernor/observations_read_bills", "rimgovernor/observations_list_resource_sources",
		"rimgovernor/observations_read_production_policy", "rimgovernor/observations_read_trade_sheet", "rimgovernor/observations_list_traders", "rimgovernor/observations_read_excavation_site",
		"rimgovernor/observations_read_caravan_catalog", "rimgovernor/observations_read_world_progression":
		return FactColony, true
	case "rimgovernor/observations_list_pawns", "rimgovernor/observations_read_population":
		return FactPawns, true
	case "rimgovernor/observations_read_status", "rimgovernor/observations_read_defense_site", "rimgovernor/observations_read_lines_of_fire":
		return FactEmergency, true
	case "rimgovernor/observations_list_rooms":
		return FactRooms, true
	case "rimgovernor/observations_read_research":
		return FactResearch, true
	}
	return "", false
}

// FactFamilyFromWire maps a clock ObservationInvalidated family to its
// controller name; an unspecified or unknown value reports false.
func FactFamilyFromWire(v k.FactFamily) (FactFamily, bool) {
	switch v {
	case k.FactFamily_FACT_FAMILY_DEFINITIONS:
		return FactDefinitions, true
	case k.FactFamily_FACT_FAMILY_WORLD:
		return FactWorld, true
	case k.FactFamily_FACT_FAMILY_IDENTITY:
		return FactIdentity, true
	case k.FactFamily_FACT_FAMILY_COLONY:
		return FactColony, true
	case k.FactFamily_FACT_FAMILY_PAWNS:
		return FactPawns, true
	case k.FactFamily_FACT_FAMILY_EMERGENCY:
		return FactEmergency, true
	case k.FactFamily_FACT_FAMILY_ROOMS:
		return FactRooms, true
	case k.FactFamily_FACT_FAMILY_RESEARCH:
		return FactResearch, true
	}
	return "", false
}

// FactCache keeps reviewed observation replies across scheduler steps. It is
// the parent of each step's StepReadCache: a step whose native reply has
// established its observation scope serves later reads of the same
// (method, request) from here when the row was read under the same load
// and native generation and either belongs to a family that survives a
// tick advance or was read within its family's tick tolerance of the step's
// tick. A timer step with no tick advance therefore issues one identity read
// and takes the rest of its facts locally.
//
// Rows are discarded by any write issued through a child cache, by the
// typed clock events the scheduler ingests (an operation outcome drops the
// families that operation touches; an authority change, epoch start or stop
// drops everything; an ObservationInvalidated event drops the families it
// names), and by a reply reporting a different (load, generation) scope.
type FactCache struct {
	mu    sync.Mutex
	load  string
	gen   uint64
	rows  map[readCacheKey]*factRow
	stats FactCacheStats
}

// FactCacheStats counts a parent's outcomes. Hits were served to a step
// cache; Stores are rows written from native replies; Invalidations counts
// the discard calls (whole-cache and per-family alike).
type FactCacheStats struct {
	Hits, Stores, Invalidations uint64
}

type factRow struct {
	tick     int64
	family   FactFamily
	payload  []byte
	result   Result
	storedAt time.Time
}

// factCacheNow stamps stored rows; tests substitute it.
var factCacheNow = time.Now

// CachedContext is the identity row Context serves: the observation
// context and pause state the scheduler last read, and when the cache
// stored it (wall clock), so a caller with a freshness bound can apply it.
type CachedContext struct {
	Context  *c.ObservationContext
	Paused   bool
	StoredAt time.Time
}

// NewFactCache returns an empty parent cache.
func NewFactCache() *FactCache {
	return &FactCache{rows: map[readCacheKey]*factRow{}}
}

// Stats returns the counts so far.
func (f *FactCache) Stats() FactCacheStats {
	if f == nil {
		return FactCacheStats{}
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.stats
}

// Len is the number of rows held.
func (f *FactCache) Len() int {
	if f == nil {
		return 0
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.rows)
}

// Invalidate discards every row.
func (f *FactCache) Invalidate() {
	if f == nil {
		return
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	f.rows = map[readCacheKey]*factRow{}
	f.stats.Invalidations++
}

// InvalidateFamilies discards the rows of the named families.
func (f *FactCache) InvalidateFamilies(families ...FactFamily) {
	if f == nil || len(families) == 0 {
		return
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	f.stats.Invalidations++
	for key, row := range f.rows {
		for _, family := range families {
			if row.family == family {
				delete(f.rows, key)
				break
			}
		}
	}
}

// lookup serves key under scope: the row must have been read under the same
// load and generation and be fresh for its family at scope.tick. A row the
// scope has outrun is dropped.
func (f *FactCache) lookup(key readCacheKey, scope readScope) ([]byte, Result, bool) {
	if f == nil {
		return nil, Result{}, false
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	if scope.load != f.load || scope.generation != f.gen {
		return nil, Result{}, false
	}
	row := f.rows[key]
	if row == nil {
		return nil, Result{}, false
	}
	if !row.family.Fresh(row.tick, scope.tick) {
		delete(f.rows, key)
		return nil, Result{}, false
	}
	f.stats.Hits++
	return row.payload, row.result, true
}

// Context returns the identity row the cache holds (the tick the bundle
// seeds each scheduler step, or a full identity read), regardless of the
// tick it was read at, and false when none is held. The identity family
// is dropped by every write and by every scope change, so a held row is
// what the scheduler last observed; its StoredAt bounds how long ago. No
// step scope is established by it; a caller's own first native read still
// does that.
func (f *FactCache) Context() (CachedContext, bool) {
	if f == nil {
		return CachedContext{}, false
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	if row := f.rows[readCacheKey{method: "rimgovernor/lifecycle_read_tick"}]; row != nil {
		reply := &l.TickReply{}
		if err := proto.Unmarshal(row.payload, reply); err == nil && reply.GetLoaded().GetContext() != nil {
			f.stats.Hits++
			return CachedContext{Context: reply.GetLoaded().GetContext(), Paused: reply.GetLoaded().GetPaused(), StoredAt: row.storedAt}, true
		}
	}
	if row := f.rows[readCacheKey{method: "rimgovernor/lifecycle_read_identity"}]; row != nil {
		reply := &l.IdentityReply{}
		if err := proto.Unmarshal(row.payload, reply); err == nil && reply.GetLoaded().GetContext() != nil {
			f.stats.Hits++
			return CachedContext{Context: reply.GetLoaded().GetContext(), Paused: reply.GetLoaded().GetPaused(), StoredAt: row.storedAt}, true
		}
	}
	return CachedContext{}, false
}

// store keeps a native reply read under scope. A different (load,
// generation) than the rows held resets the cache to the new scope first.
func (f *FactCache) store(key readCacheKey, scope readScope, payload []byte, result Result) {
	if f == nil {
		return
	}
	family, ok := FactFamilyOf(key.method)
	if !ok {
		return
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	if scope.load != f.load || scope.generation != f.gen {
		f.rows = map[readCacheKey]*factRow{}
		f.load, f.gen = scope.load, scope.generation
	}
	f.rows[key] = &factRow{tick: scope.tick, family: family, payload: payload, result: result, storedAt: factCacheNow()}
	f.stats.Stores++
}
