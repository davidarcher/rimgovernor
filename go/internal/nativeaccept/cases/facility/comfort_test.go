package facility

import (
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// A provisioned sample must rank comfort at least as deep as the mood
// pressure; a fractional deficit equal to the pressure counts as the raise.
func TestAuditProvision(t *testing.T) {
	t.Parallel()
	sample := func(pressure any, development map[string]any) map[string]any {
		s := map[string]any{"review_revision": uint64(7)}
		if pressure != nil {
			s["mood_provision"] = pressure
		}
		if development != nil {
			s["development"] = development
		}
		return s
	}
	report := na.Report{"timeline": []map[string]any{
		sample(nil, map[string]any{"deficit": .5}),
		sample(.75, map[string]any{"deficit": .75}),
		sample(1.0, map[string]any{"deficit": 1.0}),
		sample(.5, map[string]any{"deficit": 1.0}),
		sample(.25, nil),
	}}
	if err := auditProvision(report); err != nil {
		t.Fatal(err)
	}
	got, _ := report["mood_provision"].(map[string]any)
	if got["provisioned_samples"] != 4 || got["raised_samples"] != 1 || got["max_pressure"] != 1.0 {
		t.Fatalf("mood_provision = %v", got)
	}
	for name, timeline := range map[string][]map[string]any{
		"ranked below pressure":  {sample(.75, map[string]any{"deficit": .5})},
		"ranked without deficit": {sample(.75, map[string]any{"reason": "startup_survival"})},
	} {
		if err := auditProvision(na.Report{"timeline": timeline}); err == nil {
			t.Errorf("%s: accepted", name)
		}
	}
}
