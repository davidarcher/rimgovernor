package facts

// Keyed tables: per section, the keyed rows of the last whole frame
// the native answered and the tick they describe, held in the
// Store beside the decoded sections and emptied with them on a scope
// change. Tables are immutable once published, and every table goes to
// the store's Recorder, which turns successive tables into a recording.

// Watermark is the tick a table's rows describe.
type Watermark struct {
	Tick int64
}

// At is the watermark of a read at tick.
func At(tick int64) Watermark { return Watermark{Tick: tick} }

// Table is one keyed section as the store holds it: Rows as of AsOf,
// published at table Version. Never mutated once published.
type Table[K comparable, R any] struct {
	Rows    map[K]R
	AsOf    Watermark
	Version uint64
}

// GetTable is the section's table under scope, false when none is held.
func GetTable[K comparable, R any](s *Store, scope Scope, name string) (Table[K, R], bool) {
	if s == nil {
		return Table[K, R]{}, false
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	if s.scope != scope {
		return Table[K, R]{}, false
	}
	t, ok := s.tables[name].(Table[K, R])
	return t, ok
}

// PutTable publishes rows, a whole frame's section, as the section's
// table under scope, emptying the store first when scope differs from the
// one its sections belong to.
func PutTable[K comparable, R any](s *Store, scope Scope, name string, rows map[K]R, asOf Watermark) Table[K, R] {
	if rows == nil {
		rows = map[K]R{}
	}
	if s == nil {
		return Table[K, R]{Rows: rows, AsOf: asOf}
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.rescope(scope)
	s.tableVersion++
	t := Table[K, R]{Rows: rows, AsOf: asOf, Version: s.tableVersion}
	s.tables[name] = t
	if s.recorder != nil {
		s.recorder(Published{Scope: scope, Section: name, Version: t.Version, AsOf: asOf, Rows: rows})
	}
	return t
}

// KeyedRows is a section's rows as a persistent table version:
// the recorder reads what changed since the previous version without
// walking the rows both versions share. bridge.Table implements it.
type KeyedRows interface {
	// Rows calls row for every row of the table: its id and the row.
	Rows(row func(id string, row any))
	// ChangedSince calls changed for each row new or different since prev
	// (another table of the same type, or nil) and removed for each
	// id prev held that the table lacks.
	ChangedSince(prev any, changed func(id string, row any), removed func(id string))
}

// KeyedTable is a section published as a persistent table version.
type KeyedTable struct {
	Rows    KeyedRows
	AsOf    Watermark
	Version uint64
}

// PutKeyed publishes a persistent table version as the section's table
// under scope (PutTable's counterpart for the big keyed sections): the
// store keeps the version itself, with no per-publish copy, and hands it
// to the Recorder, which diffs it against the last it saw.
func PutKeyed(s *Store, scope Scope, name string, rows KeyedRows, asOf Watermark) uint64 {
	if s == nil {
		return 0
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.rescope(scope)
	s.tableVersion++
	t := KeyedTable{Rows: rows, AsOf: asOf, Version: s.tableVersion}
	s.tables[name] = t
	if s.recorder != nil {
		s.recorder(Published{Scope: scope, Section: name, Version: t.Version, AsOf: asOf, Rows: rows})
	}
	return t.Version
}

// GetKeyed is the section's persistent table under scope, false when none
// is held.
func GetKeyed(s *Store, scope Scope, name string) (KeyedTable, bool) {
	if s == nil {
		return KeyedTable{}, false
	}
	s.mu.RLock()
	defer s.mu.RUnlock()
	if s.scope != scope {
		return KeyedTable{}, false
	}
	t, ok := s.tables[name].(KeyedTable)
	return t, ok
}

// Published is one table as the store published it, handed to a
// Recorder (#795 step 4): the section's whole rows (a map[K]R) at Version,
// complete through AsOf, under Scope. Rows is never mutated after
// publishing, so a recorder may keep it.
type Published struct {
	Scope   Scope
	Section string
	Version uint64
	AsOf    Watermark
	Rows    any
}

// A Recorder sees every table the store publishes, in publish order,
// under the store's lock: a recording (snapshot.MirrorRecorder) turns them
// into keyframes and the rows that changed between tables. It must not
// call back into the store.
type Recorder func(Published)

// SetRecorder makes r see every table published from now on (nil stops).
func (s *Store) SetRecorder(r Recorder) {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.recorder = r
}
