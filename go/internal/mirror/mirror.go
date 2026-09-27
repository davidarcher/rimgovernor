// Package mirror is the controller's colony mirror (#795): per section, the
// keyed rows of the last whole frame the native answered (#858) and the
// tick they describe. Any change of the world the rows belong to (a
// reload, a map change or an authority generation flip) empties it.
// Tables are immutable once published, and every table goes to the
// mirror's Recorder, which turns successive tables into a recording.
package mirror

import "sync"

// Scope is the world a mirror's rows belong to; any change empties it.
type Scope struct {
	Load       string
	Map        int32
	Generation uint64
}

// Watermark is the tick a table's rows describe.
type Watermark struct {
	Tick int64
}

// At is the watermark of a read at tick.
func At(tick int64) Watermark { return Watermark{Tick: tick} }

// Table is one section as the mirror holds it: Rows as of AsOf, published
// at mirror Version. Never mutated once published.
type Table[K comparable, R any] struct {
	Rows    map[K]R
	AsOf    Watermark
	Version uint64
}

// Mirror holds the sections of one scope.
type Mirror struct {
	mu       sync.RWMutex
	scope    Scope
	version  uint64
	tables   map[string]any
	recorder Recorder
}

func New() *Mirror {
	return &Mirror{tables: map[string]any{}}
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

// Put publishes rows, a whole frame's section, as the section's table
// under scope, emptying the mirror first when scope differs from the one
// its rows belong to.
func Put[K comparable, R any](m *Mirror, scope Scope, name string, rows map[K]R, asOf Watermark) Table[K, R] {
	m.mu.Lock()
	defer m.mu.Unlock()
	if scope != m.scope {
		m.scope = scope
		m.tables = map[string]any{}
	}
	if rows == nil {
		rows = map[K]R{}
	}
	m.version++
	t := Table[K, R]{Rows: rows, AsOf: asOf, Version: m.version}
	m.tables[name] = t
	if m.recorder != nil {
		m.recorder(Published{Scope: scope, Section: name, Version: t.Version, AsOf: asOf, Rows: rows})
	}
	return t
}
