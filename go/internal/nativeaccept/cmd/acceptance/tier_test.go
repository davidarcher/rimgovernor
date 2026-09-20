package main

import (
	"bytes"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/affected"
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

func TestTierCasesSplitTheRegistry(t *testing.T) {
	all := cases.All()
	fullSet, err := tierCases("full", "", "main")
	if err != nil {
		t.Fatal(err)
	}
	matrixSet, err := tierCases("matrix", "", "main")
	if err != nil {
		t.Fatal(err)
	}
	full, matrix := fullSet.Cases, matrixSet.Cases
	if len(full)+len(matrix) != len(all) {
		t.Errorf("full (%d) + matrix (%d) != registry (%d)", len(full), len(matrix), len(all))
	}
	for _, c := range full {
		if c.Matrix {
			t.Errorf("full tier carries matrix case %s", c.Name)
		}
	}
	names := map[string]bool{}
	for _, c := range matrix {
		names[c.Name] = true
		if !c.Matrix {
			t.Errorf("matrix tier carries %s, which does not declare Matrix", c.Name)
		}
	}
	for _, want := range []string{"speedmatrix/plain", "tickbudget/boundaries"} {
		if !names[want] {
			t.Errorf("matrix tier lacks %s", want)
		}
	}
	if _, err := tierCases("nightly", "", "main"); err == nil || !strings.Contains(err.Error(), "unknown tier") {
		t.Errorf("unknown tier: got %v", err)
	}
	if _, err := tierCases("land", "", "main"); err == nil {
		t.Error("land tier outside a checkout should fail")
	}
}

func TestLandCasesAreAffectedAreasPlusSmoke(t *testing.T) {
	all := cases.All()
	smoke, err := smokeCases(all)
	if err != nil {
		t.Fatal(err)
	}
	land, err := landCases(all, affected.Selection{Cases: []string{"power", "speedmatrix"}})
	if err != nil {
		t.Fatal(err)
	}
	got := map[string]bool{}
	for _, c := range land {
		got[c.Name] = true
		area, _, _ := strings.Cut(c.Name, "/")
		if c.Matrix {
			t.Errorf("land tier carries matrix case %s", c.Name)
		}
		if area != "power" && !isSmoke(smoke, c.Name) {
			t.Errorf("land tier carries %s, neither affected nor smoke", c.Name)
		}
	}
	for _, want := range []string{"power/fuel", "power/reserve", "smoke/identity", "light/dark"} {
		if !got[want] {
			t.Errorf("land tier lacks %s", want)
		}
	}
	// A shared input changed: every non-matrix case except the nightly gate.
	land, err = landCases(all, affected.Selection{AllHarnesses: true})
	if err != nil {
		t.Fatal(err)
	}
	full, _ := tierCases("full", "", "main")
	if len(land) != len(full.Cases)-2 {
		t.Errorf("all harnesses affected: land has %d cases, full %d", len(land), len(full.Cases))
	}
}

func TestColonyStableNightlyOnly(t *testing.T) {
	const name = "sustained/colony-stable"
	for _, tier := range []string{"full", "smoke", "matrix"} {
		set, err := tierCases(tier, "", "main")
		if err != nil {
			t.Fatal(err)
		}
		found := false
		for _, c := range set.Cases {
			found = found || c.Name == name
		}
		if found != (tier == "full") {
			t.Errorf("%s contains stable gate: %v", tier, found)
		}
	}
	for _, sel := range []affected.Selection{{AllHarnesses: true}, {Cases: []string{"sustained"}}, {Cases: []string{"sustained"}, Sampled: []string{"sustained"}}} {
		land, err := landCases(cases.All(), sel)
		if err != nil {
			t.Fatal(err)
		}
		for _, c := range land {
			if c.Name == name {
				t.Fatalf("land contains nightly gate for %+v", sel)
			}
		}
	}
}

func TestRecreationNightlyOnly(t *testing.T) {
	const name = "mood/recreation"
	for _, tier := range []string{"full", "smoke", "matrix"} {
		set, err := tierCases(tier, "", "main")
		if err != nil {
			t.Fatal(err)
		}
		found := false
		for _, c := range set.Cases {
			found = found || c.Name == name
		}
		if found != (tier == "full") {
			t.Errorf("%s contains recreation gate: %v", tier, found)
		}
	}
	for _, sel := range []affected.Selection{{AllHarnesses: true}, {Cases: []string{"mood"}}, {Cases: []string{"mood"}, Sampled: []string{"mood"}}} {
		land, err := landCases(cases.All(), sel)
		if err != nil {
			t.Fatal(err)
		}
		for _, c := range land {
			if c.Name == name {
				t.Fatalf("land contains nightly gate for %+v", sel)
			}
		}
	}
}

// A sampled area (#348) contributes its cheapest non-matrix case, a
// bridge-only one before a serve-driven one at the same budget; an area
// selected outright still runs whole.
func TestLandCasesSampleAnArea(t *testing.T) {
	all := cases.All()
	land, err := landCases(all, affected.Selection{Cases: []string{"power", "lifecycle"}, Sampled: []string{"lifecycle"}})
	if err != nil {
		t.Fatal(err)
	}
	var power, lifecycle []cases.Case
	for _, c := range land {
		switch area, _, _ := strings.Cut(c.Name, "/"); area {
		case "power":
			power = append(power, c)
		case "lifecycle":
			lifecycle = append(lifecycle, c)
		}
	}
	if len(lifecycle) != 1 {
		t.Fatalf("sampled area lifecycle contributes %d cases, want 1: %v", len(lifecycle), lifecycle)
	}
	for _, c := range all {
		if area, _, _ := strings.Cut(c.Name, "/"); area != "lifecycle" || c.Matrix || c.Name == lifecycle[0].Name {
			continue
		}
		if c.Budget < lifecycle[0].Budget || c.Budget == lifecycle[0].Budget && serveDriven(lifecycle[0]) && !serveDriven(c) {
			t.Errorf("sampled %s (budget %s, serve %v) over cheaper %s (budget %s, serve %v)", lifecycle[0].Name, lifecycle[0].Budget, serveDriven(lifecycle[0]), c.Name, c.Budget, serveDriven(c))
		}
	}
	var wholePower int
	for _, c := range all {
		if area, _, _ := strings.Cut(c.Name, "/"); area == "power" && !c.Matrix {
			wholePower++
		}
	}
	if len(power) != wholePower {
		t.Errorf("power selected outright runs %d of %d cases", len(power), wholePower)
	}
}

func isSmoke(smoke []cases.Case, name string) bool {
	for _, c := range smoke {
		if c.Name == name {
			return true
		}
	}
	return false
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
	if code := list([]string{"-tier", "matrix"}, &stdout, &stderr); code != 0 {
		t.Fatalf("exit %d: %s", code, stderr.String())
	}
	if !strings.Contains(stdout.String(), "speedmatrix/plain") || strings.Contains(stdout.String(), "smoke/identity") {
		t.Errorf("matrix listing:\n%s", stdout.String())
	}
	if code := list([]string{"-tier", "matrix", "smoke/identity"}, &stdout, &stderr); code == 0 {
		t.Error("-tier with case names should be refused")
	}
}
