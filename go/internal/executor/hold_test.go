package executor

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// This list must be kept in sync with every policy.Reason constant declared
// across go/internal/policy/*.go. There is no way to enumerate Go constants
// at runtime, so this is the regression guard: a new Reason added to policy
// without a matching entry here (and in reasonHeldReasons) fails this test.
func allKnownReasons() []policy.Reason {
	return []policy.Reason{
		policy.UnsafeThreat, policy.CriticalMedical, policy.StaleFacts, policy.UnknownFacts,
		policy.NotReady, policy.AlreadyReserved, policy.UnsafePlacement, policy.MaterialRequired,
		policy.DependencyBlocked, policy.GeometryBlocked,
		policy.InvalidHeld,

		policy.CleanerUnavailable,
		policy.DoctorUnavailable, policy.DraftOwnership, policy.EquipPawnUnavailable, policy.FilthIneligible,
		policy.GearReplacePawnUnavailable, policy.HomeCoverageExcluded,
		policy.HomeCoverageGeometryChanged,
		policy.NativeIneligible, policy.PatientIneligible, policy.PlayerOrder,
		policy.RecoveryServicePawnUnavailable,
		policy.RepairerUnavailable, policy.RescuerUnavailable,
		policy.StructureIneligible, policy.UnsuitableEquipment,
		policy.UnsupportedThreat, policy.WallRemovalGeometryChanged, policy.WallRemovalTargetChanged,
		policy.ExcavationUnsupported, policy.ExcavationGeometryChanged,
		policy.OpenerUnavailable,
		policy.UnsafeRoute, policy.RoofSupportRisk, policy.StorageMissing, policy.UrgentCompetingWork,
	}
}
func TestReasonHeldReasonsIsExhaustiveAndBijective(t *testing.T) {
	known := allKnownReasons()
	if len(reasonHeldReasons) != len(known) {
		t.Fatalf("reasonHeldReasons has %d entries, expected %d known policy.Reason values", len(reasonHeldReasons), len(known))
	}
	seenHeld := map[domain.HeldReason]policy.Reason{}
	for _, reason := range known {
		held, ok := reasonHeldReasons[reason]
		if !ok {
			t.Fatalf("policy.Reason %q has no domain.HeldReason mapping", reason)
		}
		if prior, dup := seenHeld[held]; dup {
			t.Fatalf("domain.HeldReason %q is mapped from both %q and %q", held, prior, reason)
		}
		seenHeld[held] = reason
	}
}
