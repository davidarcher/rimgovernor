package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestGateRefusesNativeDiffWithoutResults(t *testing.T) {
	native := []string{"integrations/rimgovernor-native/src/Foo.cs"}
	runtime := []string{"go/internal/buildingruntime/plan.go"}
	plain := []string{"go/internal/policy/x.go", "docs/README.md"}
	for _, changed := range [][]string{native, runtime} {
		err := acceptanceGate{}.check(changed)
		if err == nil || !strings.Contains(err.Error(), "-tier smoke") || !strings.Contains(err.Error(), changed[0]) {
			t.Errorf("%v: got %v", changed, err)
		}
		if err := (acceptanceGate{Unverified: true}).check(changed); err != nil {
			t.Errorf("%v -unverified: %v", changed, err)
		}
	}
	if err := (acceptanceGate{}).check(plain); err != nil {
		t.Errorf("plain diff: %v", err)
	}
	if got := gatedFiles(append(append([]string{}, plain...), native...)); len(got) != 1 || got[0] != native[0] {
		t.Errorf("gatedFiles = %v", got)
	}
}

func TestGateReadsTheSuiteReport(t *testing.T) {
	dir := t.TempDir()
	write(t, filepath.Join(dir, "result.json"), `{"passed": true, "tier": "land", "cases": [{"name": "smoke/identity", "passed": true}, {"name": "light/dark", "passed": true}]}`)
	gate := acceptanceGate{Results: dir}
	if err := gate.check([]string{"integrations/rimgovernor-native/src/Foo.cs"}); err != nil {
		t.Errorf("passing suite: %v", err)
	}
	summary, err := readSuiteResults(dir)
	if err != nil || !strings.HasPrefix(summary, "land suite, 2 case(s) passed fresh") {
		t.Errorf("summary %q, %v", summary, err)
	}

	write(t, filepath.Join(dir, "result.json"), `{"passed": true, "cases": [{"name": "smoke/identity", "passed": true, "resumed_from": "t+7m"}]}`)
	if err := gate.check(nil); err != nil {
		t.Errorf("resumed row lands: %v", err)
	}
	if summary, _ := readSuiteResults(dir); !strings.Contains(summary, "1 resumed from a checkpoint (smoke/identity: passed past the resume point only)") {
		t.Errorf("resumed summary %q", summary)
	}

	write(t, filepath.Join(dir, "result.json"), `{"passed": true, "cases": [{"name": "smoke/identity", "passed": true, "postmortem_only": true}]}`)
	if err := gate.check(nil); err == nil || !strings.Contains(err.Error(), "ran postmortem-only (smoke/identity)") {
		t.Errorf("postmortem-only row: got %v", err)
	}

	write(t, filepath.Join(dir, "result.json"), `{"passed": true, "cases": [{"name": "shelter/hut", "passed": true, "staged_from": {"stage": "ring"}}]}`)
	if err := gate.check(nil); err == nil || !strings.Contains(err.Error(), "opened on a cached stage bundle (shelter/hut)") {
		t.Errorf("staged row: got %v", err)
	}

	write(t, filepath.Join(dir, "result.json"), `{"passed": false, "error": "1 of 2 cases failed: light/dark", "cases": [{"name": "light/dark"}]}`)
	if err := gate.check(nil); err == nil || !strings.Contains(err.Error(), "1 of 2 cases failed: light/dark") {
		t.Errorf("failed suite: got %v", err)
	}

	write(t, filepath.Join(dir, "result.json"), `{"passed": true}`)
	if err := gate.check(nil); err == nil || !strings.Contains(err.Error(), "has no cases") {
		t.Errorf("case result.json instead of a suite's: got %v", err)
	}
	if err := (acceptanceGate{Results: filepath.Join(dir, "missing")}).check(nil); err == nil {
		t.Error("missing results should be refused")
	}
}

func TestLandRefusesAGatedDiffWithoutResults(t *testing.T) {
	root, wt := newRepo(t)
	path := filepath.Join(wt, "integrations", "rimgovernor-native", "src", "Foo.cs")
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	write(t, path, "class Foo {}\n")
	mustGit(t, wt, "add", ".")
	mustGit(t, wt, "commit", "-qm", "native edit")
	t.Chdir(wt)
	err := run("", "", "", time.Second, false, acceptanceGate{}, nil)
	if err == nil || !strings.Contains(err.Error(), "presents no acceptance results") {
		t.Fatalf("gated diff: got %v", err)
	}
	if got := mustGit(t, root, "log", "--format=%s", "main"); got != "init" {
		t.Errorf("main changed:\n%s", got)
	}
	results := t.TempDir()
	write(t, filepath.Join(results, "result.json"), `{"passed": true, "tier": "land", "cases": [{"name": "smoke/identity", "passed": true}]}`)
	if err := run("", "", "", time.Second, false, acceptanceGate{Results: results}, nil); err != nil {
		t.Fatal(err)
	}
	if got := mustGit(t, root, "log", "--format=%s", "main"); got != "native edit\ninit" {
		t.Errorf("main subjects:\n%s", got)
	}
}
