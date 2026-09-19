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

// Held is one section's decoded value with its provenance: AsOf is the
// tick the reply described, Complete whether the value covers the whole
// section (a partial census is held but marked), Source the native method
// that produced it.
type Held[T any] struct {
	Value    T
	AsOf     int64
	Complete bool
	Source   string
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
}

type row struct {
	value    any
	asOf     int64
	complete bool
	source   string
	storedAt time.Time
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
	s.rows[section] = row{value: held.Value, asOf: held.AsOf, complete: held.Complete, source: held.Source, storedAt: storeNow()}
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
	return Held[T]{Value: value, AsOf: r.asOf, Complete: r.complete, Source: r.source}, true
}

// Fresh reports whether the held section still serves a step at scopeTick
// under its family's tolerance (bridge.FactFamily.Fresh); an absent
// section is never fresh.
func (s *Store) Fresh(section Section, scopeTick int64) bool {
	if s == nil {
		return false
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	r, ok := s.rows[section]
	return ok && section.Family().Fresh(r.asOf, scopeTick)
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
			out = append(out, Status{Section: section, Family: section.Family(), AsOf: r.asOf, Complete: r.complete, Source: r.source, StoredAt: r.storedAt})
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
