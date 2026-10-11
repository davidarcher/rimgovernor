package combatlab

import "testing"

func TestParseReseed(t *testing.T) {
	draws, err := parseReseed(map[string]any{"success": true, "seed": float64(7), "draws": []any{float64(1), float64(2)}}, 7)
	if err != nil || len(draws) != 2 || draws[1] != 2 {
		t.Fatalf("draws %v, err %v", draws, err)
	}
	for name, reply := range map[string]map[string]any{
		"refused":   {"success": false},
		"wrongSeed": {"success": true, "seed": float64(8), "draws": []any{float64(1)}},
		"noDraws":   {"success": true, "seed": float64(7)},
	} {
		if _, err := parseReseed(reply, 7); err == nil {
			t.Errorf("%s: want an error", name)
		}
	}
}
