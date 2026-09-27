// Package facts is the controller-side state store (#354): the decoded
// colony state a scheduler step planned against, held per section with the
// tick each section describes, so a later step can ask what is held and
// how old it is instead of reconstituting everything from a fresh bundle.
// It follows bridge.FactFamily for invalidation.
package facts

import (
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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
	Bills         Section = "bills"
)

// Sections lists every section in report order.
func Sections() []Section {
	return []Section{Colony, PlanningCells, Population, Research, Pawns, Emergency, Rooms, Zones, Buildings, Bills}
}

// Family is the bridge fact family whose invalidation the section follows.
func (s Section) Family() bridge.FactFamily {
	switch s {
	case Colony, PlanningCells, Zones, Buildings, Bills:
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

// Cells reports whether the section's rows are cells, so an invalidation
// narrowed to a rectangle leaves it held when the rectangle lies outside
// the region it holds.
func (s Section) Cells() bool { return s == PlanningCells }

// Entities reports a section of keyed rows (planning cells, zones,
// buildings, bills): the sections an invalidation narrowed to entity ids
// or cells names (Invalidation.Sections).
func (s Section) Entities() bool {
	switch s {
	case PlanningCells, Zones, Buildings, Bills:
		return true
	}
	return false
}

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

// Held is one section's decoded value with its provenance: AsOf is the
// tick the reply described, Complete whether the value covers the whole
// section (a partial census is held but marked), Source the native method
// that produced it and Region the cells a cell section covers (unknown for
// the rest).
type Held[T any] struct {
	Value    T
	AsOf     int64
	Complete bool
	Source   string
	Region   Rect
}

// Scope is the (load, native generation) a held section belongs to: a
// change empties the store.
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
	region   Rect
}

// Store holds the sections under one scope. One writer (the scheduler
// step) puts; the step goroutine and the HTTP API read. A nil Store holds
// nothing and accepts every call.
type Store struct {
	mu    sync.RWMutex
	scope Scope
	rows  map[Section]row
	// versions counts, per section, the invalidations the native
	// fact-change stream has named it in (an ObservationInvalidated row, an
	// operation outcome, a scope change): the per-section version a
	// domain.ReadValidity carries (#624). A refresh at cadence does not
	// move it; only evidence that the section changed does.
	versions map[Section]uint64
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
		s.bumpAll()
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
	return Held[T]{Value: value, AsOf: r.asOf, Complete: r.complete, Source: r.source, Region: r.region}, true
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
		s.drop(section)
	}
}

// bump moves a section's version: the native said it changed.
func (s *Store) bump(section Section) {
	if s.versions == nil {
		s.versions = map[Section]uint64{}
	}
	s.versions[section]++
}

// bumpAll moves every section's version (a scope change, a whole-view
// invalidation).
func (s *Store) bumpAll() {
	for _, section := range Sections() {
		s.bump(section)
	}
}

// Versions is each section's version, keyed by name, for a
// domain.ReadValidity; a section never invalidated is at zero.
func (s *Store) Versions() map[string]uint64 {
	if s == nil {
		return nil
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make(map[string]uint64, len(Sections()))
	for _, section := range Sections() {
		out[string(section)] = s.versions[section]
	}
	return out
}

// drop forgets a section and moves its version.
func (s *Store) drop(section Section) {
	s.bump(section)
	delete(s.rows, section)
}

// InvalidateFamily drops every section of the named families.
func (s *Store) InvalidateFamily(families ...bridge.FactFamily) {
	if s == nil || len(families) == 0 {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, family := range families {
		for _, section := range FamilySections(family) {
			s.drop(section)
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

// Sections names the sections the invalidation makes stale, the rows a
// scheduler routes to the planners declaring them (#625): every section of
// each family when nothing narrows it; the entity sections when entity
// ids do; the cell section when a rectangle does. A family without a
// section of the narrowed shape (ids against pawns) is dirty whole.
func (inv Invalidation) Sections() []Section {
	var out []Section
	add := func(section Section) {
		for _, held := range out {
			if held == section {
				return
			}
		}
		out = append(out, section)
	}
	for _, family := range inv.Families {
		if !inv.Narrowed() {
			for _, section := range FamilySections(family) {
				add(section)
			}
			continue
		}
		fitted := false
		for _, section := range FamilySections(family) {
			if section.Cells() && inv.Rect != nil || !section.Cells() && section.Entities() && len(inv.IDs) > 0 {
				add(section)
				fitted = true
			}
		}
		if !fitted {
			for _, section := range FamilySections(family) {
				add(section)
			}
		}
	}
	return out
}

// FamilySections lists the sections of one family in report order; a
// family without a section (world, definitions, identity) has none.
func FamilySections(family bridge.FactFamily) []Section {
	var out []Section
	for _, section := range Sections() {
		if section.Family() == family {
			out = append(out, section)
		}
	}
	return out
}

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

// Apply takes one invalidation: it drops every section of its families,
// except a cell section whose region a narrowing rectangle misses, which
// is unchanged, value and version both, so proposals planned from it hold
// (#656).
func (s *Store) Apply(inv Invalidation) {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, family := range inv.Families {
		for _, section := range FamilySections(family) {
			if r, ok := s.rows[section]; ok && section.Cells() && inv.Rect != nil && !inv.Rect.Intersects(r.region) {
				continue
			}
			s.drop(section)
		}
	}
}

// InvalidateAll drops every section and moves every version (a held or
// unheld section alike: an event gap or an unattributed mutation says
// nothing is known to have stayed the same); the scope is kept.
func (s *Store) InvalidateAll() {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for section := range s.rows {
		s.drop(section)
	}
	s.bumpAll()
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
