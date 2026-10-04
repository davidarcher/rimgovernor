package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A mood cast serves only pressure that need relief and the facility goals do
// not own; it never competes with a measured relief or a provisioning goal.
func TestMoodCastReasonLeavesReliefAndProvisioningToTheirOwners(t *testing.T) {
	for reason, want := range map[policy.MoodMethodReason]bool{
		policy.MoodNoCause: true, policy.MoodExhausted: true, policy.MoodUnowned: true,
		policy.MoodRelief: false, policy.MoodProvisioned: false, policy.MoodMentalBreak: false,
		policy.MoodPlayerWork: false, policy.MoodRecovered: false, policy.MoodUnavailable: false,
	} {
		if moodCastReason(reason) != want {
			t.Error(reason, want)
		}
	}
}

func TestMoodCastAttemptsAreBoundedPerCasterAndPsycast(t *testing.T) {
	if moodCastPrefix("zed", "amy", "WordOfJoy") == moodCastPrefix("zed", "bob", "WordOfJoy") {
		t.Fatal("casters share attempts")
	}
}
