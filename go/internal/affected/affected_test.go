package affected

import (
	"os"
	"slices"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func repo(t *testing.T) string {
	t.Helper()
	if testing.Short() {
		t.Skip("runs go list over the module")
	}
	wd, _ := os.Getwd()
	repo, ok := na.FindRepo(wd)
	if !ok {
		t.Skip("not in a checkout")
	}
	return repo
}

func TestSelectNothingForDocs(t *testing.T) {
	sel, err := Select(repo(t), []string{"AGENTS.md", "docs/README.md"})
	if err != nil {
		t.Fatal(err)
	}
	if sel.AllGo || sel.AllHarnesses || sel.Probes || len(sel.Packages) != 0 || len(sel.Cases) != 0 {
		t.Errorf("docs change selected %+v", sel)
	}
}

func TestSelectGoModIsEverything(t *testing.T) {
	sel, err := Select(repo(t), []string{"go/go.mod"})
	if err != nil {
		t.Fatal(err)
	}
	if !sel.AllGo || !sel.AllHarnesses || !slices.Contains(sel.Cases, "authority") {
		t.Errorf("go.mod change selected %+v", sel)
	}
}

func TestSelectNativeSourceIsEveryCaseNoGo(t *testing.T) {
	sel, err := Select(repo(t), []string{"integrations/rimgovernor-native/src/Foo.cs"})
	if err != nil {
		t.Fatal(err)
	}
	if sel.AllGo || !sel.AllHarnesses || !sel.Probes || len(sel.Packages) != 0 || len(sel.Cases) < 10 {
		t.Errorf("native change selected %+v", sel)
	}
}

// The probes build compiles native sources against stubs under
// contracts/tests, so a stub or probe change owes the build and nothing
// else; a generated protocol class (a mod build input too) owes it as well.
func TestSelectProbeStubOwesProbesBuildOnly(t *testing.T) {
	r := repo(t)
	sel, err := Select(r, []string{"contracts/tests/NativeContractProbes/Shared/FakeVerseStub.cs"})
	if err != nil {
		t.Fatal(err)
	}
	if !sel.Probes || sel.AllGo || sel.AllHarnesses || len(sel.Packages) != 0 || len(sel.Cases) != 0 {
		t.Errorf("stub change selected %+v", sel)
	}
	if sel, err = Select(r, []string{"contracts/generated/protobuf/csharp/Clock.cs"}); err != nil {
		t.Fatal(err)
	}
	if !sel.Probes || !sel.AllHarnesses {
		t.Errorf("generated protocol change selected %+v", sel)
	}
}

// A change to this package reaches the packages that import it (cmd/land,
// cmd/affected) and no case, since no case area or the binary imports it;
// a change to nativeaccept itself reaches every case area.
func TestSelectFollowsImports(t *testing.T) {
	r := repo(t)
	sel, err := Select(r, []string{"go/internal/affected/affected.go"})
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{
		"github.com/davidarcher/RimGovernor/go/internal/affected",
		"github.com/davidarcher/RimGovernor/go/cmd/land",
		"github.com/davidarcher/RimGovernor/go/cmd/affected",
	} {
		if !slices.Contains(sel.Packages, want) {
			t.Errorf("packages lack %s: %v", want, sel.Packages)
		}
	}
	if len(sel.Cases) != 0 || sel.AllHarnesses {
		t.Errorf("cases selected for a tooling-only change: %v", sel.Cases)
	}

	// A harness helper package selects the areas importing it; the
	// harness package itself is scoped by object (harness_test.go).
	sel, err = Select(r, []string{"go/internal/nativeaccept/sustainedfood/failfast.go"})
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Contains(sel.Cases, "sustained") || slices.Contains(sel.Cases, "smoke") {
		t.Errorf("sustainedfood change selected cases %v", sel.Cases)
	}
	if sel.AllHarnesses {
		t.Errorf("a Go change is not a shared-input change")
	}

	// A case area's own file selects only that area; the runner selects
	// every area.
	sel, err = Select(r, []string{"go/internal/nativeaccept/cases/light/light.go"})
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Equal(sel.Cases, []string{"light"}) {
		t.Errorf("area-local change selected cases %v", sel.Cases)
	}
	sel, err = Select(r, []string{"go/internal/nativeaccept/cases/run.go"})
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Contains(sel.Cases, "light") || !slices.Contains(sel.Cases, "smoke") {
		t.Errorf("runner change selected cases %v", sel.Cases)
	}
}

// A fixture source affects the areas calling its ops (#170), a committed
// save the area loading it, and a fixture build file every area, none of
// them as a shared-input change.
func TestSelectScopesFixtures(t *testing.T) {
	r := repo(t)
	sel, err := Select(r, []string{"scripts/fixtures/DefenseFixture.cs", "scripts/fixtures/saves/RimGovernor-defense-layout.rws"})
	if err != nil {
		t.Fatal(err)
	}
	// tools/saveheadroom-defense-layout loads the committed defense save
	// too (#320).
	if sel.AllHarnesses || !slices.Equal(sel.Cases, []string{"defense", "tools"}) || len(sel.Packages) != 0 {
		t.Errorf("defense fixture change selected %+v", sel)
	}
	sel, err = Select(r, []string{"scripts/fixtures/GuardedConstructionFixture.cs"})
	if err != nil {
		t.Fatal(err)
	}
	if sel.AllHarnesses || !slices.Contains(sel.Cases, "construction") || !slices.Contains(sel.Cases, "defense") || slices.Contains(sel.Cases, "light") {
		t.Errorf("guarded construction fixture change selected %+v", sel)
	}
	sel, err = Select(r, []string{"scripts/fixtures/CombatFixtures.csproj"})
	if err != nil {
		t.Fatal(err)
	}
	if sel.AllHarnesses || !slices.Contains(sel.Cases, "light") || !slices.Contains(sel.Cases, "smoke") {
		t.Errorf("fixture build file change selected %+v", sel)
	}
	sel, err = Select(r, []string{"contracts/fixtures/colony-core.json"})
	if err != nil {
		t.Fatal(err)
	}
	if sel.AllHarnesses || len(sel.Cases) != 0 {
		t.Errorf("contract fixture change selected %+v", sel)
	}
}

func TestSelectIgnoresDeletedDirs(t *testing.T) {
	sel, err := Select(repo(t), []string{"go/internal/gone/x.go"})
	if err != nil {
		t.Fatal(err)
	}
	if len(sel.Packages) != 0 {
		t.Errorf("deleted package selected %v", sel.Packages)
	}
}
