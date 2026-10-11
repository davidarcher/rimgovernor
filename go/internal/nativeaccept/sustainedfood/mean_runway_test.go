package sustainedfood

import "testing"

func TestMeanFoodRunwayAveragesTheTrailingSpan(t *testing.T) {
	sample := func(tick uint64, runway float64) map[string]any {
		return map[string]any{"tick": tick, "colony": map[string]any{"foodRunwayDays": runway}}
	}
	timeline := []map[string]any{
		sample(0, 0), // outside the span
		sample(900, 10),
		{"tick": uint64(950), "colony": map[string]any{"error": "unreadable"}},
		sample(1000, 20),
	}
	mean, n, ok := MeanFoodRunway(timeline, 150)
	if !ok || n != 2 || mean != 15 {
		t.Fatalf("mean %v over %d samples (ok=%v), want 15 over 2", mean, n, ok)
	}
	if _, _, ok := MeanFoodRunway(nil, 150); ok {
		t.Fatal("an empty timeline has no mean")
	}
}
