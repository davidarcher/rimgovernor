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

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/remoteaccept"
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
	r.Tier = "nightly"
	r.Limits.Shards = 32
	r.Limits.Attempts = 1
	r.Limits.JobMinutes, r.Limits.SuiteMinutes = 360, 345
	p, err := buildSelection(r, planReference{})
	if err != nil {
		t.Fatal(err)
	}
	q, err := buildSelection(r, planReference{})
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(p, q) {
		t.Fatal("nondeterministic plan")
	}
	want, err := tierCases("nightly")
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
	if len(seen) != len(p.Cases) || len(p.Cases)+len(p.Skipped) != len(want) {
		t.Fatal("selection mismatch")
	}
	for _, c := range p.Cases {
		if seen[c.Name] != 1 {
			t.Fatalf("%s coverage = %d", c.Name, seen[c.Name])
		}
		if p.Algorithm != remoteaccept.BudgetAlgorithm || c.BudgetNS <= 0 {
			t.Fatal("missing versioned budget evidence")
		}
		if len(c.Reasons) == 0 {
			t.Fatal("missing reason")
		}
	}
}

func TestRemotePlanSmokeMatchesContract(t *testing.T) {
	r := examplePlanRun(t)
	p, err := buildSelection(r, planReference{})
	if err != nil {
		t.Fatal(err)
	}
	// Costs are the committed measured times (remoteaccept.ShardCost), so
	// light/dark (the slowest) gets a shard to itself.
	if !reflect.DeepEqual(p.Shards[0].Cases, []string{"light/dark"}) {
		t.Fatal(p.Shards)
	}
	if !reflect.DeepEqual(p.Cases[0].Roles, []string{"bridge", "controller"}) {
		t.Fatal(p.Cases[0])
	}
	// Only committed costs enter the plan; no ambient timing history does.
	r.Limits.Shards = 32
	p, err = buildSelection(r, planReference{})
	if err != nil {
		t.Fatal(err)
	}
	if len(p.Shards) != len(p.Cases) {
		t.Fatal("empty jobs")
	}
}

func TestRemotePlanRejectsBudgets(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier = "smoke"
	r.Limits.Shards = 1
	r.Limits.SuiteMinutes = 1
	if _, err := buildSelection(r, planReference{}); err == nil || !strings.Contains(err.Error(), "budgets") {
		t.Fatal(err)
	}
}

func TestRemotePlanMatrixDependencies(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier = "nightly"
	r.Limits.Shards, r.Limits.Attempts = 32, 1
	r.Limits.JobMinutes, r.Limits.SuiteMinutes = 360, 345
	p, err := buildSelection(r, planReference{})
	if err != nil {
		t.Fatal(err)
	}
	q, err := buildSelection(r, planReference{})
	if err != nil || !reflect.DeepEqual(p, q) {
		t.Fatalf("nondeterministic plan: %v", err)
	}
	seen := map[string]int{}
	for _, shard := range p.Shards {
		for _, name := range shard.Cases {
			seen[name]++
		}
	}
	for _, c := range p.Cases {
		if seen[c.Name] != 1 {
			t.Fatalf("%s coverage = %d", c.Name, seen[c.Name])
		}
	}
	for _, v := range sustained.Variants {
		suffix := sustained.Short(v.Save)
		generator, consumer := "tools/variantsavegen-"+suffix, "sustained/matrix-"+suffix
		// Both are off-tier (#739): the full plan carries neither, but a
		// hand-picked selection still pairs them.
		if seen[consumer]+seen[generator] != 0 {
			t.Fatalf("nightly plan carries off-tier %s or %s", consumer, generator)
		}
		// The executor reorders by process tier; that must also keep
		// the generator ahead of its serve-driven consumer.
		g, _ := cases.Lookup(generator)
		c, _ := cases.Lookup(consumer)
		queue := []entry{{Name: consumer, registered: &c}, {Name: generator, registered: &g}}
		schedule(queue, nil)
		if queue[0].Name != generator {
			t.Fatal("suite scheduling reverses dependency")
		}
		if _, err := remoteaccept.PlanShards([]string{consumer}, 32, p.Algorithm, nil); err == nil || !strings.Contains(err.Error(), generator) {
			t.Fatalf("missing generator did not fail planning: %v", err)
		}
	}
}

func TestNightlySkipsRenderedCases(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier, r.Trigger.Event, r.Trigger.Ref = "nightly", "schedule", "refs/heads/main"
	r.Limits.Shards, r.Limits.Attempts = 32, 1
	r.Limits.Parallel = 20
	r.Limits.ArtifactBytes = 0
	r.Limits.JobMinutes, r.Limits.SuiteMinutes = 360, 345
	if err := r.validate(); err != nil {
		t.Fatal(err)
	}
	p, err := buildSelection(r, planReference{})
	if err != nil {
		t.Fatal(err)
	}
	full, err := tierCases("nightly")
	if err != nil {
		t.Fatal(err)
	}
	if len(p.Cases)+len(p.Skipped) != len(full) {
		t.Fatal("full selection dropped cases")
	}
	for _, c := range full {
		idx := slices.IndexFunc(p.Cases, func(pc plannedCase) bool { return pc.Name == c.Name })
		if c.Rendered {
			skipped := slices.IndexFunc(p.Skipped, func(pc remoteaccept.SkippedCase) bool { return pc.Name == c.Name && pc.Reason == "rendered" })
			if idx >= 0 || skipped < 0 {
				t.Fatalf("rendered case not skipped: %s", c.Name)
			}
			continue
		}
		if idx < 0 || p.Cases[idx].Rendered {
			t.Fatalf("lost rendering requirement for %s", c.Name)
		}
	}
	r.Trigger.Ref = "refs/heads/topic"
	if err := r.validate(); err == nil {
		t.Fatal("scheduled topic branch accepted")
	}
}

func TestRemotePlanRejectsMalformedRun(t *testing.T) {
	r := examplePlanRun(t)
	for _, mutate := range []func(*planRun){
		func(r *planRun) { r.Version = 2 },
		func(r *planRun) { r.Tier = "land" }, func(r *planRun) { r.Trigger.Event = "pull_request" },
		func(r *planRun) { r.Limits.Shards = 33 }, func(r *planRun) { r.Limits.Workers = 2 },
		func(r *planRun) { r.Limits.Parallel = 21 }, func(r *planRun) { r.Limits.Paid = true },
		func(r *planRun) { r.Limits.ArtifactBytes = -1 },
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

func TestRemotePlanCommandHashesOriginalBytes(t *testing.T) {
	r := examplePlanRun(t) // Exercise the complete CLI and verify that provenance hashes the original	// bytes, not reserialized JSON.
	raw, err := json.MarshalIndent(r, "", "    ")
	if err != nil {
		t.Fatal(err)
	}
	evidence := t.TempDir()
	if err := os.WriteFile(filepath.Join(evidence, "run.json"), raw, 0600); err != nil {
		t.Fatal(err)
	}
	var out, diagnostics bytes.Buffer
	if code := run([]string{"plan", "-evidence", evidence, "-run", "run.json"}, &out, &diagnostics); code != 0 {
		t.Fatalf("exit %d: %s", code, diagnostics.String())
	}
	var p remoteSelection
	if err := json.Unmarshal(out.Bytes(), &p); err != nil {
		t.Fatal(err)
	}
	if p.Run.SHA256 != fmt.Sprintf("%x", sha256.Sum256(raw)) || p.Commit != r.Head || len(p.Shards) != 2 {
		t.Fatalf("bad provenance: %+v", p)
	}
}

func TestRemotePlanRouteRejectsMissingRun(t *testing.T) {
	var out, err bytes.Buffer
	if run([]string{"plan"}, &out, &err) != 2 || out.Len() != 0 || !strings.Contains(err.String(), "acceptance plan") {
		t.Fatalf("%s %s", out.String(), err.String())
	}
}

func TestRemotePlanRequestedCases(t *testing.T) {
	r := examplePlanRun(t)
	r.Tier = "cases"
	if _, err := buildSelection(r, planReference{}); err == nil {
		t.Fatal("cases tier without a list planned")
	}
	r.Cases = []string{"smoke/identity", "light", "smoke/identity"}
	p, err := buildSelection(r, planReference{})
	if err != nil {
		t.Fatal(err)
	}
	var names []string
	for _, c := range p.Cases {
		if !reflect.DeepEqual(c.Reasons, []string{"requested"}) && !reflect.DeepEqual(c.Reasons, []string{"requested", "smoke"}) {
			t.Fatal(c)
		}
		names = append(names, c.Name)
		if !strings.HasPrefix(c.Name, "light/") && c.Name != "smoke/identity" {
			t.Fatal(c.Name)
		}
	}
	if !slices.Contains(names, "smoke/identity") || !slices.Contains(names, "light/dark") {
		t.Fatal(names)
	}
	r.Cases = []string{"no/such"}
	if _, err := buildSelection(r, planReference{}); err == nil || !strings.Contains(err.Error(), "no/such") {
		t.Fatal(err)
	}
	r.Tier, r.Cases = "smoke", []string{"light"}
	if _, err := buildSelection(r, planReference{}); err == nil {
		t.Fatal("smoke tier accepted a case list")
	}
}
