package main

import (
	"bytes"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// The smoke set stays a smoke set: bridge-only cases that read a kept
// game, exactly one short serve-driven case, nothing that ends the
// process, opens a window or belongs to the matrix tier.
func TestSmokeSuiteShape(t *testing.T) {
	smoke, err := smokeCases(cases.All())
	if err != nil {
		t.Fatal(err)
	}
	if len(smoke) == 0 || len(smoke) > 10 {
		t.Fatalf("smoke set has %d cases; it is a few minutes of runner proof, not a registry", len(smoke))
	}
	serve := 0
	for _, c := range smoke {
		if c.Serve != nil || c.Service {
			serve++
		}
		if c.Matrix || c.NoKeep || c.Rendered {
			t.Errorf("%s: matrix=%v nokeep=%v rendered=%v, none belongs in the smoke set", c.Name, c.Matrix, c.NoKeep, c.Rendered)
		}
		if c.Budget > 5*time.Minute {
			t.Errorf("%s: budget %s over the smoke set's 5m", c.Name, c.Budget)
		}
	}
	if serve != 1 {
		t.Errorf("smoke set has %d serve-driven cases, want exactly one", serve)
	}
}

func TestTierCases(t *testing.T) {
	nightly, err := tierCases("nightly")
	if err != nil {
		t.Fatal(err)
	}
	if len(nightly) != len(endToEnd) {
		t.Errorf("nightly tier has %d cases, endToEnd names %d", len(nightly), len(endToEnd))
	}
	for _, c := range nightly {
		if offTier(c.Name) {
			t.Errorf("nightly tier carries off-tier case %s", c.Name)
		}
	}
	for _, tier := range []string{"land", "full", "matrix", "bogus"} {
		if _, err := tierCases(tier); err == nil || !strings.Contains(err.Error(), "unknown tier") {
			t.Errorf("tier %q: got %v", tier, err)
		}
	}
}

func TestParseSuiteTierIsASelector(t *testing.T) {
	var stderr bytes.Buffer
	list, opts, err := parseSuite([]string{"-tier", "smoke", "-root", absRoot(), "-output", t.TempDir() + "/out"}, &stderr)
	if err != nil {
		t.Fatal(err)
	}
	if opts.Tier != "smoke" || len(list) == 0 {
		t.Errorf("tier %q, %d rows", opts.Tier, len(list))
	}
	_, _, err = parseSuite([]string{"-tier", "smoke", "-all", "-root", absRoot(), "-output", t.TempDir()}, &stderr)
	if err == nil || !strings.Contains(err.Error(), "exactly one") {
		t.Errorf("-tier with -all: got %v", err)
	}
	if !strings.Contains(suiteUsage, "-tier") {
		t.Error("suite usage does not mention -tier")
	}
}

func TestListTierPrintsTheTier(t *testing.T) {
	var stdout, stderr bytes.Buffer
	if code := list([]string{"-tier", "nightly"}, &stdout, &stderr); code != 0 {
		t.Fatalf("exit %d: %s", code, stderr.String())
	}
	if !strings.Contains(stdout.String(), "campaign/foothold") || strings.Contains(stdout.String(), "smoke/identity") {
		t.Errorf("nightly listing:\n%s", stdout.String())
	}
	if code := list([]string{"-tier", "nightly", "smoke/identity"}, &stdout, &stderr); code == 0 {
		t.Error("-tier with case names should be refused")
	}
}

func TestOffTierCasesAreRegisteredAndGeneratorsResolve(t *testing.T) {
	for _, name := range []string{"sustained/colony", "sustained/colony-loud", "sustained/food", "lifecycle/headless-soak",
		"medical/stable-patient", "speedmatrix/plain"} {
		if _, ok := cases.Lookup(name); !ok || !offTier(name) {
			t.Errorf("%s: registered=%v offTier=%v", name, ok, offTier(name))
		}
	}
	gens := generators()
	if len(gens["variantsave-all"]) != 10 {
		t.Errorf("variantsave-all = %v", gens["variantsave-all"])
	}
	for name, list := range gens {
		for _, c := range list {
			if _, ok := cases.Lookup(c); !ok {
				t.Errorf("setup generate %s names unknown case %s", name, c)
			}
		}
	}
}
