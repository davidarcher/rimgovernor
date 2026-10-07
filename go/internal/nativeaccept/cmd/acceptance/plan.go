package main

// Remote planning uses the tested checkout's registry and affected rules. It
// never starts a game or silently reduces a selection to fit runner limits.
import (
	"bytes"
	"crypto/sha256"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"regexp"
	"slices"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/remoteaccept"
)

type planReference struct {
	Path   string `json:"path"`
	SHA256 string `json:"sha256"`
}

type planLimits struct {
	Runner        string `json:"runner_label"`
	Shards        int    `json:"shards"`
	Parallel      int    `json:"max_parallel"`
	Workers       int    `json:"workers_per_shard"`
	JobMinutes    int    `json:"job_timeout_minutes"`
	SuiteMinutes  int    `json:"suite_timeout_minutes"`
	Attempts      int    `json:"max_attempts"`
	Retention     int    `json:"artifact_retention_days"`
	ArtifactBytes int64  `json:"artifact_max_bytes"`
	Paid          bool   `json:"paid_usage_authorized"`
}

type planRun struct {
	Version    int    `json:"schema_version"`
	ID         string `json:"run_id"`
	Repository string `json:"repository"`
	Trigger    struct {
		Event   string `json:"event"`
		Actor   string `json:"actor"`
		Ref     string `json:"published_ref"`
		ID      int64  `json:"actions_run_id"`
		Attempt int    `json:"actions_run_attempt"`
	} `json:"trigger"`
	Workflow string        `json:"workflow_commit"`
	Head     string        `json:"tested_commit"`
	Tier     string        `json:"tier"`
	Cases    []string      `json:"cases,omitempty"`
	Bundle   planReference `json:"bundle"`
	Limits   planLimits    `json:"limits"`
}

type plannedCase struct {
	BudgetNS   int64    `json:"budget_ns,omitempty"`
	Name       string   `json:"name"`
	Reasons    []string `json:"reasons"`
	FixtureOps []string `json:"fixture_ops"`
	Roles      []string `json:"roles"`
	ModRole    string   `json:"mod_role"`
	Rendered   bool     `json:"rendered"`
}
type plannedShard struct {
	ID    string   `json:"id"`
	Cases []string `json:"cases"`
}
type remoteSelection struct {
	Version   int                        `json:"schema_version"`
	Run       planReference              `json:"run"`
	Commit    string                     `json:"planner_commit"`
	Cases     []plannedCase              `json:"cases"`
	Skipped   []remoteaccept.SkippedCase `json:"skipped,omitempty"`
	Algorithm string                     `json:"algorithm"`
	Shards    []plannedShard             `json:"shards"`
}

var planOID = regexp.MustCompile(`^[0-9a-f]{40}$`)
var planDigest = regexp.MustCompile(`^[0-9a-f]{64}$`)
var planRepository = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$`)

func validPlanPath(s string) bool {
	if s == "" || strings.ContainsAny(s, `\:`) || strings.HasPrefix(s, "/") {
		return false
	}
	for _, p := range strings.Split(s, "/") {
		if p == "" || p == "." || p == ".." {
			return false
		}
	}
	return true
}

func (r planRun) validate() error {
	if r.Version != 1 || !planOID.MatchString(r.Head) || !planOID.MatchString(r.Workflow) || r.Head == strings.Repeat("0", 40) || r.Workflow == strings.Repeat("0", 40) {
		return fmt.Errorf("run requires schema_version 1 and nonzero full commit identities")
	}
	if r.Tier != "smoke" && r.Tier != "nightly" && r.Tier != "cases" {
		return fmt.Errorf("unsupported remote tier %q", r.Tier)
	}
	if (r.Tier == "cases") != (len(r.Cases) > 0) {
		return fmt.Errorf("the cases tier, and only it, takes a nonempty case list")
	}
	if r.Trigger.Event != "workflow_dispatch" && r.Trigger.Event != "schedule" {
		return fmt.Errorf("unsupported trigger %q", r.Trigger.Event)
	}
	if r.Trigger.Event == "schedule" && r.Trigger.Ref != "refs/heads/main" {
		return fmt.Errorf("scheduled planning requires refs/heads/main")
	}
	if !planRepository.MatchString(r.Repository) || r.Trigger.Actor == "" || r.Trigger.Ref == "" || r.Trigger.ID < 1 || r.Trigger.Attempt < 1 || r.ID != fmt.Sprintf("gh:%s:%d:%d", r.Repository, r.Trigger.ID, r.Trigger.Attempt) {
		return fmt.Errorf("invalid run provenance")
	}
	if !validPlanPath(r.Bundle.Path) || !planDigest.MatchString(r.Bundle.SHA256) {
		return fmt.Errorf("invalid bundle reference")
	}
	l := r.Limits
	if l.Runner != "windows-2022" || l.Shards < 1 || l.Shards > 32 || l.Parallel < 1 || l.Parallel > 20 || l.Workers != 1 || l.Paid || l.JobMinutes < 15 || l.JobMinutes > 360 || l.SuiteMinutes < 1 || l.SuiteMinutes > 345 || l.JobMinutes-l.SuiteMinutes < 5 || l.Attempts < 1 || l.Attempts > 2 || l.Retention < 1 || l.Retention > 7 || l.ArtifactBytes < 0 {
		return fmt.Errorf("run limits exceed remote v1 capabilities")
	}
	return nil
}

// uniqueJSON rejects duplicate keys at every depth before typed decoding.
func uniqueJSON(d *json.Decoder) error {
	t, err := d.Token()
	if err != nil {
		return err
	}
	delim, ok := t.(json.Delim)
	if !ok {
		return nil
	}
	switch delim {
	case '{':
		seen := map[string]bool{}
		for d.More() {
			key, err := d.Token()
			if err != nil {
				return err
			}
			name, ok := key.(string)
			if !ok || seen[name] {
				return fmt.Errorf("duplicate or invalid JSON key %v", key)
			}
			seen[name] = true
			if err := uniqueJSON(d); err != nil {
				return err
			}
		}
	case '[':
		for d.More() {
			if err := uniqueJSON(d); err != nil {
				return err
			}
		}
	default:
		return fmt.Errorf("unexpected JSON delimiter")
	}
	_, err = d.Token()
	return err
}

func decodePlanRun(raw []byte) (planRun, error) {
	var r planRun
	d := json.NewDecoder(bytes.NewReader(raw))
	if err := uniqueJSON(d); err != nil {
		return r, err
	}
	if _, err := d.Token(); err != io.EOF {
		return r, fmt.Errorf("trailing JSON data")
	}
	if err := json.Unmarshal(raw, &r); err != nil {
		return r, err
	}
	// false is a valid value, but omission and null are not authorization.
	var fields struct {
		Limits map[string]json.RawMessage `json:"limits"`
	}
	if err := json.Unmarshal(raw, &fields); err != nil {
		return r, err
	}
	if v := fields.Limits["paid_usage_authorized"]; !bytes.Equal(bytes.TrimSpace(v), []byte("false")) {
		return r, fmt.Errorf("paid_usage_authorized must explicitly be false")
	}
	return r, r.validate()
}

func buildSelection(r planRun, ref planReference) (remoteSelection, error) {
	p := remoteSelection{Version: 1, Run: ref, Commit: r.Head, Algorithm: remoteaccept.BudgetAlgorithm}
	if err := r.validate(); err != nil {
		return p, err
	}
	all := cases.All()
	smoke, err := smokeCases(all)
	if err != nil {
		return p, err
	}
	selected := smoke
	if r.Tier == "nightly" {
		if selected, err = tierCases(r.Tier); err != nil {
			return p, err
		}
	}
	if r.Tier == "cases" {
		selected, err = requestedCases(all, r.Cases)
		if err != nil {
			return p, err
		}
	}
	slices.SortFunc(selected, func(a, b cases.Case) int { return strings.Compare(a.Name, b.Name) })
	// Hosted Windows has no usable GPU. Keep exclusions explicit and remove
	// them before assigning shards or charging execution budgets.
	selected = slices.DeleteFunc(selected, func(c cases.Case) bool {
		if !c.Rendered {
			return false
		}
		p.Skipped = append(p.Skipped, remoteaccept.SkippedCase{Name: c.Name, Reason: "rendered"})
		return true
	})
	if len(selected) == 0 {
		return p, fmt.Errorf("empty remote selection")
	}
	names := make([]string, len(selected))
	costs, ceilings := map[string]int64{}, map[string]int64{}
	for i, c := range selected {
		names[i] = c.Name
		costs[c.Name] = remoteaccept.ShardCost(c.Name, c.Budget)
		ceilings[c.Name] = int64(c.Budget)
	}
	shards, err := remoteaccept.PlanShards(names, r.Limits.Shards, p.Algorithm, costs)
	if err != nil {
		return p, err
	}
	// Measured times balance real wall time, but every shard's budgets
	// must still fit the suite allowance; when they do not, plan on the
	// budgets themselves.
	allowance := int64(time.Duration(r.Limits.SuiteMinutes)*time.Minute) / int64(r.Limits.Attempts)
	for _, shard := range shards {
		var load int64
		for _, name := range shard.Cases {
			load += ceilings[name]
		}
		if load > allowance {
			if shards, err = remoteaccept.PlanShards(names, r.Limits.Shards, p.Algorithm, ceilings); err != nil {
				return p, err
			}
			break
		}
	}
	assignment := map[string]int{}
	for i, shard := range shards {
		p.Shards = append(p.Shards, plannedShard{ID: shard.ID, Cases: shard.Cases})
		for _, name := range shard.Cases {
			assignment[name] = i
		}
	}
	budgets := make([]time.Duration, len(shards))
	for _, c := range selected {
		if c.Matrix {
			return p, fmt.Errorf("%s requires a separate matrix selection; selection cannot be truncated", c.Name)
		}
		row := plannedCase{Name: c.Name, BudgetNS: int64(c.Budget), Reasons: []string{}, FixtureOps: append([]string{}, c.FixtureOps()...), Roles: []string{"bridge"}, ModRole: "fixture", Rendered: c.Rendered}
		if serveDriven(c) {
			row.Roles = append(row.Roles, "controller")
		}
		for _, s := range smoke {
			if s.Name == c.Name {
				row.Reasons = append(row.Reasons, "smoke")
			}
		}
		if r.Tier == "nightly" {
			row.Reasons = append(row.Reasons, r.Tier)
		}
		if r.Tier == "cases" {
			row.Reasons = append(row.Reasons, "requested")
		}
		slices.Sort(row.Reasons)
		slices.Sort(row.FixtureOps)
		if len(row.Reasons) == 0 || c.Budget <= 0 {
			return p, fmt.Errorf("%s lacks selection reason or budget", c.Name)
		}
		p.Cases = append(p.Cases, row)
		budgets[assignment[c.Name]] += c.Budget * time.Duration(r.Limits.Attempts)
	}
	for i, budget := range budgets {
		if budget > time.Duration(r.Limits.SuiteMinutes)*time.Minute {
			return p, fmt.Errorf("%s known case budgets including retries (%s) exceed suite allowance; increase shards within v1 limits", p.Shards[i].ID, budget)
		}
	}
	return p, nil
}

func plan(args []string, stdout, stderr io.Writer) int {
	fs := flag.NewFlagSet("plan", flag.ContinueOnError)
	fs.SetOutput(stderr)
	path := fs.String("run", "", "run.json path relative to evidence root")
	root := fs.String("evidence", ".", "evidence root")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	fail := func(err error) int { fmt.Fprintln(stderr, err); return 2 }
	if fs.NArg() != 0 || !validPlanPath(*path) {
		return fail(fmt.Errorf("usage: acceptance plan -evidence <root> -run <relative run.json>"))
	}
	// os.Root prevents a reference escaping through a filesystem link.
	dir, err := os.OpenRoot(*root)
	if err != nil {
		return fail(err)
	}
	defer dir.Close()
	raw, err := dir.ReadFile(*path)
	if err != nil {
		return fail(err)
	}
	r, err := decodePlanRun(raw)
	if err != nil {
		return fail(err)
	}
	p, err := buildSelection(r, planReference{Path: *path, SHA256: fmt.Sprintf("%x", sha256.Sum256(raw))})
	if err != nil {
		return fail(err)
	}
	enc := json.NewEncoder(stdout)
	enc.SetIndent("", "  ")
	if err := enc.Encode(p); err != nil {
		return fail(err)
	}
	return 0
}

// requestedCases resolves an on-demand dispatch list. Each entry is an exact
// registry name or a bare area naming every case in it; an entry that matches
// nothing refuses the plan rather than quietly running less.
func requestedCases(all []cases.Case, want []string) ([]cases.Case, error) {
	seen := map[string]bool{}
	var out []cases.Case
	for _, w := range want {
		w = strings.TrimSpace(w)
		hit := false
		for _, c := range all {
			area, _, _ := strings.Cut(c.Name, "/")
			if c.Name != w && area != w {
				continue
			}
			hit = true
			if !seen[c.Name] {
				seen[c.Name] = true
				out = append(out, c)
			}
		}
		if !hit {
			return nil, fmt.Errorf("requested case %q matches no registered case or area", w)
		}
	}
	return out, nil
}
