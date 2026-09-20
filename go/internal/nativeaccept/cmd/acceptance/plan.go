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
	"os/exec"
	"regexp"
	"slices"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/affected"
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
	Base     string        `json:"base_commit"`
	Tier     string        `json:"tier"`
	Bundle   planReference `json:"bundle"`
	Limits   planLimits    `json:"limits"`
}

type plannedCase struct {
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
	DiffMode  string                     `json:"diff_mode"`
	Files     []string                   `json:"changed_files"`
	Cases     []plannedCase              `json:"cases"`
	Skipped   []remoteaccept.SkippedCase `json:"skipped,omitempty"`
	Sampled   []string                   `json:"sampled_areas"`
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
	if r.Version != 1 || !planOID.MatchString(r.Head) || !planOID.MatchString(r.Base) || !planOID.MatchString(r.Workflow) || r.Base == strings.Repeat("0", 40) || r.Head == strings.Repeat("0", 40) || r.Workflow == strings.Repeat("0", 40) {
		return fmt.Errorf("run requires schema_version 1 and nonzero full commit identities")
	}
	if r.Tier != "land" && r.Tier != "smoke" && r.Tier != "full" {
		return fmt.Errorf("unsupported remote tier %q", r.Tier)
	}
	if r.Tier == "land" && r.Base == r.Head {
		return fmt.Errorf("land requires distinct base and tested commits")
	}
	if r.Trigger.Event != "workflow_dispatch" && r.Trigger.Event != "push" && r.Trigger.Event != "schedule" {
		return fmt.Errorf("unsupported trigger %q", r.Trigger.Event)
	}
	if (r.Trigger.Event == "push" || r.Trigger.Event == "schedule") && r.Trigger.Ref != "refs/heads/main" {
		return fmt.Errorf("push planning requires refs/heads/main")
	}
	if !planRepository.MatchString(r.Repository) || r.Trigger.Actor == "" || r.Trigger.Ref == "" || r.Trigger.ID < 1 || r.Trigger.Attempt < 1 || r.ID != fmt.Sprintf("gh:%s:%d:%d", r.Repository, r.Trigger.ID, r.Trigger.Attempt) {
		return fmt.Errorf("invalid run provenance")
	}
	if !validPlanPath(r.Bundle.Path) || !planDigest.MatchString(r.Bundle.SHA256) {
		return fmt.Errorf("invalid bundle reference")
	}
	l := r.Limits
	if l.Runner != "windows-2022" || l.Shards < 1 || l.Shards > 32 || l.Parallel < 1 || l.Parallel > 20 || l.Workers != 1 || l.Paid || l.JobMinutes < 16 || l.JobMinutes > 360 || l.SuiteMinutes < 1 || l.SuiteMinutes > 345 || l.JobMinutes-l.SuiteMinutes < 15 || l.Attempts < 1 || l.Attempts > 2 || l.Retention < 1 || l.Retention > 7 || l.ArtifactBytes < 0 {
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

func planGit(repo string, args ...string) (string, error) {
	cmd := exec.Command("git", append([]string{"-C", repo}, args...)...)
	b, err := cmd.CombinedOutput()
	if err != nil {
		return "", fmt.Errorf("git %s: %w: %s", strings.Join(args, " "), err, b)
	}
	// NUL-delimited paths may themselves start with whitespace.
	if slices.Contains(args, "-z") {
		return string(b), nil
	}
	return strings.TrimSpace(string(b)), nil
}

func planComparison(repo string, r planRun, fetch bool) ([]string, error) {
	head, err := planGit(repo, "rev-parse", "HEAD")
	if err != nil {
		return nil, err
	}
	if head != r.Head {
		return nil, fmt.Errorf("checkout HEAD must equal tested_commit %s", r.Head)
	}
	if ref, _ := planGit(repo, "symbolic-ref", "-q", "HEAD"); ref != "" {
		return nil, fmt.Errorf("planner requires a detached tested checkout")
	}
	dirty, err := planGit(repo, "status", "--porcelain", "--untracked-files=all")
	if err != nil {
		return nil, err
	}
	if dirty != "" {
		return nil, fmt.Errorf("planner requires a clean checkout, including untracked source")
	}
	if fetch {
		// Pin both fetches to immutable IDs in the run's same repository.
		remote := "https://github.com/" + r.Repository + ".git"
		args := []string{"fetch", "--no-tags", remote, r.Base, r.Head}
		shallow, err := planGit(repo, "rev-parse", "--is-shallow-repository")
		if err != nil {
			return nil, err
		}
		if shallow == "true" {
			args = append([]string{"fetch", "--unshallow", "--no-tags", remote}, r.Base, r.Head)
		}
		if _, err := planGit(repo, args...); err != nil {
			return nil, fmt.Errorf("fetch comparison history: %w", err)
		}
	}
	for _, oid := range []string{r.Base, r.Head} {
		resolved, err := planGit(repo, "rev-parse", "--verify", oid+"^{commit}")
		if err != nil || resolved != oid {
			return nil, fmt.Errorf("comparison commit %s unavailable: %v", oid, err)
		}
	}
	if _, err := planGit(repo, "merge-base", "--is-ancestor", r.Base, r.Head); err != nil {
		return nil, fmt.Errorf("base is not a proven ancestor (fetch complete history): %w", err)
	}
	// --no-renames represents renames as delete+add, retaining both paths.
	raw, err := planGit(repo, "diff", "--no-renames", "--name-only", "-z", r.Base, r.Head, "--")
	if err != nil {
		return nil, err
	}
	files := []string{}
	for _, f := range strings.Split(raw, "\x00") {
		if f != "" {
			files = append(files, f)
		}
	}
	slices.Sort(files)
	return slices.Compact(files), nil
}

func buildSelection(r planRun, ref planReference, files []string, sel affected.Selection) (remoteSelection, error) {
	p := remoteSelection{Version: 1, Run: ref, Commit: r.Head, DiffMode: "ancestor-tree", Files: append([]string{}, files...), Sampled: []string{}, Algorithm: "sorted-round-robin-v1"}
	if err := r.validate(); err != nil {
		return p, err
	}
	all := cases.All()
	smoke, err := smokeCases(all)
	if err != nil {
		return p, err
	}
	selected := smoke
	if r.Tier == "full" {
		full, e := tierCases("full", "", "")
		if e != nil {
			return p, e
		}
		selected = full.Cases
	}
	if r.Tier == "land" {
		selected, err = landCases(all, sel)
		if err != nil {
			return p, err
		}
		p.Sampled = append(p.Sampled, sel.Sampled...)
	}
	slices.Sort(p.Sampled)
	slices.Sort(p.Files)
	p.Files = slices.Compact(p.Files)
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
	count := min(len(selected), r.Limits.Shards)
	for i := range count {
		p.Shards = append(p.Shards, plannedShard{ID: fmt.Sprintf("s%d", i+1), Cases: []string{}})
	}
	budgets := make([]time.Duration, count)
	for i, c := range selected {
		if c.Matrix {
			return p, fmt.Errorf("%s requires a separate matrix selection; selection cannot be truncated", c.Name)
		}
		row := plannedCase{Name: c.Name, Reasons: []string{}, FixtureOps: append([]string{}, c.FixtureOps()...), Roles: []string{"bridge"}, ModRole: "fixture", Rendered: c.Rendered}
		if c.Production {
			row.ModRole = "production"
		}
		if serveDriven(c) {
			row.Roles = append(row.Roles, "controller")
		}
		for _, s := range smoke {
			if s.Name == c.Name {
				row.Reasons = append(row.Reasons, "smoke")
			}
		}
		area, _, _ := strings.Cut(c.Name, "/")
		if r.Tier == "full" {
			row.Reasons = append(row.Reasons, "full")
		}
		if r.Tier == "land" {
			if slices.Contains(sel.Sampled, area) && !sel.AllHarnesses {
				row.Reasons = append(row.Reasons, "sampled:"+area)
			} else if sel.AllHarnesses || slices.Contains(sel.Cases, area) {
				row.Reasons = append(row.Reasons, "affected:"+area)
			}
		}
		slices.Sort(row.Reasons)
		slices.Sort(row.FixtureOps)
		if len(row.Reasons) == 0 || c.Budget <= 0 {
			return p, fmt.Errorf("%s lacks selection reason or budget", c.Name)
		}
		p.Cases = append(p.Cases, row)
		p.Shards[i%count].Cases = append(p.Shards[i%count].Cases, c.Name)
		budgets[i%count] += c.Budget * time.Duration(r.Limits.Attempts)
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
	fetch := fs.Bool("fetch", false, "fetch exact comparison history from run.repository")
	if err := fs.Parse(args); err != nil {
		return 2
	}
	fail := func(err error) int { fmt.Fprintln(stderr, err); return 2 }
	if fs.NArg() != 0 || !validPlanPath(*path) {
		return fail(fmt.Errorf("usage: acceptance plan -evidence <root> -run <relative run.json> [-fetch]"))
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
	repo, ok := repoOfCwd()
	if !ok {
		return fail(fmt.Errorf("planner requires a git checkout"))
	}
	files, err := planComparison(repo, r, *fetch)
	if err != nil {
		return fail(err)
	}
	var sel affected.Selection
	if r.Tier == "land" {
		sel, err = affected.Select(repo, files, r.Base)
		if err != nil {
			return fail(err)
		}
	}
	p, err := buildSelection(r, planReference{Path: *path, SHA256: fmt.Sprintf("%x", sha256.Sum256(raw))}, files, sel)
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
