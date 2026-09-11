package main

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestUnavailableRuntimeCannotBeStarted(t *testing.T) {
	for _, args := range [][]string{{"start"}, {"automate"}, {"--bridge", "local"}, {"version", "start"}} {
		var out, errors bytes.Buffer
		if got := run(args, &out, &errors); got != 2 || out.Len() != 0 || errors.Len() == 0 {
			t.Fatalf("%q: exit=%d stdout=%q stderr=%q", args, got, &out, &errors)
		}
	}
}

func TestHelpListsExplicitModesAndReplay(t *testing.T) {
	for _, args := range [][]string{nil, {"help"}, {"--help"}} {
		var out, errors bytes.Buffer
		if got := run(args, &out, &errors); got != 0 || errors.Len() != 0 ||
			!strings.Contains(out.String(), "replay <expected.json> <actual.json>") ||
			!strings.Contains(out.String(), "--building-control --profile PATH") ||
			!strings.Contains(out.String(), "Native writes require explicit building-control mode and player acquisition") {
			t.Fatalf("%q: exit=%d stdout=%q stderr=%q", args, got, &out, &errors)
		}
	}
}

func TestReplayFiles(t *testing.T) {
	const original = `{"actions":[{"id":"build-1","generation":7}],"receipt":{"uncertain":true}}`
	cases := []struct {
		name, expected, actual, diagnostic string
		want                               int
		samePath                           bool
	}{
		{name: "match", expected: original, actual: "\n " + original + "\n", want: 0},
		{name: "same file", expected: original, actual: original, samePath: true, want: 0},
		{name: "changed action", expected: original, actual: strings.Replace(original, "build-1", "build-2", 1), want: 1, diagnostic: "replay differs"},
		{name: "changed receipt", expected: original, actual: strings.Replace(original, "true", "false", 1), want: 1, diagnostic: "replay differs"},
		{name: "malformed expected", expected: "{", actual: original, want: 1, diagnostic: "expected"},
		{name: "malformed actual", expected: original, actual: "{", want: 1, diagnostic: "candidate"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			dir := t.TempDir()
			expectedPath := filepath.Join(dir, "expected.json")
			actualPath := filepath.Join(dir, "actual.json")
			for path, contents := range map[string]string{expectedPath: tc.expected, actualPath: tc.actual} {
				if err := os.WriteFile(path, []byte(contents), 0600); err != nil {
					t.Fatal(err)
				}
			}
			if tc.samePath {
				actualPath = expectedPath
			}
			var out, errors bytes.Buffer
			got := run([]string{"replay", expectedPath, actualPath}, &out, &errors)
			if got != tc.want {
				t.Fatalf("exit=%d want=%d stdout=%q stderr=%q", got, tc.want, &out, &errors)
			}
			if tc.want == 0 {
				if out.String() != "replay matches\n" || errors.Len() != 0 {
					t.Fatalf("stdout=%q stderr=%q", &out, &errors)
				}
			} else if out.Len() != 0 || !strings.Contains(errors.String(), tc.diagnostic) {
				t.Fatalf("stdout=%q stderr=%q", &out, &errors)
			}
			// CLI comparison must leave its input unchanged and release file handles.
			for path, contents := range map[string]string{expectedPath: tc.expected, filepath.Join(dir, "actual.json"): tc.actual} {
				data, err := os.ReadFile(path)
				if err != nil || string(data) != contents {
					t.Fatalf("input changed: %s: %q, %v", path, data, err)
				}
				if err := os.Remove(path); err != nil {
					t.Fatalf("input handle retained: %v", err)
				}
			}
		})
	}
}

func TestReplayMissingFile(t *testing.T) {
	dir := t.TempDir()
	valid := filepath.Join(dir, "valid.json")
	missing := filepath.Join(dir, "missing.json")
	if err := os.WriteFile(valid, []byte(`{}`), 0600); err != nil {
		t.Fatal(err)
	}
	for _, args := range [][]string{{"replay", missing, valid}, {"replay", valid, missing}} {
		var out, errors bytes.Buffer
		if got := run(args, &out, &errors); got != 1 || out.Len() != 0 || errors.Len() == 0 {
			t.Fatalf("%q: exit=%d stdout=%q stderr=%q", args, got, &out, &errors)
		}
	}
	if err := os.Remove(valid); err != nil {
		t.Fatalf("expected input handle retained after actual open failure: %v", err)
	}
}

func TestReplayUsage(t *testing.T) {
	for _, args := range [][]string{{"replay"}, {"replay", "one.json"}, {"replay", "one.json", "two.json", "extra"}} {
		var out, errors bytes.Buffer
		if got := run(args, &out, &errors); got != 2 || out.Len() != 0 || !strings.Contains(errors.String(), "usage:") {
			t.Fatalf("%q: exit=%d stdout=%q stderr=%q", args, got, &out, &errors)
		}
	}
}

func TestVersionReportsMigrationGate(t *testing.T) {
	var out, errors bytes.Buffer
	if run([]string{"version"}, &out, &errors) != 0 || !strings.Contains(out.String(), "native writes unavailable") {
		t.Fatalf("stdout=%q stderr=%q", &out, &errors)
	}
}
