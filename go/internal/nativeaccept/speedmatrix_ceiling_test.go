package nativeaccept

import "testing"

func TestCeilingRatios(t *testing.T) {
	rows := []SpeedMetrics{{Case: "governor-off", WallTPS: 5000}, {Case: "regulated", WallTPS: 2500, PausedFractionNative: 0.2, NativePauseSamples: 3}, {Case: "empty"}}
	got := CeilingRatios(rows)
	if len(got) != 1 || got[0].Case != "regulated" || got[0].Ratio != 0.5 || got[0].CeilingTPS != 5000 || got[0].PausedFractionNative != 0.2 {
		t.Fatalf("ratios = %+v", got)
	}
	if got := CeilingRatios(rows[1:]); got != nil {
		t.Fatalf("no ceiling: %+v", got)
	}
}
