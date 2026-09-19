package dialog

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func TestPauseDeclaresServiceHookFixture(t *testing.T) {
	c, ok := cases.Lookup("dialog/pause")
	if !ok || !slices.Contains(c.FixtureOps(), fixtureTool) {
		t.Fatal("dialog/pause preflight must require its service hook fixture")
	}
}
