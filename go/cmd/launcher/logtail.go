package main

import (
	"io"
	"os"
	"path/filepath"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/logdigest"
)

// LogRow is one row of the Log panel.
type LogRow struct {
	Level     string
	Component string
	Message   string
	Count     int
	FirstTime string
	LastTime  string
	FirstTick int64
	LastTick  int64
	Detail    string
	Problem   bool
	Text      string // the whole row, as the Copy button puts it on the clipboard
}

// logTail follows the newest controller log in dir and keeps its digest,
// reading only what was appended since the last call.
type logTail struct {
	mu     sync.Mutex
	dir    string
	path   string
	offset int64
	rest   []byte // a line the file has not finished yet
	digest *logdigest.Digest
}

// rows is the digest of the newest controller log; empty without one.
func (t *logTail) rows() []LogRow {
	t.mu.Lock()
	defer t.mu.Unlock()
	logs, _ := filepath.Glob(filepath.Join(t.dir, "controller-*.err.log"))
	if len(logs) == 0 {
		return []LogRow{}
	}
	newest := logs[0] // names carry their start stamp, so the last sorts newest
	for _, l := range logs {
		if l > newest {
			newest = l
		}
	}
	f, err := os.Open(newest)
	if err != nil {
		return []LogRow{}
	}
	defer f.Close()
	st, err := f.Stat()
	if err != nil {
		return []LogRow{}
	}
	if newest != t.path || st.Size() < t.offset || t.digest == nil {
		t.path, t.offset, t.rest, t.digest = newest, 0, nil, logdigest.New()
	}
	if _, err := f.Seek(t.offset, io.SeekStart); err == nil {
		if data, err := io.ReadAll(f); err == nil {
			t.offset += int64(len(data))
			data = append(t.rest, data...)
			start := 0
			for i, b := range data {
				if b == '\n' {
					t.digest.Feed(string(data[start:i]))
					start = i + 1
				}
			}
			t.rest = append([]byte(nil), data[start:]...)
		}
	}
	out := []LogRow{}
	for _, r := range t.digest.Rows() {
		out = append(out, LogRow{Level: r.Level, Component: r.Component, Message: r.Message, Count: r.Count,
			FirstTime: r.FirstTime, LastTime: r.LastTime, FirstTick: r.FirstTick, LastTick: r.LastTick,
			Detail: r.Detail, Problem: r.Problem, Text: r.Text()})
	}
	return out
}
