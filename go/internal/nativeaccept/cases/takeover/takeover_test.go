package takeover

import "testing"

func TestProvenanceHolds(t *testing.T) {
	for _, reason := range []string{"foreign_designation", "player_owned", "foreign_bill", "player_schedule", "player_excluded"} {
		if err := noProvenanceHold(map[string]any{"goal": map[string]any{"hold": reason}}); err == nil {
			t.Fatalf("accepted %s", reason)
		}
	}
	for _, reason := range []string{"roof_blocker", "stale_snapshot", "emergency", "no_work"} {
		if err := noProvenanceHold(map[string]any{"hold": reason}); err != nil {
			t.Fatalf("native safety %s: %v", reason, err)
		}
	}
}
