package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestPruneControllerLogsKeepsTheNewestStarts(t *testing.T) {
	dir := t.TempDir()
	touch := func(name string) {
		if err := os.WriteFile(filepath.Join(dir, name), nil, 0o644); err != nil {
			t.Fatal(err)
		}
	}
	for _, stamp := range []string{"20261001-100000", "20261002-100000", "20261003-100000", "20261004-100000"} {
		touch("controller-" + stamp + ".out.log")
		touch("controller-" + stamp + ".err.log")
	}
	touch("state.sqlite")
	pruneControllerLogs(dir, 2)
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	var got []string
	for _, e := range entries {
		got = append(got, e.Name())
	}
	want := []string{"controller-20261003-100000.err.log", "controller-20261003-100000.out.log", "controller-20261004-100000.err.log", "controller-20261004-100000.out.log", "state.sqlite"}
	if len(got) != len(want) {
		t.Fatalf("kept %v", got)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("kept %v", got)
		}
	}
}
