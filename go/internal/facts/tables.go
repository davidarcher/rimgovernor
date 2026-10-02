package facts

// Keyed tables (#795): per section, the keyed rows of the last whole frame
// the native answered (#858) and the tick they describe, held in the
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
