package snapshot

import (
	"bufio"
	"bytes"
	"encoding/json"
	"io"
	"os"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Sync points (stream.go KeyEvery). A review keyframe after the stream's
// first is preceded by a keyframe of every mirror section the writer
// holds, so replay from the first of those section lines needs nothing
// earlier in the stream.

// keySections writes every held section again as a keyframe, by name. A
// failed write drops the section, as recordSection does. streamsMu is held.
func (rec *streamWriter) keySections() {
	names := make([]string, 0, len(rec.sections))
	for name := range rec.sections {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		s := rec.sections[name]
		frame := sectionFrame{Name: name, Version: s.version, AsOf: s.asOf, Scope: s.scope, Key: true}
		if s.grid != nil {
			if data, next, ok := encodeGrid(&heldGrid{grid: s.grid.grid}, true, nil, nil, s.rows); ok {
				frame.Grid, s.grid = data, next
			} else {
				s.grid = nil
			}
		}
		for _, k := range sortedKeys(s.rows) {
			if frame.Grid == nil {
				frame.Upserts = append(frame.Upserts, [2]json.RawMessage{s.keys[k], s.rows[k]})
			}
		}
		frame.stamp()
		if err := rec.append(streamLine{Tick: domain.Tick(s.asOf.Tick), Section: &frame}); err != nil {
			delete(rec.sections, name)
		}
	}
}

// openStreamFile opens a stream for seeking: a plain file as is, a gzipped
// one decompressed into memory.
func openStreamFile(path string) (io.ReadSeeker, func(), error) {
	if strings.HasSuffix(path, ".gz") {
		data, err := readFile(path)
		if err != nil {
			return nil, nil, err
		}
		return bytes.NewReader(data), func() {}, nil
	}
	f, err := os.Open(path)
	if err != nil {
		return nil, nil, err
	}
	return f, func() { f.Close() }, nil
}

// flag decodes any JSON value as present, without keeping it.
type flag bool

func (f *flag) UnmarshalJSON([]byte) error { *f = true; return nil }

// syncBefore is the byte offset of the last sync point whose review
// keyframe at (tick, seq) before accepts, 0 (the stream's start) when
// none does. It reads only each line's header.
func syncBefore(path string, before func(domain.Tick, int) bool) (int64, error) {
	in, done, err := openStreamFile(path)
	if err != nil {
		return 0, err
	}
	defer done()
	lines := bufio.NewReader(in)
	var at, sync int64
	run := int64(-1)
	seen, inRun := map[string]bool{}, map[string]bool{}
	for {
		raw, rerr := lines.ReadBytes('\n')
		off := at
		at += int64(len(raw))
		var head struct {
			Tick    domain.Tick
			Seq     int
			Key     flag
			Section *struct {
				Name string
				Key  bool
			}
		}
		if len(bytes.TrimSpace(raw)) > 0 && json.Unmarshal(raw, &head) == nil {
			switch s := head.Section; {
			case s != nil && s.Key:
				if run < 0 {
					run, inRun = off, map[string]bool{}
				}
				seen[s.Name], inRun[s.Name] = true, true
			case s != nil:
				seen[s.Name], run = true, -1
			default:
				if bool(head.Key) && run >= 0 && len(inRun) == len(seen) && before(head.Tick, head.Seq) {
					sync = run
				}
				run = -1
			}
		}
		if rerr == io.EOF {
			return sync, nil
		}
		if rerr != nil {
			return 0, rerr
		}
	}
}

// seek runs load from the last sync point before accepts, then from the
// stream's start when that finds nothing or fails: found says whether it
// did.
func seek(path string, before func(domain.Tick, int) bool, load func(from int64) (found bool, err error)) error {
	if from, err := syncBefore(path, before); err == nil && from > 0 {
		if found, err := load(from); err == nil && found {
			return nil
		}
	}
	_, err := load(0)
	return err
}

// reviewBefore accepts a sync point at or before review (tick, seq); seq 0
// is the last review at tick.
func reviewBefore(tick domain.Tick, seq int) func(domain.Tick, int) bool {
	return func(t domain.Tick, s int) bool { return t < tick || t == tick && (seq == 0 || s <= seq) }
}
