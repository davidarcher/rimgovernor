package inputs

import (
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
)

func TestHarnessInputsRejectsPaths(t *testing.T) {
	for _, harness := range []string{"", "../x", "warmauthorityaccept", "cmd/acceptance", "no-such-area/case", "authority/../x"} {
		if _, err := HarnessInputs(t.TempDir(), harness); err == nil {
			t.Errorf("HarnessInputs(%q) accepted", harness)
		}
	}
}

// The inputs cover the case's area, the runner, the native sources and the
// fixtures every build carries, and ignore files outside the inputs, test
// files, other areas and the fixtures only other areas call (#170).
func TestHarnessInputsTrackInputs(t *testing.T) {
	if testing.Short() {
		t.Skip("runs go list")
	}
	wd, _ := os.Getwd()
	repo, ok := FindRepo(wd)
	if !ok {
		t.Skip("not in a checkout")
	}
	files, err := HarnessInputs(repo, "authority/warm")
	if err != nil {
		t.Fatal(err)
	}
	has := func(path string) bool {
		for _, file := range files {
			if file == path {
				return true
			}
		}
		return false
	}
	for _, want := range []string{
		"go/internal/nativeaccept/cases/authority/warm.go",
		"go/internal/nativeaccept/cmd/acceptance/main.go",
		"go/internal/nativeaccept/inputs/harness_inputs.go",
		"go/cmd/rimgovernor/main.go",
		"go/go.mod",
		"integrations/rimgovernor-native/README.md",
		"scripts/fixtures/CombatFixtures.csproj",
		"scripts/fixtures/FreezeNeedsFixture.cs",
		"scripts/fixtures/QuietStorytellerFixture.cs",
	} {
		if !has(want) {
			t.Errorf("inputs lack %s", want)
		}
	}
	for _, skip := range []string{
		"go/internal/nativeaccept/verified_test.go",
		"AGENTS.md",
		"go/internal/nativeaccept/cases/defense/defense.go",
		"scripts/fixtures/DefenseFixture.cs",
		"scripts/fixtures/saves/RimGovernor-defense-layout.rws",
		"contracts/fixtures/colony-core.json",
	} {
		if has(skip) {
			t.Errorf("inputs include non-input %s", skip)
		}
	}
	for _, file := range files {
		if strings.Contains(file, "\\") {
			t.Errorf("input %q is not forward-slashed", file)
		}
	}
}

// A case's inputs carry the fixture sources whose ops its area calls, the
// sources those mention, and the committed save it loads.
func TestHarnessInputsScopeFixtures(t *testing.T) {
	if testing.Short() {
		t.Skip("runs go list")
	}
	wd, _ := os.Getwd()
	repo, ok := FindRepo(wd)
	if !ok {
		t.Skip("not in a checkout")
	}
	files, err := HarnessInputs(repo, "defense/layout")
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{
		"go/internal/nativeaccept/cases/defense/defense.go",
		"scripts/fixtures/DefenseFixture.cs",
		"scripts/fixtures/GuardedConstructionFixture.cs",
		"scripts/fixtures/saves/RimGovernor-defense-layout.rws",
		"scripts/fixtures/saves/RimGovernor-defense-layout.checkpoint.json",
	} {
		if !slices.Contains(files, want) {
			t.Errorf("inputs lack %s", want)
		}
	}
	if slices.Contains(files, "scripts/fixtures/LightingFixture.cs") || slices.Contains(files, "go/internal/nativeaccept/cases/light/light.go") {
		t.Errorf("inputs include another area's fixture or package")
	}
}

// FixtureInputs follows tool names to sources, class mentions between
// sources, and save names to saves; build files come with every set.
func TestFixtureInputs(t *testing.T) {
	repo := t.TempDir()
	dir := filepath.Join(repo, filepath.FromSlash(FixtureRoot))
	if err := os.MkdirAll(filepath.Join(dir, "saves"), 0755); err != nil {
		t.Fatal(err)
	}
	write := func(name, body string) {
		if err := os.WriteFile(filepath.Join(dir, filepath.FromSlash(name)), []byte(body), 0644); err != nil {
			t.Fatal(err)
		}
	}
	write("Fixtures.csproj", "<Project/>")
	write("AFixture.cs", `[Tool("test/a_prepare")] void A() { BHelper.Do(); }`)
	write("BHelper.cs", `static class BHelper {}`)
	write("CFixture.cs", `[Tool("test/c_prepare")] void C() {}`)
	write("saves/Colony-a.rws", "")
	write("saves/Colony-a.checkpoint.json", "{}")
	write("saves/Colony-c.rws", "")
	got, err := FixtureInputs(repo, FixtureRefs{"test/a_prepare": true, "Colony-a": true, "test/": true})
	if err != nil {
		t.Fatal(err)
	}
	want := []string{
		"scripts/fixtures/AFixture.cs",
		"scripts/fixtures/BHelper.cs",
		"scripts/fixtures/Fixtures.csproj",
		"scripts/fixtures/saves/Colony-a.checkpoint.json",
		"scripts/fixtures/saves/Colony-a.rws",
	}
	if !slices.Equal(got, want) {
		t.Errorf("FixtureInputs = %v, want %v", got, want)
	}
}

func TestWithoutOtherAreas(t *testing.T) {
	goDir := filepath.Join("repo", "go")
	cases := filepath.Join(goDir, "internal", "nativeaccept", "cases")
	dirs := []string{
		filepath.Join(cases, "light"),
		filepath.Join(cases, "defense"),
		filepath.Join(cases, "defense", "sub"),
		cases,
		filepath.Join(goDir, "internal", "nativeaccept"),
		filepath.Join(goDir, "internal", "nativeaccept", "casesx"),
	}
	got := WithoutOtherAreas(goDir, "defense", dirs)
	want := []string{
		filepath.Join(cases, "defense"),
		filepath.Join(cases, "defense", "sub"),
		cases,
		filepath.Join(goDir, "internal", "nativeaccept"),
		filepath.Join(goDir, "internal", "nativeaccept", "casesx"),
	}
	if !slices.Equal(got, want) {
		t.Errorf("WithoutOtherAreas = %v, want %v", got, want)
	}
}
