package main

import (
	"bytes"
	"encoding/json"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// writeProfileLogs writes three repeated worker decisions into the log ring
// of a profile and returns the profile and the ring path.
func writeProfileLogs(t *testing.T) (profile, ring string) {
	t.Helper()
	profile = t.TempDir()
	var rows []telemetrytest.Row
	for tick := 1; tick <= 3; tick++ {
		rows = append(rows, telemetrytest.Row{Kind: "planner_step", Context: map[string]any{"level": "INFO", "tick": tick, "component": "worker"},
			Payload: map[string]any{"verdict": "waiting", "reason": "no_work", "target": "stock", "dur_ms": 0, "attrs": map[string]any{}}})
	}
	return profile, telemetrytest.WriteLogs(t, filepath.Join(profile, "otlp"), rows...)
}

func TestLogCommand(t *testing.T) {
	profile, path := writeProfileLogs(t)
	var out, errs bytes.Buffer
	if code := run([]string{"log", "--profile", profile}, &out, &errs); code != 0 || !strings.Contains(out.String(), "x 3, first/last tick 1/3") || strings.Count(out.String(), "\n") != 1 {
		t.Fatalf("exit=%d out=%q err=%q", code, &out, &errs)
	}
	out.Reset()
	if code := run([]string{"log", path, "--json", "--tick", "2..3"}, &out, &errs); code != 0 || !strings.Contains(out.String(), `"count":2`) {
		t.Fatalf("json exit=%d out=%q err=%q", code, &out, &errs)
	}
	out.Reset()
	if code := run([]string{"log", path, "--level", "ERROR"}, &out, &errs); code != 0 || out.Len() != 0 {
		t.Fatalf("level exit=%d out=%q", code, &out)
	}
}

func TestLogCommandUsageAndReadErrors(t *testing.T) {
	for _, args := range [][]string{{"log"}, {"log", "relative.jsonl"}, {"log", "--profile"}, {"log", "--level", "loud", "--profile", "x"}, {"log", "--tick", "9..1", "--profile", "x"}, {"log", "--bogus"}, {"log", "--profile", "x", "/abs.jsonl"}} {
		var out, errs bytes.Buffer
		if code := run(args, &out, &errs); code != 2 || !strings.Contains(errs.String(), "usage: rimgovernor log") {
			t.Errorf("%q: exit=%d err=%q", args, code, &errs)
		}
	}
}

func TestHelpListsLog(t *testing.T) {
	var out, errs bytes.Buffer
	if run([]string{"help"}, &out, &errs) != 0 || !strings.Contains(out.String(), "rimgovernor log ") {
		t.Fatalf("help: %q", &out)
	}
}

// trace without an id lists the ring's traces (none here: the rows ran outside
// any span), with an unknown id it fails, and its usage errors exit 2.
func TestTraceAndPhasesCommands(t *testing.T) {
	profile, path := writeProfileLogs(t)
	var out, errs bytes.Buffer
	if code := run([]string{"trace", "--json", "--profile", profile}, &out, &errs); code != 0 {
		t.Fatalf("trace index exit=%d err=%q", code, &errs)
	}
	var index []map[string]any
	if err := json.Unmarshal(out.Bytes(), &index); err != nil || len(index) != 0 {
		t.Fatalf("trace index = %q (%v)", &out, err)
	}
	out.Reset()
	if code := run([]string{"trace", path, "nosuchtrace"}, &out, &errs); code != 1 || !strings.Contains(errs.String(), "nosuchtrace") {
		t.Fatalf("unknown trace exit=%d err=%q", code, &errs)
	}
	out.Reset()
	if code := run([]string{"phases", "--json", "--profile", profile}, &out, &errs); code != 0 || !json.Valid(out.Bytes()) {
		t.Fatalf("phases exit=%d out=%q err=%q", code, &out, &errs)
	}
	for _, args := range [][]string{{"trace"}, {"trace", "relative.jsonl"}, {"trace", "--bogus"}, {"phases"}, {"phases", "rel.jsonl"}} {
		out.Reset()
		errs.Reset()
		if code := run(args, &out, &errs); code != 2 || !strings.Contains(errs.String(), "usage: rimgovernor") {
			t.Errorf("%q: exit=%d err=%q", args, code, &errs)
		}
	}
}
