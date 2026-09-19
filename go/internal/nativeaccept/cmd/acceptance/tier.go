package main

// Tiers (#273) name the three shapes an acceptance pass takes, so the
// landing lane runs a fraction of the registry and the rest runs on its
// own cadence:
//
//   - land: the cases cmd/affected selects for the worktree's diff against
//     -base, plus the smoke set; on demand before a landing the author wants
//     proven (#387). An area a
//     harness change reaches through plumbing alone is sampled: one case
//     (#348).
//   - full: every case outside the matrix tier; the nightly loop against
//     main on CI, chained with -baseline for regression flagging (#387).
//   - matrix: the cases that declare Matrix (speedmatrix, tickbudget, a
//     DLC-save case); on demand and whenever the clock scheduler or the
//     native tick path changes.
//   - smoke: the land tier's fixed half alone, the committed
//     suites/smoke.json: runner-proving bridge-only cases over a kept debug
//     game and one short serve-driven case; the landing lane's pass (#387).
//
// `acceptance list -tier <name>` prints a tier (and prices it with
// -cost); `acceptance suite -tier <name>` runs it and records the tier in
// the suite report.

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"os"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/affected"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// repoOfCwd is the checkout enclosing the working directory, "" outside
// one (only the land tier needs it).
func repoOfCwd() (string, bool) {
	cwd, err := os.Getwd()
	if err != nil {
		return "", false
	}
	return na.FindRepo(cwd)
}

// tierNames are the tiers in the order the usage lists them.
var tierNames = []string{"land", "full", "matrix", "smoke"}

// tierSet is a resolved tier: its registry cases in registry order and,
// for the land tier, the areas it sampled to one case (#348).
type tierSet struct {
	Cases   []cases.Case
	Sampled []string
}

// tierCases resolves a tier. The land tier diffs repo's working tree
// against base.
func tierCases(tier, repo, base string) (tierSet, error) {
	all := cases.All()
	switch tier {
	case "full":
		var out []cases.Case
		for _, c := range all {
			if !c.Matrix {
				out = append(out, c)
			}
		}
		return tierSet{Cases: out}, nil
	case "matrix":
		var out []cases.Case
		for _, c := range all {
			if c.Matrix {
				out = append(out, c)
			}
		}
		return tierSet{Cases: out}, nil
	case "smoke":
		smoke, err := smokeCases(all)
		return tierSet{Cases: smoke}, err
	case "land":
		if repo == "" {
			return tierSet{}, fmt.Errorf("the land tier needs a git checkout to diff")
		}
		changed, err := affected.ChangedFiles(repo, base)
		if err != nil {
			return tierSet{}, err
		}
		sel, err := affected.Select(repo, changed, base)
		if err != nil {
			return tierSet{}, err
		}
		land, err := landCases(all, sel)
		return tierSet{Cases: land, Sampled: sel.Sampled}, err
	}
	return tierSet{}, fmt.Errorf("unknown tier %q (one of %s)", tier, strings.Join(tierNames, ", "))
}

// landCases is the affected areas' cases plus the smoke set, matrix cases
// excluded, in registry order. An area the selection sampled (a harness
// change reaching it through plumbing alone, #348) contributes one case,
// its cheapest by budget, a bridge-only case before a serve-driven one at
// the same budget; the full tier runs the rest.
func landCases(all []cases.Case, sel affected.Selection) ([]cases.Case, error) {
	smoke, err := smokeCases(all)
	if err != nil {
		return nil, err
	}
	want := map[string]bool{}
	for _, c := range smoke {
		want[c.Name] = true
	}
	areas := map[string]bool{}
	for _, area := range sel.Cases {
		areas[area] = true
	}
	sampled := map[string]bool{}
	for _, area := range sel.Sampled {
		sampled[area] = true
	}
	for _, c := range sampleCases(all, sel.Sampled) {
		want[c.Name] = true
	}
	var out []cases.Case
	for _, c := range all {
		area, _, _ := strings.Cut(c.Name, "/")
		// The fifteen-day stability gate belongs to the nightly full tier only.
		if c.Matrix || c.Name == "sustained/colony-stable" {
			continue
		}
		if want[c.Name] || (areas[area] && !sampled[area]) || sel.AllHarnesses {
			out = append(out, c)
		}
	}
	return out, nil
}

// sampleCases picks one non-matrix case per named area: the smallest
// budget, a bridge-only case before a serve-driven one, registry order
// last. An area with matrix cases only contributes nothing.
func sampleCases(all []cases.Case, areas []string) []cases.Case {
	pick := map[string]cases.Case{}
	for _, c := range all {
		area, _, _ := strings.Cut(c.Name, "/")
		if c.Matrix || c.Name == "sustained/colony-stable" || !slices.Contains(areas, area) {
			continue
		}
		best, ok := pick[area]
		if !ok || c.Budget < best.Budget || c.Budget == best.Budget && serveDriven(best) && !serveDriven(c) {
			pick[area] = c
		}
	}
	var out []cases.Case
	for _, c := range all {
		if best, ok := pick[strings.Split(c.Name, "/")[0]]; ok && best.Name == c.Name {
			out = append(out, c)
		}
	}
	return out
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
