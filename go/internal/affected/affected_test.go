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
	if sel.AllGo || sel.AllHarnesses || len(sel.Packages) != 0 || len(sel.Harnesses) != 0 {
		t.Errorf("docs change selected %+v", sel)
	}
}

func TestSelectGoModIsEverything(t *testing.T) {
	sel, err := Select(repo(t), []string{"go/go.mod"})
	if err != nil {
		t.Fatal(err)
	}
	if !sel.AllGo || !sel.AllHarnesses || !slices.Contains(sel.Harnesses, "upkeepaccept") {
		t.Errorf("go.mod change selected %+v", sel)
	}
}

func TestSelectNativeSourceIsEveryHarnessNoGo(t *testing.T) {
	sel, err := Select(repo(t), []string{"integrations/rimgovernor-native/src/Foo.cs"})
	if err != nil {
		t.Fatal(err)
	}
	if sel.AllGo || !sel.AllHarnesses || len(sel.Packages) != 0 || len(sel.Harnesses) < 10 {
		t.Errorf("native change selected %+v", sel)
	}
}

// A change to this package reaches the packages that import it (cmd/land,
// cmd/affected) and no harness, since no harness or the binary imports it;
// a change to nativeaccept itself reaches every harness.
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
	if len(sel.Harnesses) != 0 || sel.AllHarnesses {
		t.Errorf("harnesses selected for a tooling-only change: %v", sel.Harnesses)
	}

	sel, err = Select(r, []string{"go/internal/nativeaccept/clock.go"})
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Contains(sel.Harnesses, "upkeepaccept") || slices.Contains(sel.Harnesses, "verified") {
		t.Errorf("nativeaccept change selected harnesses %v", sel.Harnesses)
	}
	if sel.AllHarnesses {
		t.Errorf("a Go change is not a shared-input change")
	}

	// A harness's own file selects only that harness (plus its test package).
	sel, err = Select(r, []string{"go/internal/nativeaccept/cmd/upkeepaccept/main.go"})
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Equal(sel.Harnesses, []string{"upkeepaccept"}) {
		t.Errorf("harness-local change selected %v", sel.Harnesses)
	}
	if len(sel.Cases) != 0 {
		t.Errorf("harness-local change selected case areas %v", sel.Cases)
	}

	// A case area's own file selects only that area; the runner selects
	// every area.
	sel, err = Select(r, []string{"go/internal/nativeaccept/cases/light/light.go"})
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Equal(sel.Cases, []string{"light"}) || len(sel.Harnesses) != 0 {
		t.Errorf("area-local change selected cases %v harnesses %v", sel.Cases, sel.Harnesses)
	}
	sel, err = Select(r, []string{"go/internal/nativeaccept/cases/run.go"})
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Contains(sel.Cases, "light") || !slices.Contains(sel.Cases, "smoke") || len(sel.Harnesses) != 0 {
		t.Errorf("runner change selected cases %v harnesses %v", sel.Cases, sel.Harnesses)
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
