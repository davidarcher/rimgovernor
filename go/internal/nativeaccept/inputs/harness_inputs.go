package inputs

import (
	"path/filepath"
	"sort"
	"strings"
)

// harnessSharedInputs are the checked-in inputs every acceptance run
// depends on besides its Go packages and the native mod build
// (nativeSourceInputs, the same list RequireCurrentPackage compares). The
// test fixtures (FixtureRoot) are not shared: FixtureInputs scopes them to
// the cases that call their ops or load their saves (#170), and the
// contract fixtures (contracts/fixtures) feed unit tests only.
var harnessSharedInputs = []string{
	"go/go.mod",
	"go/go.sum",
}

// WithoutOtherAreas drops from dirs (absolute package directories) the
// case area packages under go/internal/nativeaccept/cases other than
// area's: the runner imports every area to register it, but a case only
// runs its own.
func WithoutOtherAreas(goDir, area string, dirs []string) []string {
	casesDir := filepath.Join(goDir, "internal", "nativeaccept", "cases")
	var kept []string
	for _, dir := range dirs {
		rel, err := filepath.Rel(casesDir, dir)
		if err == nil && rel != "." && !strings.HasPrefix(rel, "..") && strings.Split(filepath.ToSlash(rel), "/")[0] != area {
			continue
		}
		kept = append(kept, dir)
	}
	return kept
}

// HarnessInputRoots lists, repo-relative with forward slashes, the files
// and directories outside a case's Go packages that every acceptance run
// depends on: the native mod's build inputs (less the test fixtures,
// which FixtureInputs scopes per case) and harnessSharedInputs. A change
// under any of them affects every case.
func HarnessInputRoots() []string {
	roots := make([]string, 0, len(nativeSourceInputs)+len(harnessSharedInputs))
	for _, input := range nativeSourceInputs {
		if input.repo == FixtureRoot {
			continue
		}
		roots = append(roots, input.repo)
	}
	roots = append(roots, harnessSharedInputs...)
	sort.Strings(roots)
	return roots
}
