package nativeaccept

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// The player row (#627) passes inside its bounds and names each bound a run
// breaks.
func TestPlayerRowProblems(t *testing.T) {
	good := PlayerRow{HazardGaps: []bridge.HazardGap{{HazardClass: "fire", MaxTickGap: 250, BoundTicks: 300}},
		SpeedChanges: 4, Ticks: 12000, PacedFrames: 1000, OverBudget: 20, LastPacingReason: "accelerated",
		Dispatch: bridge.Quantiles{Samples: 50, P95: 12}}
	if problems := PlayerRowProblems(good); len(problems) != 0 {
		t.Fatal(problems)
	}
	bad := good
	bad.HazardGaps = []bridge.HazardGap{{HazardClass: "fire", MaxTickGap: 400, BoundTicks: 300}}
	bad.OverBudget, bad.SpeedChanges, bad.LastPacingReason = 100, 40, "ceiling"
	bad.Dispatch.P95 = 80
	problems := strings.Join(PlayerRowProblems(bad), "\n")
	for _, want := range []string{"hazard fire", "exceeded the frame budget", "oscillate", "not the accelerated rate", "past the 30ms frame budget"} {
		if !strings.Contains(problems, want) {
			t.Fatalf("missing %q in:\n%s", want, problems)
		}
	}
	if problems := PlayerRowProblems(PlayerRow{}); len(problems) == 0 {
		t.Fatal("an empty row passed")
	}
	if _, err := ParseSpeedCases("player"); err != nil {
		t.Fatal(err)
	}
}
