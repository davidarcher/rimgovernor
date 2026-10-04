package policy

import "testing"

// Foothold stalls after six in-game hours of the one-day default, not one
// hour: a slow but working method is not churned before a hauler arrives.
func TestStageGoalStallScaleFootholdSixHours(t *testing.T) {
	if got := StageConcernStallScale(StageFoothold) * 24; got != 6 {
		t.Fatalf("foothold hours: %v", got)
	}
	if StageConcernStallScale(StageReserves) != 1 {
		t.Fatal("reserves scaled")
	}
}
