package inputs

import (
	"os"
	"strings"
	"testing"
)

func TestHarnessInputsRejectsPaths(t *testing.T) {
	for _, harness := range []string{"", "../x", "cmd/upkeepaccept", "no-such-harness"} {
		if _, err := HarnessInputs(t.TempDir(), harness); err == nil {
			t.Errorf("HarnessInputs(%q) accepted", harness)
		}
	}
}

// The inputs cover the harness's own package and the native sources, and
// ignore files outside the inputs and test files.
func TestHarnessInputsTrackInputs(t *testing.T) {
	if testing.Short() {
		t.Skip("runs go list")
	}
	wd, _ := os.Getwd()
	repo, ok := FindRepo(wd)
	if !ok {
		t.Skip("not in a checkout")
	}
	files, err := HarnessInputs(repo, "upkeepaccept")
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
		"go/internal/nativeaccept/cmd/upkeepaccept/main.go",
		"go/internal/nativeaccept/inputs/harness_inputs.go",
		"go/cmd/rimgovernor/main.go",
		"go/go.mod",
		"integrations/rimgovernor-native/README.md",
	} {
		if !has(want) {
			t.Errorf("inputs lack %s", want)
		}
	}
	if has("go/internal/nativeaccept/verified_test.go") || has("AGENTS.md") {
		t.Errorf("inputs include non-inputs")
	}
	for _, file := range files {
		if strings.Contains(file, "\\") {
			t.Errorf("input %q is not forward-slashed", file)
		}
	}
}
