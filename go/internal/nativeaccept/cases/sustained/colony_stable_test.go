package sustained

import (
	"strings"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
)

func TestStableTimeline(t *testing.T) {
	report := na.Report{
		"window": &sustainedfood.TickWindow{Sampled: true, FirstTick: 300000},
		"timeline": []map[string]any{
			{"tick": uint64(300000), "colony": map[string]any{"colonists": 8.0, "foodRunwayDays": 2.0}},
			{"tick": uint64(360000), "colony": map[string]any{"error": "unavailable"}},
			{"tick": uint64(420000), "colony": map[string]any{"colonists": 7.0, "foodRunwayDays": 0.1}},
			{"tick": uint64(480000), "colony": map[string]any{"colonists": 6.0, "foodRunwayDays": 1.0}},
		},
	}
	got := stableTimeline(report)
	for _, want := range []string{"loss on day 3 (tick 420000, 8 -> 7)", "runway 0.1 days on day 3 (tick 420000)"} {
		if !strings.Contains(got, want) {
			t.Fatalf("%q missing %q", got, want)
		}
	}
	if got := stableTimeline(na.Report{}); !strings.Contains(got, "day unavailable") {
		t.Fatal(got)
	}
}
