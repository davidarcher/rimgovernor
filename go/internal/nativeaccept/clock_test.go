package nativeaccept

import (
	"testing"
)

func clockHealthyPawnFixture(field string) map[string]any {
	pawn := map[string]any{
		"pawn": map[string]any{"id": "pawn"}, "dead": false, "downed": false,
		"health": map[string]any{"bleeding": false, "needsTend": false},
	}
	switch field {
	case "dead", "downed":
		pawn[field] = true
	case "bleeding", "needsTend":
		health, _ := AsMap(pawn["health"])
		health[field] = true
	case "unknown":
		health, _ := AsMap(pawn["health"])
		delete(health, "needsTend")
	}
	// The reply as the game returns it for filter colonist=true: pawns and
	// completeness directly under observed, filtered counting non-colonists.
	return map[string]any{"observed": map[string]any{
		"pawns": []any{pawn},
		"completeness": map[string]any{
			"filtered": "1",
		},
	}}
}

func TestClockFixtureReportsMedicalPrerequisiteBeforeRunning(t *testing.T) {
	for _, field := range []string{"", "dead", "downed", "bleeding", "needsTend", "unknown"} {
		t.Run(field, func(t *testing.T) {
			reply := clockHealthyPawnFixture(field)
			err := RequireHealthyColonists(reply)
			if field == "" {
				if err != nil {
					t.Fatalf("unexpected error: %v", err)
				}
				return
			}
			if err == nil {
				t.Fatalf("expected a prerequisite error for field %q", field)
			}
		})
	}
}
