package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/doctor"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/postmortem"
)

func absRoot() string {
	if runtime.GOOS == "windows" {
		return `C:\bridge`
	}
	return "/bridge"
}

func TestParseRunResolvesFlagsAndCases(t *testing.T) {
	root := absRoot()
	var stderr bytes.Buffer
	selected, opts, err := parseRun([]string{"smoke/identity", "-root", root, "-budget", "4m", "-stall", "90s", "-headless=false"}, &stderr)
	if err != nil {
		t.Fatalf("parseRun: %v (%s)", err, stderr.String())
	}
	if len(selected) != 1 || selected[0].Name != "smoke/identity" {
		t.Fatalf("selected = %v", selected)
	}
	if opts.Root != root || opts.Output != filepath.Join(root, "acceptance") {
		t.Fatalf("root/output = %q/%q", opts.Root, opts.Output)
	}
	if opts.Budget != 4*time.Minute || opts.Stall != 90*time.Second || opts.Headless || opts.GameID != "rimgovernor-trial" {
		t.Fatalf("opts = %+v", opts)
	}
	if got := opts.CaseOutput(selected[0]); got != filepath.Join(root, "acceptance", "smoke", "identity") {
		t.Fatalf("CaseOutput = %q", got)
	}
}

func TestParseRunRejects(t *testing.T) {
	root := absRoot()
	for name, args := range map[string][]string{
		"no case":         {"-root", root},
		"unknown case":    {"smoke/nope", "-root", root},
		"missing root":    {"smoke/identity"},
		"relative root":   {"smoke/identity", "-root", "bridge"},
		"case after flag": {"-root", root, "smoke/identity"},
		"unknown flag":    {"smoke/identity", "-root", root, "-bogus"},
	} {
		var stderr bytes.Buffer
		if _, _, err := parseRun(args, &stderr); err == nil {
			t.Errorf("%s: parseRun(%v) = nil", name, args)
		}
	}
}

// TestRegistryPassesLint is the checklist gate (#139): every registered
// case, from every area package the binary imports, satisfies cases.Lint.
// A case that breaks a rule fails here with the rule and the checklist
// item named.
func TestRegistryPassesLint(t *testing.T) {
	all := cases.All()
	if len(all) == 0 {
		t.Fatalf("no cases registered")
	}
	for _, c := range all {
		if err := c.Lint(); err != nil {
			t.Errorf("%v", err)
		}
	}
}

// TestLintNamesEachRule pins the messages a violating case gets, one per
// checklist rule, so a failing TestRegistryPassesLint reads as a fix.
func TestLintNamesEachRule(t *testing.T) {
	noop := func(context.Context, cases.Session) error { return nil }
	good := cases.Case{Name: "area/good", Start: cases.DebugStart{}, Budget: time.Minute, Run: noop}
	if err := good.Lint(); err != nil {
		t.Fatalf("good case: %v", err)
	}
	loud := good
	loud.Quiet, loud.Reason = na.Loud, "asserts a raid interrupts the plan"
	big := good
	big.Start, big.Reason = cases.DebugStart{Size: na.DebugStart{MapSize: 250, PlanetCoverage: 0.05}}, "excavation reasons about terrain beyond the default"
	served := good
	served.Start, served.Serve = cases.Save{Name: "colony"}, &cases.ServeSpec{}
	for name, c := range map[string]cases.Case{"loud with reason": loud, "big with reason": big, "serve on save": served} {
		if err := c.Lint(); err != nil {
			t.Errorf("%s: %v", name, err)
		}
	}
	for _, tc := range []struct {
		name string
		edit func(c *cases.Case)
		want string
		item string // the checklist item the message names; empty for a shape rule
	}{
		{"no budget", func(c *cases.Case) { c.Budget = 0 }, "Budget is missing", "checklist item 6"},
		{"budget over max", func(c *cases.Case) { c.Budget = 16 * time.Minute }, "exceeds 15m0s", "checklist item 6"},
		{"loud without reason", func(c *cases.Case) { c.Quiet = na.Loud }, "Quiet is loud without a Reason", "checklist item 4"},
		{"big start without reason", func(c *cases.Case) {
			c.Start = cases.DebugStart{Size: na.DebugStart{MapSize: 250, PlanetCoverage: 0.05}}
		}, "bigger than the default", "checklist item 2"},
		{"serve on debug start", func(c *cases.Case) { c.Serve = &cases.ServeSpec{} }, "Serve declared on a bare DebugStart", "checklist item 1"},
		{"name without area", func(c *cases.Case) { c.Name = "good" }, "not <area>/<case>", ""},
		{"name with upper case", func(c *cases.Case) { c.Name = "Area/Good" }, "not <area>/<case>", ""},
		{"no run", func(c *cases.Case) { c.Run = nil }, "has no Run", ""},
	} {
		c := good
		tc.edit(&c)
		err := c.Lint()
		if err == nil || !strings.Contains(err.Error(), tc.want) || !strings.Contains(err.Error(), tc.item) {
			t.Errorf("%s: Lint() = %v, want %q naming %q", tc.name, err, tc.want, tc.item)
		}
	}
	// Every violation is reported at once.
	c := good
	c.Budget, c.Quiet, c.Name = 0, na.Loud, "bad"
	err := c.Lint()
	for _, want := range []string{"Budget is missing", "without a Reason", "not <area>/<case>"} {
		if err == nil || !strings.Contains(err.Error(), want) {
			t.Errorf("Lint() = %v, missing %q", err, want)
		}
	}
	if errors.Unwrap(err) != nil {
		t.Errorf("joined error unwraps to one: %v", err)
	}
}

func TestListPrintsRegistry(t *testing.T) {
	var stdout, stderr bytes.Buffer
	if code := run([]string{"list"}, &stdout, &stderr); code != 0 {
		t.Fatalf("list exit %d: %s", code, stderr.String())
	}
	if !strings.Contains(stdout.String(), "smoke/identity\t") {
		t.Fatalf("list output: %q", stdout.String())
	}
	if code := run(nil, &stdout, &stderr); code != 2 {
		t.Fatalf("no command exit %d", code)
	}
}

func TestParseSetup(t *testing.T) {
	var stderr bytes.Buffer
	o, err := parseSetup([]string{"-worktree", absRoot(), "-fixture", "UpkeepFixture, ShutdownFixture", "-rebuild"}, &stderr)
	if err != nil {
		t.Fatalf("parseSetup: %v (%s)", err, stderr.String())
	}
	if o.repo != absRoot() || o.overrides.Repo != absRoot() || !o.run.Rebuild {
		t.Fatalf("options = %+v", o)
	}
	if strings.Join(o.run.Fixtures, ",") != "UpkeepFixture,ShutdownFixture" {
		t.Fatalf("fixtures = %v", o.run.Fixtures)
	}
	for name, args := range map[string][]string{
		"fixture and production": {"-worktree", absRoot(), "-fixture", "UpkeepFixture", "-production"},
		"positional":             {"-worktree", absRoot(), "smoke/identity"},
		"unknown flag":           {"-worktree", absRoot(), "-bogus"},
	} {
		if _, err := parseSetup(args, &stderr); err == nil {
			t.Errorf("%s: parseSetup(%v) = nil", name, args)
		}
	}
}

func TestWhyPrintsDigestForCaseDirectory(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "result.json"), []byte(`{"case":"a/b","error":"it broke","passed":false}`), 0644); err != nil {
		t.Fatal(err)
	}
	var stdout, stderr bytes.Buffer
	if code := run([]string{"why", dir}, &stdout, &stderr); code != 0 {
		t.Fatalf("why exit %d: %s", code, stderr.String())
	}
	if out := stdout.String(); !strings.HasPrefix(out, "case: a/b\nerror: it broke\n## revision\n") || !strings.Contains(out, "## pooled-job mismatches\n") {
		t.Fatalf("why output:\n%s", out)
	}
	stdout.Reset()
	if code := run([]string{"why", dir, "-json"}, &stdout, &stderr); code != 0 {
		t.Fatalf("why -json exit %d: %s", code, stderr.String())
	}
	var digest postmortem.Digest
	if err := json.Unmarshal(stdout.Bytes(), &digest); err != nil || digest.Case != "a/b" || len(digest.Sections) != 7 {
		t.Fatalf("why -json: %v %+v", err, digest)
	}
	if code := run([]string{"why", filepath.Join(dir, "missing")}, &stdout, &stderr); code != 1 || !strings.Contains(stderr.String(), "no result.json") {
		t.Fatalf("why on a missing directory: exit %d %s", code, stderr.String())
	}
	if code := run([]string{"why"}, &stdout, &stderr); code != 2 {
		t.Fatalf("why without a directory: exit %d", code)
	}
}

func TestListCostPricesCasesFromBaseline(t *testing.T) {
	baseline := filepath.Join(t.TempDir(), "result.json")
	if err := os.WriteFile(baseline, []byte(`{"cases":[{"name":"smoke/identity","wall_ms":15000,"boot_ms":5000}]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	var stdout, stderr bytes.Buffer
	if code := run([]string{"list", "-cost", "-baseline", baseline, "smoke/..."}, &stdout, &stderr); code != 0 {
		t.Fatalf("list -cost exit %d: %s", code, stderr.String())
	}
	out := stdout.String()
	if !regexp.MustCompile(`smoke/identity +15s +5s +Runner smoke`).MatchString(out) || !regexp.MustCompile(`smoke/dispatch +untimed +#227`).MatchString(out) {
		t.Errorf("list -cost rows:\n%s", out)
	}
	if !strings.Contains(out, "total: 2 cases, 15s wall (boot 5s), 1 untimed (baseline "+baseline+")") {
		t.Errorf("list -cost total:\n%s", out)
	}
	if strings.Contains(out, "light/") {
		t.Errorf("area filter leaked other areas:\n%s", out)
	}

	// Without a baseline every case is untimed and the total says why.
	stdout.Reset()
	if code := run([]string{"list", "-cost", "smoke/identity"}, &stdout, &stderr); code != 0 {
		t.Fatalf("list -cost exit %d: %s", code, stderr.String())
	}
	if out := stdout.String(); !regexp.MustCompile(`smoke/identity +untimed +Runner`).MatchString(out) || !strings.Contains(out, "total: 1 case, 1 untimed (no -baseline given)") {
		t.Errorf("no-baseline output:\n%s", out)
	}

	stdout.Reset()
	if code := run([]string{"list", "nosuch/case"}, &stdout, &stderr); code != 2 || !strings.Contains(stderr.String(), "unknown case") {
		t.Errorf("unknown name exit %d: %s", code, stderr.String())
	}
	if code := run([]string{"list", "-cost", "-baseline", filepath.Join(t.TempDir(), "missing.json")}, &stdout, &stderr); code != 2 {
		t.Errorf("missing baseline exit %d", code)
	}
}

func TestWarmRejectsBadRoots(t *testing.T) {
	for name, args := range map[string][]string{
		"missing root":  {},
		"relative root": {"-root", "bridge"},
		"unknown flag":  {"-root", absRoot(), "-bogus"},
	} {
		var stdout, stderr bytes.Buffer
		if code := warm(context.Background(), args, &stdout, &stderr); code != 2 {
			t.Errorf("%s: warm(%v) = %d, want 2 (%s)", name, args, code, stderr.String())
		}
	}
}

func TestDoctorRefusesBadFlagsAndFailsAnEmptyRoot(t *testing.T) {
	var stdout, stderr bytes.Buffer
	if code := run([]string{"doctor"}, &stdout, &stderr); code != 2 || !strings.Contains(stderr.String(), "-root is required") {
		t.Fatalf("no root: code %d, stderr %q", code, stderr.String())
	}
	stderr.Reset()
	if code := run([]string{"doctor", "-root", "relative"}, &stdout, &stderr); code != 2 || !strings.Contains(stderr.String(), "absolute") {
		t.Fatalf("relative root: code %d, stderr %q", code, stderr.String())
	}
	// An empty directory is a root setup has not made: the root check
	// fails with the setup command, and the exit code says so.
	stdout.Reset()
	if code := run([]string{"doctor", "-root", t.TempDir(), "-worktree", t.TempDir()}, &stdout, &stderr); code != 1 || !strings.Contains(stdout.String(), "FAIL  root") || !strings.Contains(stdout.String(), "acceptance setup") {
		t.Fatalf("empty root: code %d\n%s", code, stdout.String())
	}
	// `run` fronts the same checks and refuses before any game opens.
	stdout.Reset()
	selected, opts, err := parseRun([]string{"smoke/identity", "-root", t.TempDir()}, &stderr)
	if err != nil {
		t.Fatal(err)
	}
	if code := runCases(context.Background(), selected, opts, &stdout); code != 2 || !strings.Contains(stdout.String(), "preflight failed") || strings.Contains(stdout.String(), "ok    ") {
		t.Fatalf("run preflight: code %d\n%s", code, stdout.String())
	}
}

func TestHealableOnlyWhenEveryFailHasACode(t *testing.T) {
	stale := doctor.Check{Name: "mod", Status: doctor.Fail, Code: doctor.HealStaleMod}
	if got := healable([]doctor.Check{{Name: "root"}, stale}); len(got) != 1 || got[0] != doctor.HealStaleMod {
		t.Fatalf("healable = %v", got)
	}
	if got := healable([]doctor.Check{stale, {Name: "baseline", Status: doctor.Fail}}); got != nil {
		t.Fatalf("a codeless Fail must refuse the heal: %v", got)
	}
	if got := healable([]doctor.Check{{Name: "mod", Status: doctor.Warn, Code: doctor.HealStaleMod}}); got != nil {
		t.Fatalf("a warn is not healed: %v", got)
	}
}
