package main

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestPruneKeepsNewestRunsAndSkipsNonRuns(t *testing.T) {
	dir := t.TempDir()
	write := func(rel, body string, at time.Time) {
		path := filepath.Join(dir, rel)
		if err := os.MkdirAll(filepath.Dir(path), 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(body), 0644); err != nil {
			t.Fatal(err)
		}
		if err := os.Chtimes(path, at, at); err != nil {
			t.Fatal(err)
		}
	}
	base := time.Now().Add(-time.Hour)
	// A single-case run, two suites (one still being written: its newest
	// file is recent though its result.json is old), a root passed by
	// mistake (no result.json within reach) and a loose series file.
	write("old-run/result.json", "{}", base)
	write("old-run.log", "log", base)
	write("old-run.err", "", base)
	write("mid-suite/area/case/result.json", "{}", base.Add(10*time.Minute))
	write("mid-suite/area/case/0001-call.json", "{}", base.Add(10*time.Minute))
	write("live-suite/area/case/result.json", "{}", base.Add(-time.Hour))
	write("live-suite/area/other/0001-call.json", "{}", base.Add(50*time.Minute))
	write("bridge/profile/deep/nest/result.json", "{}", base.Add(55*time.Minute))
	write("metrics.jsonl", "{}\n", base)

	var stdout, stderr bytes.Buffer
	if code := prune([]string{"-output", dir, "-keep", "2", "-dry-run"}, &stdout, &stderr); code != 0 {
		t.Fatalf("dry run exit %d: %s", code, stderr.String())
	}
	if !strings.Contains(stdout.String(), "would delete old-run") || !strings.Contains(stdout.String(), "skip bridge") {
		t.Fatalf("dry run output:\n%s", stdout.String())
	}
	if _, err := os.Stat(filepath.Join(dir, "old-run.log")); err != nil {
		t.Fatalf("dry run deleted: %v", err)
	}

	stdout.Reset()
	if code := prune([]string{"-output", dir, "-keep", "2"}, &stdout, &stderr); code != 0 {
		t.Fatalf("exit %d: %s", code, stderr.String())
	}
	for _, gone := range []string{"old-run", "old-run.log", "old-run.err"} {
		if _, err := os.Stat(filepath.Join(dir, gone)); !os.IsNotExist(err) {
			t.Errorf("%s survived: %v", gone, err)
		}
	}
	for _, kept := range []string{"mid-suite", "live-suite", "bridge", "metrics.jsonl"} {
		if _, err := os.Stat(filepath.Join(dir, kept)); err != nil {
			t.Errorf("%s removed: %v", kept, err)
		}
	}
	if !strings.Contains(stdout.String(), "2 run outputs kept, 1 removed") {
		t.Fatalf("summary:\n%s", stdout.String())
	}
}

func TestPruneRefusesRelativeOutput(t *testing.T) {
	var stdout, stderr bytes.Buffer
	if code := prune([]string{"-output", "relative"}, &stdout, &stderr); code != 2 || !strings.Contains(stderr.String(), "absolute") {
		t.Fatalf("exit %d: %s", code, stderr.String())
	}
}
