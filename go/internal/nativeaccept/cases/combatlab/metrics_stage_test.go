package combatlab

import (
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// Every metrics case stages once into a cacheable stage (#2837), and the
// record the code after the stage reads survives the sidecar's JSON.
func TestMetricsCasesDeclareTheStage(t *testing.T) {
	for _, name := range Names {
		c, ok := cases.Lookup("combatlab/metrics-" + name)
		if !ok {
			t.Fatalf("metrics-%s is not registered", name)
		}
		if len(c.Stages) != 1 || c.Stages[0] != metricsStage {
			t.Errorf("metrics-%s Stages = %v, want [%s]", name, c.Stages, metricsStage)
		}
		if !c.NoCheckpoint {
			t.Errorf("metrics-%s must stay out of the mid-fight ring", name)
		}
	}
	want := stagedRecord{Sides: map[string]string{"a": Colonist, "b": Hostile}, Center: Cell{X: 40, Z: 52}, Digest: "d1", Seed: 7}
	data, err := json.Marshal(map[string]any{metricsStagedKey: want})
	if err != nil {
		t.Fatal(err)
	}
	var state map[string]any
	if err := json.Unmarshal(data, &state); err != nil {
		t.Fatal(err)
	}
	raw, _ := json.Marshal(state[metricsStagedKey])
	var got stagedRecord
	if err := json.Unmarshal(raw, &got); err != nil {
		t.Fatal(err)
	}
	if got.Center != want.Center || got.Digest != want.Digest || got.Seed != want.Seed || got.Sides["b"] != Hostile {
		t.Fatalf("round trip %+v, want %+v", got, want)
	}
}
