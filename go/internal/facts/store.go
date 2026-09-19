// Package facts is the controller-side state store (#354): the decoded
// colony state a scheduler step planned against, held per section with the
// tick each section describes, so a later step can ask what is held and
// how old it is instead of reconstituting everything from a fresh bundle.
// It stands beside bridge.FactCache (a wire-byte memo keyed by request)
// and reuses its families for tick tolerance and invalidation.
package facts

import (
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// Section names one decoded state section. Today each maps to a bundle or
// routine census family; planning_cells is a section of the colony facts
// with its own as-of so its source can change without touching consumers.
type Section string

const (
	Colony        Section = "colony"
	PlanningCells Section = "planning_cells"
	Population    Section = "population"
	Research      Section = "research"
	Pawns         Section = "pawns"
	Emergency     Section = "emergency"
	Rooms         Section = "rooms"
	Zones         Section = "zones"
	Buildings     Section = "buildings"
)

// Sections lists every section in report order.
func Sections() []Section {
	return []Section{Colony, PlanningCells, Population, Research, Pawns, Emergency, Rooms, Zones, Buildings}
}

// Family is the bridge fact family whose tick tolerance and invalidation
// the section follows.
func (s Section) Family() bridge.FactFamily {
	switch s {
	case Colony, PlanningCells, Zones, Buildings:
		return bridge.FactColony
	case Population, Pawns:
		return bridge.FactPawns
	case Research:
		return bridge.FactResearch
	case Emergency:
		return bridge.FactEmergency
	case Rooms:
		return bridge.FactRooms
	}
	return ""
}

// TickTolerance is the section's refresh cadence (#360): the greatest tick
// advance a held section serves a review step across before the step reads
// it again. The continuous families do not follow their invalidation
// family's memo tolerance where that is tighter than their consumers need:
// the population census moves on the colony's scale (a prisoner's
// resistance, a guest's stay), not the pawn census's. Every other section
// keeps its family's tolerance.
func (s Section) TickTolerance() int64 {
	if s == Population {
		return bridge.FactTickToleranceColony
	}
	return s.Family().TickTolerance()
}

// Cells reports whether the section's rows are cells, so an invalidation
// narrowed to a rectangle can leave it alone when the rectangle lies
// outside the region it holds.
func (s Section) Cells() bool { return s == PlanningCells }

// Rect is inclusive cell bounds. The zero Rect is "unknown", which
// intersects everything.
type Rect struct{ MinX, MinZ, MaxX, MaxZ int32 }

// Unknown reports the zero Rect.
func (r Rect) Unknown() bool { return r == Rect{} }

// Intersects reports whether the two rectangles share a cell; an unknown
// rectangle on either side counts as intersecting.
func (r Rect) Intersects(o Rect) bool {
	if r.Unknown() || o.Unknown() {
		return true
	}
	return r.MinX <= o.MaxX && o.MinX <= r.MaxX && r.MinZ <= o.MaxZ && o.MinZ <= r.MaxZ
}

// Union is the smallest rectangle covering both.
func (r Rect) Union(o Rect) Rect {
	if r.Unknown() || o.Unknown() {
		return Rect{}
	}
	return Rect{MinX: min(r.MinX, o.MinX), MinZ: min(r.MinZ, o.MinZ), MaxX: max(r.MaxX, o.MaxX), MaxZ: max(r.MaxZ, o.MaxZ)}
}

// Staleness is the part of a held section a narrowed invalidation named
// since it was put: entity ids for entity sections, a rectangle for cell
// sections, or the whole section when the narrowing did not fit its shape.
// The value stays held (a plan may still reason over it; apply refuses
// stale intent) but the section no longer reads as fresh.
type Staleness struct {
	IDs  []string
	Rect *Rect
	All  bool
}

// Any reports whether anything is marked stale.
func (st Staleness) Any() bool { return st.All || st.Rect != nil || len(st.IDs) > 0 }

// Held is one section's decoded value with its provenance: AsOf is the
// tick the reply described, Complete whether the value covers the whole
// section (a partial census is held but marked), Source the native method
// that produced it, Region the cells a cell section covers (unknown for
// the rest) and Stale what a narrowed invalidation has marked since.
type Held[T any] struct {
	Value    T
	AsOf     int64
	Complete bool
	Source   string
	Region   Rect
	Stale    Staleness
}

// Scope is the (load, native generation) a held section belongs to, the
// same rule bridge.FactCache applies: a change empties the store.
type Scope struct {
	Load       string
	Generation uint64
}

// Status is one held section as the HTTP API reports it.
type Status struct {
	Section  Section
	Family   bridge.FactFamily
	AsOf     int64
	Complete bool
	Source   string
	StoredAt time.Time
	Stale    Staleness
}

type row struct {
	value    any
	asOf     int64
	complete bool
	source   string
	storedAt time.Time
	region   Rect
	stale    Staleness
}

// Store holds the sections under one scope. One writer (the scheduler
// step) puts; the step goroutine and the HTTP API read. A nil Store holds
// nothing and accepts every call.
type Store struct {
	mu    sync.RWMutex
	scope Scope
	rows  map[Section]row
}

// storeNow stamps rows; tests substitute it.
var storeNow = time.Now

func NewStore() *Store {
	return &Store{rows: map[Section]row{}}
}

// Put holds section under scope; a scope other than the rows held empties
// the store first.
func Put[T any](s *Store, scope Scope, section Section, held Held[T]) {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if scope != s.scope {
		s.rows = map[Section]row{}
		s.scope = scope
	}
	s.rows[section] = row{value: held.Value, asOf: held.AsOf, complete: held.Complete, source: held.Source, storedAt: storeNow(), region: held.Region}
}

// Get returns the held section and false when none is held or it was put
// as another type.
func Get[T any](s *Store, section Section) (Held[T], bool) {
	if s == nil {
		return Held[T]{}, false
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	r, ok := s.rows[section]
	if !ok {
		return Held[T]{}, false
	}
	value, ok := r.value.(T)
	if !ok {
		return Held[T]{}, false
	}
	return Held[T]{Value: value, AsOf: r.asOf, Complete: r.complete, Source: r.source, Region: r.region, Stale: r.stale}, true
}

// Fresh reports whether the held section still serves a step at scopeTick
// under the section's cadence (TickTolerance, widened by the running
// window's drift as bridge.FactFamily.Fresh is); an absent or
// stale-marked section is never fresh.
func (s *Store) Fresh(section Section, scopeTick int64) bool {
	return s.FreshWithin(section, scopeTick, bridge.FactTickUnbounded)
}

// FreshWithin is Fresh under the tighter of the section's cadence and
// maxAge, a policy's own bound in ticks (#360): a consumer that needs a
// continuous value fresher than the cadence asks with it, and a section
// older than that is read again. FactTickUnbounded leaves the cadence
// alone; zero serves the step's own tick only.
func (s *Store) FreshWithin(section Section, scopeTick, maxAge int64) bool {
	if s == nil {
		return false
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	r, ok := s.rows[section]
	return ok && !r.stale.Any() && sectionFresh(section, r.asOf, scopeTick, maxAge)
}

// sectionFresh is bridge.FactFamily.Fresh with the section's cadence,
// bounded by maxAge when that is tighter.
func sectionFresh(section Section, rowTick, scopeTick, maxAge int64) bool {
	advance := scopeTick - rowTick
	if advance < 0 {
		return false
	}
	tolerance := section.TickTolerance()
	if maxAge != bridge.FactTickUnbounded && (tolerance == bridge.FactTickUnbounded || maxAge < tolerance) {
		tolerance = maxAge
	}
	return tolerance == bridge.FactTickUnbounded || advance <= tolerance+int64(domain.LiveDrift())
}

// Scope is the scope the held rows belong to.
func (s *Store) Scope() Scope {
	if s == nil {
		return Scope{}
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.scope
}

// Len is the number of sections held.
func (s *Store) Len() int {
	if s == nil {
		return 0
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	return len(s.rows)
}

// Invalidate drops the named sections.
func (s *Store) Invalidate(sections ...Section) {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, section := range sections {
		delete(s.rows, section)
	}
}

// InvalidateFamily drops every section of the named families, as
// bridge.FactCache.InvalidateFamilies drops their rows.
func (s *Store) InvalidateFamily(families ...bridge.FactFamily) {
	if s == nil || len(families) == 0 {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for section := range s.rows {
		for _, family := range families {
			if section.Family() == family {
				delete(s.rows, section)
				break
			}
		}
	}
}

// Invalidation is one decoded ObservationInvalidated (#359): the families
// that changed and, when native could attribute the change, the entity
// ids and the one rectangle of cells it touched.
type Invalidation struct {
	Families []bridge.FactFamily
	IDs      []string
	Rect     *Rect
}

// Narrowed reports whether the invalidation names rows rather than whole
// families.
func (inv Invalidation) Narrowed() bool { return len(inv.IDs) > 0 || inv.Rect != nil }

// InvalidationFromWire decodes an ObservationInvalidated; an unknown
// family reports false. A rectangle is taken as the decoder validated it
// (bridge accepts only present, ordered bounds).
func InvalidationFromWire(o *k.ObservationInvalidated) (Invalidation, bool) {
	var inv Invalidation
	for _, wire := range o.GetFamilies() {
		family, ok := bridge.FactFamilyFromWire(wire)
		if !ok {
			return Invalidation{}, false
		}
		inv.Families = append(inv.Families, family)
	}
	inv.IDs = append(inv.IDs, o.GetEntityIds()...)
	if cells := o.GetCells(); cells != nil {
		inv.Rect = &Rect{MinX: cells.GetMinimum().GetX(), MinZ: cells.GetMinimum().GetZ(), MaxX: cells.GetMaximum().GetX(), MaxZ: cells.GetMaximum().GetZ()}
	}
	return inv, true
}

// Apply takes one invalidation. Without narrowing it drops the families'
// sections (InvalidateFamily). Narrowed, it keeps every value and marks:
// the ids on the families' entity sections; the rectangle on a cell
// section whose region it intersects (a disjoint cell section stays
// fresh); the whole section when the narrowing does not fit its shape
// (ids alone against cells, a rectangle alone against entities).
func (s *Store) Apply(inv Invalidation) {
	if s == nil {
		return
	}
	if !inv.Narrowed() {
		s.InvalidateFamily(inv.Families...)
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for section, r := range s.rows {
		if !containsFamily(inv.Families, section.Family()) {
			continue
		}
		switch {
		case section.Cells() && inv.Rect != nil:
			if inv.Rect.Intersects(r.region) {
				marked := *inv.Rect
				if r.stale.Rect != nil {
					marked = marked.Union(*r.stale.Rect)
				}
				r.stale.Rect = &marked
			}
		case !section.Cells() && len(inv.IDs) > 0:
			for _, id := range inv.IDs {
				if !containsID(r.stale.IDs, id) {
					r.stale.IDs = append(r.stale.IDs, id)
				}
			}
		default:
			r.stale.All = true
		}
		s.rows[section] = r
	}
}

func containsFamily(families []bridge.FactFamily, family bridge.FactFamily) bool {
	for _, f := range families {
		if f == family {
			return true
		}
	}
	return false
}

func containsID(ids []string, id string) bool {
	for _, held := range ids {
		if held == id {
			return true
		}
	}
	return false
}

// InvalidateAll drops every section; the scope is kept.
func (s *Store) InvalidateAll() {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.rows = map[Section]row{}
}

// Status lists the held sections in Sections order.
func (s *Store) Status() []Status {
	if s == nil {
		return nil
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]Status, 0, len(s.rows))
	for _, section := range Sections() {
		if r, ok := s.rows[section]; ok {
			out = append(out, Status{Section: section, Family: section.Family(), AsOf: r.asOf, Complete: r.complete, Source: r.source, StoredAt: r.storedAt, Stale: r.stale})
		}
	}
	return out
}

// AsOf is the tick each held section describes.
func (s *Store) AsOf() map[Section]int64 {
	if s == nil {
		return nil
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make(map[Section]int64, len(s.rows))
	for section, r := range s.rows {
		out[section] = r.asOf
	}
	return out
}

// Spread is the oldest tick among the sections and how far the newest is
// ahead of it (max - min); zero and zero when nothing is held. A non-zero
// spread means a step planned against sections from different ticks.
func Spread(asOf map[Section]int64) (min, spread int64) {
	first := true
	var max int64
	for _, tick := range asOf {
		if first || tick < min {
			min = tick
		}
		if first || tick > max {
			max = tick
		}
		first = false
	}
	return min, max - min
}
