package mirror

// Published is one table as the mirror published it, handed to a
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

// A Recorder sees every table the mirror publishes, in publish order,
// under the mirror's lock: a recording (snapshot.MirrorRecorder) turns them
// into keyframes and the rows that changed between tables. It must not call back into the
// mirror.
type Recorder func(Published)

// SetRecorder makes r see every table published from now on (nil stops).
func (m *Mirror) SetRecorder(r Recorder) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.recorder = r
}
