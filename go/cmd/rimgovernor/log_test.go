package main

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestLogCommand(t *testing.T) {
	profile := t.TempDir()
	path := filepath.Join(profile, "flight", "flight.jsonl")
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	var rows strings.Builder
	for i := 1; i <= 3; i++ {
		rows.WriteString(`{"version":2,"run":"r","sequence":` + string(rune('0'+i)) + `,"wall_time":1,"kind":"planner_step","context":{"level":"INFO","tick":` + string(rune('0'+i)) + `,"component":"worker"},"payload":{"verdict":"waiting","reason":"no_work","target":"stock","dur_ms":0,"attrs":{}}}` + "\n")
	}
	if err := os.WriteFile(path, []byte(rows.String()), 0o644); err != nil {
		t.Fatal(err)
	}
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
