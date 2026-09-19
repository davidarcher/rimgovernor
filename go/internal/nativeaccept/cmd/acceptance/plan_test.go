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
	repo, _ := repoOfCwd()
	for _, tc := range []struct {
		file, area string
		all        bool
	}{
		{"go/cmd/rimgovernor/serve_building.go", "light", false},
		{"go/internal/nativeaccept/cases/power/power.go", "power", false},
		{"integrations/rimgovernor-native/src/Changed.cs", "", true},
		{"docs/README.md", "", false},
	} {
		sel, err := affected.Select(repo, []string{tc.file})
		if err != nil {
			t.Fatal(err)
		}
		if sel.AllHarnesses != tc.all || tc.area != "" && !slices.Contains(sel.Cases, tc.area) {
			t.Fatalf("%s: %+v", tc.file, sel)
		}
		if tc.area == "" && !tc.all && len(sel.Cases) != 0 {
			t.Fatalf("documentation affected cases: %+v", sel)
		}
	}
}

func TestRemotePlanRejectsUnsupportedSelectionAndBudgets(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier = "land"
	r.Limits.Shards = 32
	if _, err := buildSelection(r, planReference{}, nil, affected.Selection{AllHarnesses: true}); err == nil {
		t.Fatal("native-wide selection silently fit unsupported runner")
	}
	r.Tier = "smoke"
	r.Limits.Shards = 1
	r.Limits.SuiteMinutes = 1
	if _, err := buildSelection(r, planReference{}, nil, affected.Selection{}); err == nil || !strings.Contains(err.Error(), "budgets") {
		t.Fatal(err)
	}
}

func TestRemotePlanRejectsMalformedRun(t *testing.T) {
	r := examplePlanRun(t)
	for _, mutate := range []func(*planRun){
		func(r *planRun) { r.Version = 2 }, func(r *planRun) { r.Base = strings.Repeat("0", 40) },
		func(r *planRun) { r.Tier = "land"; r.Base = r.Head }, func(r *planRun) { r.Trigger.Event = "pull_request" },
		func(r *planRun) { r.Limits.Shards = 33 }, func(r *planRun) { r.Limits.Workers = 2 },
		func(r *planRun) { r.Limits.Parallel = 5 }, func(r *planRun) { r.Limits.Paid = true },
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
