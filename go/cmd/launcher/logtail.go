package main

import (
	"encoding/json"
	"fmt"
	"sort"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/logview"
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

// infoLogKinds are the INFO kinds the Log panel shows beside every WARN and
// ERROR row and recording gap: the events a player reads (combat, the colony
// changing stage, the clock's authority changing). Every other INFO row stays
// in the Problems feed and `rimgovernor log`.
var infoLogKinds = map[string]bool{
	"combat_summary": true,
	"defense_action": true,
	"colony_stage":   true, "build_tier": true,
	"authority": true,
}

// logWorthy is the Log panel's level-and-kind rule: WARN and ERROR always,
// INFO only for the explicit kinds above.
func logWorthy(rec bridge.TimelineRecord) bool {
	if rec.Kind == "recording_gap" {
		return true
	}
	switch logview.Level(rec) {
	case "WARN", "ERROR":
		return true
	}
	return infoLogKinds[rec.Kind]
}

// logTail is the Log panel: the newest run's flight rows read through the
// recorder tail and collapsed by logview, recomputed only when the
// recording grew.
type logTail struct {
	mu       sync.Mutex
	recorder *recorderTail
	key      string
	cached   []LogRow
}

// rows is the collapsed entries of the newest run, the one seen most
// recently first; empty without a recording.
func (t *logTail) rows() []LogRow {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.recorder == nil {
		return []LogRow{}
	}
	records, err := t.recorder.read()
	if err != nil || len(records) == 0 {
		t.key, t.cached = "", nil
		return []LogRow{}
	}
	last := records[len(records)-1]
	key := fmt.Sprintf("%d/%d/%g", len(records), last.Sequence, last.WallTime)
	if t.key == key && t.cached != nil {
		return t.cached
	}
	kept := logview.Filter{SinceRun: true}.Apply(records)
	var worthy []bridge.TimelineRecord
	for _, rec := range kept {
		if logWorthy(rec) {
			worthy = append(worthy, rec)
		}
	}
	entries := logview.Collapse(worthy)
	sort.SliceStable(entries, func(i, j int) bool { return entries[i].LastAt > entries[j].LastAt })
	out := make([]LogRow, 0, len(entries))
	for _, e := range entries {
		out = append(out, logRow(e))
	}
	t.key, t.cached = key, out
	return out
}

func logRow(e logview.Entry) LogRow {
	row := LogRow{Level: e.Level, Component: e.Component, Message: e.Words(), Count: e.Count,
		FirstTime: e.FirstAt, LastTime: e.LastAt, Problem: e.Level == "WARN" || e.Level == "ERROR"}
	if e.FirstTick != nil {
		row.FirstTick = *e.FirstTick
	}
	if e.LastTick != nil {
		row.LastTick = *e.LastTick
	}
	if len(e.Payload) > 0 {
		if p, err := json.Marshal(e.Payload); err == nil {
			row.Detail = string(p)
		}
	}
	row.Text = e.Line()
	if row.Detail != "" {
		row.Text += "\n" + row.Detail
	}
	return row
}
