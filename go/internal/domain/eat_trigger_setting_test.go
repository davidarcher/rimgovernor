package domain

import (
	"math"
	"testing"
)

func TestEatTriggerSettingRange(t *testing.T) {
	for _, bad := range []float64{-0.01, 1.01, math.NaN(), math.Inf(1)} {
		if _, err := NewEatTriggerSetting("Human1", bad); err == nil {
			t.Fatal("accepted", bad)
		}
	}
	if _, err := NewEatTriggerSetting("", 0.125); err == nil {
		t.Fatal("empty pawn accepted")
	}
	v, err := NewEatTriggerSetting("Human1", 0.125)
	if err != nil {
		t.Fatal(err)
	}
	if level, ok := v.EatTrigger(); !ok || level != 0.125 {
		t.Fatal(v)
	}
	if _, err := NewPawnSettingsAction("s1", v); err != nil {
		t.Fatal(err)
	}
}
