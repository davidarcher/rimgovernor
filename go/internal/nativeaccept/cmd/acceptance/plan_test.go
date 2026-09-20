package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/affected"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func examplePlanRun(t *testing.T) planRun {
	t.Helper()
	repo, ok := repoOfCwd()
	if !ok {
		t.Fatal("missing repo")
	}
	b, err := os.ReadFile(filepath.Join(repo, "docs/developers/contracts/remote-acceptance/run.json"))
	if err != nil {
		t.Fatal(err)
	}
	r, err := decodePlanRun(b)
	if err != nil {
		t.Fatal(err)
	}
	return r
}

func TestRemotePlanDeterministicCoverage(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier = "land"
	r.Limits.Shards = 32
	r.Limits.Attempts = 1
	for _, sel := range []affected.Selection{{}, {Cases: []string{"power"}}, {Cases: []string{"lifecycle"}, Sampled: []string{"lifecycle"}}} {
		p, err := buildSelection(r, planReference{}, nil, sel)
		if err != nil {
			t.Fatal(err)
		}
		q, err := buildSelection(r, planReference{}, nil, sel)
		if err != nil {
			t.Fatal(err)
		}
		if !reflect.DeepEqual(p, q) {
			t.Fatal("nondeterministic plan")
		}
		want, err := landCases(cases.All(), sel)
		if err != nil {
			t.Fatal(err)
		}
		seen := map[string]int{}
		for _, shard := range p.Shards {
			if len(shard.Cases) == 0 {
				t.Fatal("empty shard")
			}
			for _, name := range shard.Cases {
				seen[name]++
			}
		}
		if len(seen) != len(want) || len(p.Cases) != len(want) {
			t.Fatal("selection mismatch")
		}
		for _, c := range want {
			if seen[c.Name] != 1 {
				t.Fatalf("%s coverage = %d", c.Name, seen[c.Name])
			}
		}
		for i, c := range p.Cases {
			if !slices.Contains(p.Shards[i%len(p.Shards)].Cases, c.Name) {
				t.Fatal("not sorted round robin")
			}
			if len(c.Reasons) == 0 {
				t.Fatal("missing reason")
			}
		}
	}
}

func TestRemotePlanSmokeMatchesContract(t *testing.T) {
	r := examplePlanRun(t)
	p, err := buildSelection(r, planReference{}, []string{"b", "a", "b"}, affected.Selection{})
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(p.Files, []string{"a", "b"}) {
		t.Fatal(p.Files)
	}
	if !reflect.DeepEqual(p.Shards[0].Cases, []string{"light/dark", "pawn/reads", "smoke/dispatch"}) {
		t.Fatal(p.Shards)
	}
	if !reflect.DeepEqual(p.Cases[0].Roles, []string{"bridge", "controller"}) {
		t.Fatal(p.Cases[0])
	}
	// Untimed cases use the same algorithm: no ambient cost history enters it.
	r.Limits.Shards = 32
	p, err = buildSelection(r, planReference{}, nil, affected.Selection{})
	if err != nil {
		t.Fatal(err)
	}
	if len(p.Shards) != len(p.Cases) {
		t.Fatal("empty jobs")
	}
}

func TestRemotePlanPackageRoles(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier = "land"
	r.Limits.Shards = 32
	r.Limits.Attempts = 1
	p, err := buildSelection(r, planReference{}, nil, affected.Selection{Cases: []string{"rooms", "supplies"}})
	if err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"rooms/reads", "supplies/reads"} {
		found := false
		for _, c := range p.Cases {
			if c.Name == name {
				found = true
				if c.ModRole != "production" {
					t.Fatal(c)
				}
			}
		}
		if !found {
			t.Fatalf("missing %s", name)
		}
	}
}

func TestRemotePlanUsesAffectedEntryPointAndHarnessRules(t *testing.T) {
	// Exercise real Go discovery on a small module; the rules do not need
	// the production repository's dependency closure or external modules.
	repo := t.TempDir()
	for name, body := range map[string]string{
		"go.mod":                            "module example.com/plan\n\ngo 1.25\n",
		"cmd/rimgovernor/serve_building.go": "package main\nfunc main() {}\n",
		"internal/nativeaccept/cases/light/light.go": "package light\nvar Serve = true\n",
		"internal/nativeaccept/cases/power/power.go": "package power\n",
	} {
		file := filepath.Join(repo, "go", filepath.FromSlash(name))
		if err := os.MkdirAll(filepath.Dir(file), 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(file, []byte(body), 0600); err != nil {
			t.Fatal(err)
		}
	}
	for _, tc := range []struct {
		file  string
		areas []string
		all   bool
	}{
		{"go/cmd/rimgovernor/serve_building.go", []string{"light"}, false},
		{"go/internal/nativeaccept/cases/power/power.go", []string{"power"}, false},
		{"integrations/rimgovernor-native/src/Changed.cs", []string{"light", "power"}, true},
		{"docs/README.md", nil, false},
	} {
		sel, err := affected.Select(repo, []string{tc.file})
		if err != nil {
			t.Fatal(err)
		}
		if sel.AllHarnesses != tc.all || !slices.Equal(sel.Cases, tc.areas) {
			t.Fatalf("%s: %+v", tc.file, sel)
		}
	}
}

func TestRemotePlanRejectsBudgets(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier = "smoke"
	r.Limits.Shards = 1
	r.Limits.SuiteMinutes = 1
	if _, err := buildSelection(r, planReference{}, nil, affected.Selection{}); err == nil || !strings.Contains(err.Error(), "budgets") {
		t.Fatal(err)
	}
}

func TestRemoteLandCompleteRegistryFitsEightShards(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier = "land"
	r.Limits.Shards, r.Limits.Attempts = 8, 1
	r.Limits.JobMinutes, r.Limits.SuiteMinutes = 360, 345
	if _, err := buildSelection(r, planReference{}, nil, affected.Selection{AllHarnesses: true}); err != nil {
		t.Fatal(err)
	}
}

func TestNightlyFullIncludesRenderedCases(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier, r.Trigger.Event, r.Trigger.Ref = "full", "schedule", "refs/heads/main"
	r.Base = r.Head
	r.Limits.Shards, r.Limits.Attempts = 32, 1
	r.Limits.Parallel = 20
	r.Limits.JobMinutes, r.Limits.SuiteMinutes = 360, 345
	if err := r.validate(); err != nil {
		t.Fatal(err)
	}
	p, err := buildSelection(r, planReference{}, nil, affected.Selection{})
	if err != nil {
		t.Fatal(err)
	}
	full, err := tierCases("full", "", "")
	if err != nil {
		t.Fatal(err)
	}
	if len(p.Cases) != len(full.Cases) {
		t.Fatal("full selection dropped cases")
	}
	for _, c := range full.Cases {
		idx := slices.IndexFunc(p.Cases, func(pc plannedCase) bool { return pc.Name == c.Name })
		if idx < 0 || p.Cases[idx].Rendered != c.Rendered {
			t.Fatalf("lost rendering requirement for %s", c.Name)
		}
	}
	r.Trigger.Ref = "refs/heads/topic"
	if err := r.validate(); err == nil {
		t.Fatal("scheduled topic branch accepted")
	}
}

func TestRemoteRenderedAreaPlansNormally(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier, r.Limits.Shards, r.Limits.Attempts = "land", 4, 1
	p, err := buildSelection(r, planReference{}, nil, affected.Selection{Cases: []string{"video"}})
	if err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"video/stream", "video/feeds", "video/matrix", "video/source-spike"} {
		i := slices.IndexFunc(p.Cases, func(c plannedCase) bool { return c.Name == name })
		if i < 0 || !p.Cases[i].Rendered {
			t.Fatalf("rendered case missing: %s", name)
		}
	}
}

func TestRemotePlanRejectsMalformedRun(t *testing.T) {
	r := examplePlanRun(t)
	for _, mutate := range []func(*planRun){
		func(r *planRun) { r.Version = 2 }, func(r *planRun) { r.Base = strings.Repeat("0", 40) },
		func(r *planRun) { r.Tier = "land"; r.Base = r.Head }, func(r *planRun) { r.Trigger.Event = "pull_request" },
		func(r *planRun) { r.Limits.Shards = 33 }, func(r *planRun) { r.Limits.Workers = 2 },
		func(r *planRun) { r.Limits.Parallel = 21 }, func(r *planRun) { r.Limits.Paid = true },
		func(r *planRun) { r.Bundle.Path = "../bundle.json" }, func(r *planRun) { r.Repository = "host/repo?query" },
	} {
		bad := r
		mutate(&bad)
		if err := bad.validate(); err == nil {
			t.Fatalf("accepted %+v", bad)
		}
	}
	b, _ := json.Marshal(r)
	for _, bad := range [][]byte{bytes.Replace(b, []byte(`"schema_version":1`), []byte(`"schema_version":1,"schema_version":1`), 1), bytes.Replace(b, []byte(`"paid_usage_authorized":false`), []byte(`"paid_usage_authorized":null`), 1), append(append([]byte{}, b...), []byte(` {}`)...)} {
		if _, err := decodePlanRun(bad); err == nil {
			t.Fatal("accepted malformed JSON")
		}
	}
}

func TestRemoteComparisonHistoryAndRename(t *testing.T) {
	repo := t.TempDir()
	git := func(args ...string) string {
		t.Helper()
		out, err := planGit(repo, args...)
		if err != nil {
			t.Fatal(err)
		}
		return out
	}
	git("init")
	git("config", "user.name", "Planner test")
	git("config", "user.email", "planner@example.invalid")
	write := func(name, body string) {
		t.Helper()
		if err := os.WriteFile(filepath.Join(repo, name), []byte(body), 0600); err != nil {
			t.Fatal(err)
		}
	}
	write(" old.txt", "original")
	git("add", ".")
	git("commit", "-m", "base")
	r := examplePlanRun(t)
	r.Base = git("rev-parse", "HEAD")
	git("mv", " old.txt", "new.txt")
	git("commit", "-m", "rename")
	r.Head = git("rev-parse", "HEAD")
	if _, err := planComparison(repo, r, false); err == nil {
		t.Fatal("accepted attached branch")
	}
	git("checkout", "--detach")
	files, err := planComparison(repo, r, false)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(files, []string{" old.txt", "new.txt"}) {
		t.Fatal(files)
	}
	write("new.txt", "dirty")
	if _, err := planComparison(repo, r, false); err == nil {
		t.Fatal("accepted dirty checkout")
	}
	git("restore", "new.txt")
	write("injected.go", "package main")
	if _, err := planComparison(repo, r, false); err == nil {
		t.Fatal("accepted untracked source")
	}
	if err := os.Remove(filepath.Join(repo, "injected.go")); err != nil {
		t.Fatal(err)
	}
	r.Base = strings.Repeat("e", 40)
	if _, err := planComparison(repo, r, false); err == nil {
		t.Fatal("missing history became empty success")
	}
	r.Base = r.Head
	git("checkout", "--detach", "HEAD^")
	r.Head = git("rev-parse", "HEAD")
	if _, err := planComparison(repo, r, false); err == nil {
		t.Fatal("accepted non-ancestor")
	}
	// Smoke explicitly allows an equal base/head. Exercise the complete CLI
	// and verify that provenance hashes the original bytes, not reserialized JSON.
	r.Base = r.Head
	raw, err := json.MarshalIndent(r, "", "    ")
	if err != nil {
		t.Fatal(err)
	}
	evidence := t.TempDir()
	if err := os.WriteFile(filepath.Join(evidence, "run.json"), raw, 0600); err != nil {
		t.Fatal(err)
	}
	t.Chdir(repo)
	var out, diagnostics bytes.Buffer
	if code := run([]string{"plan", "-evidence", evidence, "-run", "run.json"}, &out, &diagnostics); code != 0 {
		t.Fatalf("exit %d: %s", code, diagnostics.String())
	}
	var p remoteSelection
	if err := json.Unmarshal(out.Bytes(), &p); err != nil {
		t.Fatal(err)
	}
	if p.Run.SHA256 != fmt.Sprintf("%x", sha256.Sum256(raw)) || p.Commit != r.Head || len(p.Files) != 0 || len(p.Shards) != 2 {
		t.Fatalf("bad provenance: %+v", p)
	}
}

func TestRemotePlanRouteRejectsMissingRun(t *testing.T) {
	var out, err bytes.Buffer
	if run([]string{"plan"}, &out, &err) != 2 || out.Len() != 0 || !strings.Contains(err.String(), "acceptance plan") {
		t.Fatalf("%s %s", out.String(), err.String())
	}
}
