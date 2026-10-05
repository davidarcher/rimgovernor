package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func row(seq int, run string, wall float64, kind, context, payload string) string {
	return fmt.Sprintf(`{"version":1,"run":%q,"sequence":%d,"wall_time":%g,"kind":%q,"context":%s,"payload":%s}`+"\n", run, seq, wall, kind, context, payload)
}
func writeFile(t *testing.T, path, text string, appendTo bool) {
	t.Helper()
	flag := os.O_CREATE | os.O_WRONLY | os.O_TRUNC
	if appendTo {
		flag = os.O_CREATE | os.O_WRONLY | os.O_APPEND
	}
	f, err := os.OpenFile(path, flag, 0o644)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	if _, err := f.WriteString(text); err != nil {
		t.Fatal(err)
	}
}

// fixture: an older run, then the newest run's rotated segment (1..3) and
// active file (5.., sequence 4 lost: a recording_gap).
func fixture(t *testing.T) (path string, tail *recorderTail) {
	dir := t.TempDir()
	path = filepath.Join(dir, "flight", "flight.jsonl")
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	writeFile(t, path+".1",
		row(1, "runA", 10, "native_call", `{}`, `{"tool":"games_call_tool","native_tool":"x/read","error":"boom"}`)+
			row(2, "runA", 11, "clock_step", `{"tick":100}`, `{"verdict":"admitted","reason":"","target":"timer","dur_ms":7,"attrs":{"reads":4}}`)+
			row(3, "runA", 12, "native_request", `{}`, `{"native_tool":"x/read"}`), false)
	writeFile(t, path,
		row(5, "runA", 14, "authority", `{"tick":120}`, `{"msg":"authority changed","change":"changed","generation":9}`)+
			row(6, "runA", 15, "goal", `{"tick":130,"level":"WARN","trace_id":"abc123"}`, `{"msg":"goal refused","goal":"build-hut"}`)+
			row(7, "runA", 16, "clock_step", `{"tick":140}`, `{"verdict":"admitted","reason":"","target":"timer","dur_ms":9,"attrs":{"reads":2}}`), false)
	return path, newRecorderTail(path)
}

func TestProblemsFeedOrderingGapAndKinds(t *testing.T) {
	_, tail := fixture(t)
	v := tail.view([]string{"native_request"}, "")
	if !v.Available || v.Rows != 7 { // 6 rows + the gap
		t.Fatalf("available=%v rows=%d empty=%q", v.Available, v.Rows, v.Empty)
	}
	var kinds []string
	for _, e := range v.Events {
		kinds = append(kinds, e.Kind)
	}
	want := "clock_step goal authority recording_gap clock_step native_call"
	if strings.Join(kinds, " ") != want { // newest first, slow marker hidden

		t.Fatalf("feed %v, want %s", kinds, want)
	}
	for _, e := range v.Events {
		if e.Kind == "recording_gap" && !e.Problem {
			t.Fatal("gap is a problem")
		}
		if e.Kind == "clock_step" && e.Problem {
			t.Fatal("clock_step is not a problem")
		}
	}
	all := tail.view(nil, "")
	if len(all.Events) != len(v.Events)+1 {
		t.Fatalf("showing decode rows: %d vs %d", len(all.Events), len(v.Events))
	}
	if got := len(v.Kinds); got != 6 {
		t.Fatalf("kinds %+v", v.Kinds)
	}
	problems := tail.allProblems()
	if !strings.Contains(problems, "boom") || !strings.Contains(problems, "goal refused") || strings.Contains(problems, "authority changed") {
		t.Fatalf("problems:\n%s", problems)
	}
}

func TestProblemsSubstringFilter(t *testing.T) {
	_, tail := fixture(t)
	for needle, want := range map[string]int{"build-hut": 1, "ABC123": 1, "boom": 1, "nothing-here": 0, "clock_step": 2} {
		if got := len(tail.view(nil, needle).Events); got != want {
			t.Errorf("filter %q: %d rows, want %d", needle, got, want)
		}
	}
}

func TestProblemsHealthFromNewestRun(t *testing.T) {
	path, tail := fixture(t)
	writeFile(t, path, row(8, "runB", 20, "clock_step", `{"tick":500}`, `{"verdict":"admitted","reason":"","target":"timer","dur_ms":4,"attrs":{"reads":3}}`), true)
	h := tail.view(nil, "").Health
	if !h.Known || h.Run != "runB" || !h.HasTick || h.Tick != 500 || h.LastStepMs != 4 || h.NativeErrors != 0 || h.ReadsPerStep != 3 {
		t.Fatalf("%+v", h)
	}
	if h.Authority != "" {
		t.Fatalf("authority from another run: %q", h.Authority)
	}
	// runA's strip carries its native error and authority.
	_, other := fixture(t)
	h = other.view(nil, "").Health
	if h.Run != "runA" || h.NativeErrors != 1 || h.LastStepMs != 9 || h.Tick != 140 || h.Authority != "native generation 9" || h.ReadsPerStep != 3 {
		t.Fatalf("%+v", h)
	}
}

func TestProblemsIncrementalReadAndEmptyStates(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "flight", "flight.jsonl")
	tail := newRecorderTail(path)
	if v := tail.view(nil, ""); v.Available || !strings.Contains(v.Empty, "No flight recorder yet") {
		t.Fatalf("%+v", v)
	}
	os.MkdirAll(filepath.Dir(path), 0o755)
	writeFile(t, path, "", false)
	if v := tail.view(nil, ""); v.Available || !strings.Contains(v.Empty, "empty") {
		t.Fatalf("%+v", v)
	}
	writeFile(t, path, row(1, "r", 10, "goal", `{}`, `{"msg":"one"}`), false)
	if v := tail.view(nil, ""); len(v.Events) != 1 {
		t.Fatalf("%+v", v)
	}
	writeFile(t, path, row(2, "r", 11, "goal", `{}`, `{"msg":"two"}`), true)
	v := tail.view(nil, "")
	if len(v.Events) != 2 || v.Events[0].Seq != 2 {
		t.Fatalf("%+v", v.Events)
	}
}
