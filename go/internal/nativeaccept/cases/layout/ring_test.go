package layout

import "testing"

// layout/ring (run 36997676121): the watch ended at tick 23 the
// moment the expansion step was planned, so the audit read the ring as
// BlocksSlate blueprints and no finished wall; the planned rows count.
func TestStoneRingCountsPlannedBlueprints(t *testing.T) {
	audit := map[string]any{
		"walls": []any{map[string]any{"def": "Wall", "stuff": "WoodLog", "x": 96.0, "z": 180.0}},
		"planned": []any{
			map[string]any{"def": "Door", "frame": false, "stuff": "BlocksSlate", "x": 112.0, "z": 194.0},
			map[string]any{"def": "Wall", "frame": false, "stuff": "BlocksSlate", "x": 108.0, "z": 194.0},
			map[string]any{"def": "Wall", "frame": true, "stuff": "BlocksSlate", "x": 109.0, "z": 194.0},
		},
	}
	if doors, walls := stoneRing(audit); doors != 1 || walls != 2 {
		t.Fatalf("stone ring %d doors, %d walls; want 1, 2", doors, walls)
	}
}
