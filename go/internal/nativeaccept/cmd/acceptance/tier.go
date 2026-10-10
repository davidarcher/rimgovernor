package main

// Tiers name the two shapes an acceptance pass takes:
//
//  - smoke: the committed suites/smoke.json: runner-proving bridge-only
//   cases over a kept debug game and one short serve-driven case.
//  - nightly: the end-to-end cases; the scheduled loop
//  against main on CI, a signal rather than a gate.
//
// Every other case runs on demand by name (acceptance run, or the remote
// workflow's cases tier). `acceptance list -tier <name>` prints a tier;
// `acceptance suite -tier <name>` runs it and records the tier in the suite
// report.

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"os"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// repoOfCwd is the checkout enclosing the working directory, "" outside one.
func repoOfCwd() (string, bool) {
	cwd, err := os.Getwd()
	if err != nil {
		return "", false
	}
	return na.FindRepo(cwd)
}

// tierNames are the tiers in the order the usage lists them.
var tierNames = []string{"nightly", "smoke"}

// tierCases resolves a tier to its registry cases in registry order.
func tierCases(tier string) ([]cases.Case, error) {
	all := tiered()
	switch tier {
	case "nightly":
		var out []cases.Case
		for _, c := range all {
			if endToEnd[c.Name] {
				out = append(out, c)
			}
		}
		return out, nil
	case "smoke":
		return smokeCases(all)
	}
	return nil, fmt.Errorf("unknown tier %q (one of %s)", tier, strings.Join(tierNames, ", "))
}

// serveDriven reports whether a case hosts rimgovernor serve.
func serveDriven(c cases.Case) bool { return c.Serve != nil || c.Service }

// smokeSuite is the committed smoke set (suites/smoke.json, embedded so
// the tier resolves from any working directory): bridge-only cases over a
// kept debug game plus one short serve-driven case (light/dark, so the
// build carries LightingFixture), a few minutes on two workers. Every row
// runs on any fixture build: none needs another fixture class or asserts
// a production discovery (rooms/reads and supplies/reads do, research/reads
// needs ResearchFixture). TestSmokeSuiteShape holds the shape; extend it
// with a case that proves a runner path the others miss, not per area.
//
//go:embed suites/smoke.json
var smokeSuite []byte

// smokeCases resolves the embedded smoke suite to registry cases in
// registry order.
func smokeCases(all []cases.Case) ([]cases.Case, error) {
	var rows []entry
	if err := json.Unmarshal(smokeSuite, &rows); err != nil {
		return nil, fmt.Errorf("suites/smoke.json: %w", err)
	}
	want := map[string]bool{}
	for _, r := range rows {
		if _, ok := cases.Lookup(r.Name); !ok {
			return nil, fmt.Errorf("suites/smoke.json names unknown case %q", r.Name)
		}
		want[r.Name] = true
	}
	var out []cases.Case
	for _, c := range all {
		if want[c.Name] {
			out = append(out, c)
		}
	}
	return out, nil
}

// endToEnd is the nightly tier: whole-colony
// proofs, a signal rather than a gate.
var endToEnd = map[string]bool{
	"combatlab/native-hold": true,
	"campaign/foothold":     true, "campaign/recovery": true, "clearance/shrine-breach": true,
	"defense/perimeter": true, "food/reserve": true, "food/starving-tribal-recovery": true, "production/ladder": true,
	"shelter/bunks-first": true, "startup/labor": true,
	"shelter/retirement": true, "shelter/climate-mild": true, "shelter/climate-cold": true, "shelter/climate-hot": true,
	"sustained/colony-stable": true, "sustained/winter": true, "upkeep/campaign": true, "sleeping/suites": true,
	"layout/ring": true, "layout/rich-soil": true,
	"mood/headroom": true, "mood/ledger": true, "mood/gathering": true,
}

// offTier names the cases no tier runs: fixture generators, which
// `acceptance setup generate` runs, and diagnostics, which gate nothing
// and stay runnable by hand with `acceptance run`.
func offTier(name string) bool {
	switch name {
	case "sustained/colony", "sustained/colony-loud", "sustained/food", "lifecycle/headless-soak",
		"medical/stable-patient", "review/colony-week":
		return true
	}
	return strings.HasPrefix(name, "tools/") || strings.HasPrefix(name, "sustained/matrix-") ||
		strings.HasPrefix(name, "speedmatrix/")
}

// tiered is the registry less the off-tier cases, in registry order.
func tiered() []cases.Case {
	var out []cases.Case
	for _, c := range cases.All() {
		if !offTier(c.Name) {
			out = append(out, c)
		}
	}
	return out
}
