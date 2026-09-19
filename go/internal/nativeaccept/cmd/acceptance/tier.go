package main

// Tiers (#273) name the three shapes an acceptance pass takes, so the
// landing lane runs a fraction of the registry and the rest runs on its
// own cadence:
//
//   - land: the cases cmd/affected selects for the worktree's diff against
//     -base, plus the smoke set; the landing lane's fresh pass.
//   - full: every case outside the matrix tier; the nightly loop against
//     main, chained with -baseline for regression flagging.
//   - matrix: the cases that declare Matrix (speedmatrix, tickbudget, a
//     DLC-save case); on demand and whenever the clock scheduler or the
//     native tick path changes.
//   - smoke: the land tier's fixed half alone, the committed
//     suites/smoke.json: runner-proving bridge-only cases over a kept debug
//     game and one short serve-driven case.
//
// `acceptance list -tier <name>` prints a tier (and prices it with
// -cost); `acceptance suite -tier <name>` runs it and records the tier in
// the suite report.

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"os"
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

// tierCases resolves a tier to its registry cases in registry order. The
// land tier diffs repo's working tree against base.
func tierCases(tier, repo, base string) ([]cases.Case, error) {
	all := cases.All()
	switch tier {
	case "full":
		var out []cases.Case
		for _, c := range all {
			if !c.Matrix {
				out = append(out, c)
			}
		}
		return out, nil
	case "matrix":
		var out []cases.Case
		for _, c := range all {
			if c.Matrix {
				out = append(out, c)
			}
		}
		return out, nil
	case "smoke":
		return smokeCases(all)
	case "land":
		if repo == "" {
			return nil, fmt.Errorf("the land tier needs a git checkout to diff")
		}
		changed, err := affected.ChangedFiles(repo, base)
		if err != nil {
			return nil, err
		}
		sel, err := affected.Select(repo, changed)
		if err != nil {
			return nil, err
		}
		return landCases(all, sel)
	}
	return nil, fmt.Errorf("unknown tier %q (one of %s)", tier, strings.Join(tierNames, ", "))
}

// landCases is the affected areas' cases plus the smoke set, matrix cases
// excluded, in registry order.
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
	var out []cases.Case
	for _, c := range all {
		area, _, _ := strings.Cut(c.Name, "/")
		if c.Matrix {
			continue
		}
		if want[c.Name] || areas[area] || sel.AllHarnesses {
			out = append(out, c)
		}
	}
	return out, nil
}

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
