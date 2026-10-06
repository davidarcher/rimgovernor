package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func recoveryShadowRow(id string, x int32, home, deconstructible bool) ClearanceTarget {
	return ClearanceTarget{
		EntityID: id, DefName: "Ruin", Minimum: domain.Cell{X: x}, Maximum: domain.Cell{X: x},
		InHome: home, Deconstructible: deconstructible,
		Salvage: &SalvageEvidence{Safe: domain.Known(true), Candidate: SupplyCandidate{Yields: []CandidateYield{recoveryYield("Steel", 5, 100)}}},
	}
}

// One census fixture, both decisions: the old path's Home selection (one
// removal, the rest of Home unlisted, outside Home held) and the queue's.
func TestCompareRecoveryClassesOneFixture(t *testing.T) {
	rows := []ClearanceTarget{
		recoveryShadowRow("home_near", 1, true, true),
		recoveryShadowRow("home_next", 2, true, true),
		recoveryShadowRow("remote_far", 90, false, true),
		recoveryShadowRow("remote_demand", 91, false, true),
		recoveryShadowRow("stuck", 3, true, false),
	}
	selection := SelectHomeClearance(rows, domain.Cell{})
	old := RecoveryOld{Holds: selection.Holds}
	for _, r := range selection.Targets {
		old.Admitted = append(old.Admitted, r.EntityID)
	}
	// The old remote filter journaled a demand hold in place of outside_home.
	for i := range old.Holds {
		if old.Holds[i].Target == "remote_demand" {
			old.Holds[i].Reason = "demand:no_demand"
		}
	}
	r := RecoveryRequest{Slot: true, Center: domain.Cell{}, Short: map[Resource]bool{"Steel": true}}
	for _, row := range rows {
		thing, _ := RecoveryClearanceThing(row, false)
		r.Things = append(r.Things, thing)
	}
	s := CompareRecovery(old, RankRecovery(r))
	got := map[string]ShadowClass{}
	for _, d := range s.Divergences {
		got[d.ID] = d.Class
	}
	want := map[string]ShadowClass{"home_next": ShadowBatch, "remote_far": ShadowHomeSplit, "remote_demand": ShadowDemandGone}
	// home_near and stuck (held for the same word on both sides) agree.
	// remote_far and remote_demand outrank nothing: they queue behind Home by distance.
	if len(got) != len(want) {
		t.Fatalf("divergences = %+v", s.Divergences)
	}
	for id, class := range want {
		if got[id] != class {
			t.Fatalf("%s class = %q, want %q (%+v)", id, got[id], class, s.Divergences)
		}
	}
	if s.Unexplained() != 0 || len(s.QueueBatch) != 4 || len(s.OldAdmitted) != 1 {
		t.Fatalf("shadow = %+v", s)
	}
}

func TestCompareRecoveryUnexplained(t *testing.T) {
	q := RecoveryQueue{Admitted: "b", Entries: []RecoveryEntry{
		{ID: "b", Kind: RemoteSalvage, Status: RecoveryAdmitted},
		{ID: "a", Kind: RemoteSalvage, Status: RecoveryHeld, Reason: RemoteHoldRouteUnsafe},
		{ID: "c", Kind: RemoteSalvage, Status: RecoveryQueued},
		{ID: "d", Kind: RemoteSalvage, Status: RecoveryDeferred, Reason: RemoteHoldMissingStorage},
		{ID: "e", Kind: RemoteSalvage, Status: RecoveryHeld, Reason: "casket"},
		{ID: "loot", Kind: RemoteLoot, Status: RecoveryQueued},
	}}
	old := RecoveryOld{Admitted: []string{"a", "d", "gone"}, Holds: []ClearanceHold{{"e", "ancient_danger"}, {"c", "casket"}}}
	s := CompareRecovery(old, q)
	got := map[string]ShadowClass{}
	for _, d := range s.Divergences {
		got[d.ID] = d.Class
	}
	want := map[string]ShadowClass{"a": ShadowOrder, "d": ShadowStorageThrottle, "e": ShadowHoldReason, "c": ShadowAdmitsHeld, "gone": ShadowQueueMissing}
	for id, class := range want {
		// a is both queue_holds and the head's order divergence; the later row wins the map.
		if id == "a" {
			continue
		}
		if got[id] != class {
			t.Fatalf("%s class = %q, want %q (%+v)", id, got[id], class, s.Divergences)
		}
	}
	if !hasShadow(s, "a", ShadowQueueHolds) || !hasShadow(s, "a", ShadowOrder) {
		t.Fatalf("a = %+v", s.Divergences)
	}
	if s.Loot != 1 || s.Unexplained() != 5 {
		t.Fatalf("loot %d unexplained %d: %+v", s.Loot, s.Unexplained(), s.Divergences)
	}
}

func hasShadow(s RecoveryShadow, id string, class ShadowClass) bool {
	for _, d := range s.Divergences {
		if d.ID == id && d.Class == class {
			return true
		}
	}
	return false
}
