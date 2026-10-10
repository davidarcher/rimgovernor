package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestRecoveryContinuationReasonsRemainDistinct(t *testing.T) {
	cases := []struct {
		reason policy.RecoverySelectionReason
		want   Verdict
	}{
		{policy.RecoveryFactsUnknown, fieldUnavailable("recovery_observations")},
		{policy.RecoveryNoWorker, noWorker("recovery_service")},
		{policy.RecoveryTrackedMissing, siteBlocked("recovery_infrastructure", string(policy.RecoveryTrackedMissing))},
		{policy.RecoveryNoWork, BuildingReasonNoDeficit},
		{policy.RecoveryMethodsSeen, waitFor(policy.CauseMethodUsed, "recovery_service_proposal")},
	}
	for _, tc := range cases {
		if got := recoverySelectionVerdict(tc.reason); got != tc.want || got.Validate() != nil {
			t.Fatalf("%s: %+v", tc.reason, got)
		}
	}
}
