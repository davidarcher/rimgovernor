// Package mirror is the controller's colony mirror (#795): per section, the
// keyed rows the native last described and the watermark tick they are
// complete through, kept current by changed-since reads (upserts and
// tombstones, #357/#358) instead of whole-section pulls. A keyframe (a
// full read) seeds a section, replaces it on a cadence as the backstop
// against a missed change, and replaces every section when the world the
// rows belong to changes (a reload, a map change or an authority
// generation flip).
//
// A section plugs in by implementing Section: the read that answers a
// keyframe (since 0) or the rows changed at or after a watermark. Tables
// are immutable once published, so a View is a consistent, versioned
// snapshot of every section however the mirror moves after it is taken.
package mirror

import (
	"context"
	"errors"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// ErrExpired is a delta the native refused because the watermark is older
// than its tombstone window: the section is read as a keyframe instead.
var ErrExpired = bridge.ErrDeltaExpired

// TombstoneWindow is the entity sections' Window (bridge.EntityTombstoneWindow).
const TombstoneWindow = bridge.EntityTombstoneWindow

// ResyncEvery is the backstop cadence: every this-many delta refreshes of
// a section is also a keyframe read, and the delta's rows are compared
// against it (Outcome.Drift).
const ResyncEvery = 8

// Scope is the world a mirror's rows belong to; any change empties it, so
// the next refresh of every section is a keyframe.
type Scope struct {
	Load       string
	Map        int32
	Generation uint64
}

// Watermark is how far a section is complete: every change through Tick,
// and within Tick every change up to Seq. Seq orders changes inside one
// tick (a paused game changes without the tick moving); a read whose
// native stamps ticks only carries Seq 0. The zero Watermark asks for a
// keyframe.
type Watermark struct {
	Tick int64
	Seq  uint64
}

// At is the watermark of a tick-only read.
func At(tick int64) Watermark { return Watermark{Tick: tick} }

// Zero reports the keyframe ask.
func (w Watermark) Zero() bool { return w == Watermark{} }

// Before orders watermarks by tick, then seq.
func (w Watermark) Before(o Watermark) bool {
	return w.Tick < o.Tick || w.Tick == o.Tick && w.Seq < o.Seq
}

// Watermarks is the per-section completeness a reader plans against. A
// View implements it, and View.CompleteThrough folds it to the one point
// every section is complete through.
type Watermarks interface {
	Watermark(section string) (Watermark, bool)
}

// Read is one section read: a keyframe (Delta false) whose Rows are the
// whole section, or a delta whose Rows are the rows changed at or after
// the watermark asked, Removed the rows the native dropped since, and
// Unchanged the count of the rest when Counted.
type Read[K comparable, R any] struct {
	AsOf      Watermark
	Delta     bool
	Rows      map[K]R
	Removed   []K
	Unchanged uint64
	Counted   bool
}

// Windowed is a Section whose native keeps tombstones for a bounded
// window: a watermark older than Window ticks is read as a keyframe
// rather than asked as a delta the native would only refuse.
type Windowed interface {
	Window() int64
}

// Section is one mirrored section's change source. Read answers since 0
// with a keyframe and a positive since with a delta (or a keyframe, when
// the native keeps no tracking for it); ErrExpired asks for a keyframe.
// Equal compares two rows' facts for the resync drift count. A section
// joins the mirror by implementing this (#773: pawns, gear, acquisition,
// upkeep).
type Section[K comparable, R any] interface {
	Name() string
	Read(ctx context.Context, since Watermark) (Read[K, R], error)
	Equal(a, b R) bool
}

// Table is one section as the mirror holds it: Rows complete through
// AsOf, published at mirror Version. Never mutated once published.
type Table[K comparable, R any] struct {
	Rows    map[K]R
	AsOf    Watermark
	Version uint64
}

// Mirror holds the sections of one scope. One writer (the scheduler step)
// refreshes; any goroutine may take a View or request a resync.
type Mirror struct {
	mu        sync.RWMutex
	scope     Scope
	version   uint64
	tables    map[string]any
	refreshes map[string]int
	resync    map[string]bool
	recorder  Recorder
}

func New() *Mirror {
	return &Mirror{tables: map[string]any{}, refreshes: map[string]int{}, resync: map[string]bool{}}
}

// Rescope empties the mirror when scope differs from the one its rows
// belong to; it reports whether it did.
func (m *Mirror) Rescope(scope Scope) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.rescope(scope)
}

func (m *Mirror) rescope(scope Scope) bool {
	if scope == m.scope {
		return false
	}
	m.scope = scope
	m.tables = map[string]any{}
	m.refreshes = map[string]int{}
	m.version++
	return true
}

// RequestResync makes the next refresh of the named section a keyframe
// compared against its delta (an apply refused on a stale token).
func (m *Mirror) RequestResync(name string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.resync[name] = true
}

// Get is the section's table under scope, false when none is held.
func Get[K comparable, R any](m *Mirror, scope Scope, name string) (Table[K, R], bool) {
	m.mu.RLock()
	defer m.mu.RUnlock()
	if m.scope != scope {
		return Table[K, R]{}, false
	}
	t, ok := m.tables[name].(Table[K, R])
	return t, ok
}

// Put publishes a keyframe the caller read another way (a bundle that
// carried the section, a published view).
func Put[K comparable, R any](m *Mirror, scope Scope, name string, rows map[K]R, asOf Watermark) Table[K, R] {
	m.Rescope(scope)
	return commit(m, scope, name, rows, asOf, false)
}

// Usable reports whether a table of section s held at asOf can still be
// refreshed at tick by a delta: always, unless s keeps tombstones for a
// bounded Window the watermark has fallen behind.
func Usable(s any, asOf Watermark, tick int64) bool {
	w, ok := s.(Windowed)
	return !ok || tick-asOf.Tick <= w.Window()
}

// View is every section at one mirror version.
type View struct {
	Scope   Scope
	Version uint64
	tables  map[string]any
}

// View takes a consistent snapshot of the mirror.
func (m *Mirror) View() View {
	m.mu.RLock()
	defer m.mu.RUnlock()
	tables := make(map[string]any, len(m.tables))
	for name, t := range m.tables {
		tables[name] = t
	}
	return View{Scope: m.scope, Version: m.version, tables: tables}
}

// Of is the named section in the view.
func Of[K comparable, R any](v View, name string) (Table[K, R], bool) {
	t, ok := v.tables[name].(Table[K, R])
	return t, ok
}

// Watermark is the named section's watermark in the view.
func (v View) Watermark(section string) (Watermark, bool) {
	t, ok := v.tables[section].(interface{ watermark() Watermark })
	if !ok {
		return Watermark{}, false
	}
	return t.watermark(), true
}

func (t Table[K, R]) watermark() Watermark { return t.AsOf }

// CompleteThrough is the point every section in the view is complete
// through: the least of their watermarks, false when the view holds
// none. It is the shared complete_through_tick planners decide at (#795
// step 3).
func (v View) CompleteThrough() (Watermark, bool) {
	var out Watermark
	found := false
	for name := range v.tables {
		mark, ok := v.Watermark(name)
		if ok && (!found || mark.Before(out)) {
			out, found = mark, true
		}
	}
	return out, found
}

// Sections names the sections the view holds.
func (v View) Sections() []string {
	out := make([]string, 0, len(v.tables))
	for name := range v.tables {
		out = append(out, name)
	}
	return out
}

// Kind is how a refresh filled its section.
type Kind string

const (
	Keyframe Kind = "keyframe" // a full read
	Delta    Kind = "delta"    // a changed-since read merged over the held rows
	Resync   Kind = "resync"   // a delta, then the backstop keyframe replacing it
)

// Outcome reports one refresh.
type Outcome struct {
	Kind      Kind
	Since     Watermark
	Changed   int
	Removed   int
	Unchanged uint64
	// Checked is set when a resync compared the delta's rows against a
	// keyframe of the same tick; Drift counts the rows they disagree on
	// (non-zero is a bug against the native tracker). Requested is
	// whether the resync was asked for rather than due by cadence.
	Checked   bool
	Drift     int
	Requested bool
	// Expired is a delta the native refused, answered by a keyframe.
	Expired bool
}

// Refresh brings one section current at tick. Nothing held, or a table
// whose watermark the tombstone window has outrun, reads a keyframe; a
// held table is refreshed by a delta since its watermark (a keyframe when
// the native refuses it as expired), merged by key with tombstones
// dropped. Every ResyncEvery-th delta, the first after RequestResync, and
// a delta whose counts do not cover the held rows also read a keyframe,
// count the drift and keep the keyframe. A failed read leaves the held
// table and returns it with the error. Whether a section needs refreshing
// at all (it may be complete through the step's tick) is the caller's.
func Refresh[K comparable, R any](ctx context.Context, m *Mirror, scope Scope, tick int64, s Section[K, R]) (Table[K, R], Outcome, error) {
	name := s.Name()
	m.mu.Lock()
	m.rescope(scope)
	held, ok := m.tables[name].(Table[K, R])
	requested := m.resync[name]
	m.mu.Unlock()
	keyframe := func(out Outcome) (Table[K, R], Outcome, error) {
		read, err := s.Read(ctx, Watermark{})
		if err != nil {
			return held, out, err
		}
		out.Kind, out.Changed = Keyframe, len(read.Rows)
		return commit(m, scope, name, read.Rows, read.AsOf, requested), out, nil
	}
	if !ok || !Usable(s, held.AsOf, tick) {
		return keyframe(Outcome{})
	}
	out := Outcome{Since: held.AsOf, Requested: requested}
	read, err := s.Read(ctx, held.AsOf)
	if errors.Is(err, ErrExpired) {
		out.Expired = true
		return keyframe(out)
	}
	if err != nil {
		return held, out, err
	}
	if !read.Delta {
		out.Kind, out.Changed = Keyframe, len(read.Rows)
		return commit(m, scope, name, read.Rows, read.AsOf, requested), out, nil
	}
	rows := Merge(held.Rows, read)
	out.Kind, out.Changed, out.Removed, out.Unchanged = Delta, len(read.Rows), len(read.Removed), read.Unchanged
	m.mu.Lock()
	n := m.refreshes[name]
	m.refreshes[name] = n + 1
	m.mu.Unlock()
	covered := !read.Counted || uint64(len(rows)) == uint64(len(read.Rows))+read.Unchanged
	if requested || !covered || n%ResyncEvery == ResyncEvery-1 {
		full, err := s.Read(ctx, Watermark{})
		if err != nil {
			// The delta stands; the resync is asked again next refresh.
			m.RequestResync(name)
			return commit(m, scope, name, rows, read.AsOf, false), out, err
		}
		if full.AsOf == read.AsOf {
			out.Checked, out.Drift = true, Drift(rows, full.Rows, s.Equal)
		}
		out.Kind = Resync
		return commit(m, scope, name, full.Rows, full.AsOf, requested), out, nil
	}
	return commit(m, scope, name, rows, read.AsOf, requested), out, nil
}

// commit publishes rows as the section's table unless the scope moved
// under the read, and clears a resync request it answered.
func commit[K comparable, R any](m *Mirror, scope Scope, name string, rows map[K]R, asOf Watermark, answered bool) Table[K, R] {
	m.mu.Lock()
	defer m.mu.Unlock()
	if rows == nil {
		rows = map[K]R{}
	}
	m.version++
	t := Table[K, R]{Rows: rows, AsOf: asOf, Version: m.version}
	if m.scope == scope {
		m.tables[name] = t
		if m.recorder != nil {
			m.recorder(Published{Scope: scope, Section: name, Version: t.Version, AsOf: asOf, Rows: rows})
		}
		if answered {
			delete(m.resync, name)
		}
	}
	return t
}

// Merge lays a read over held rows without touching them: a keyframe
// replaces them; a delta upserts its rows and drops its tombstones.
func Merge[K comparable, R any](held map[K]R, read Read[K, R]) map[K]R {
	if !read.Delta {
		return read.Rows
	}
	out := make(map[K]R, len(held)+len(read.Rows))
	for k, row := range held {
		out[k] = row
	}
	for _, k := range read.Removed {
		delete(out, k)
	}
	for k, row := range read.Rows {
		out[k] = row
	}
	return out
}

// Drift counts the keys on which two tables disagree: a row in one and
// not the other, or a row whose facts differ under equal.
func Drift[K comparable, R any](a, b map[K]R, equal func(R, R) bool) int {
	drift := 0
	for k, row := range b {
		if have, ok := a[k]; !ok || !equal(have, row) {
			drift++
		}
	}
	for k := range a {
		if _, ok := b[k]; !ok {
			drift++
		}
	}
	return drift
}
