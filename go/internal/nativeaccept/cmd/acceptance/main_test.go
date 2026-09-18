package main

import (
	"bytes"
	"context"
	"errors"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
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
