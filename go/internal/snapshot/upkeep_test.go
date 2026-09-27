package snapshot

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Upkeep deficits converted from the native upkeep/* cases (#746), each
// recorded at 04b0a98c from `acceptance run upkeep/<case>`: the review
// that opened the staged deficit and, where the case followed it to
// recovery, the review that closed it.

// assess replays path and returns its assessment of goal.
func assess(t *testing.T, path string, goal policy.GoalID) policy.RoutineAssessment {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	a, err := r.Assessment(goal)
	if err != nil {
		t.Fatal(err)
	}
	return a
}

// transition asserts goal is an actionable deficit in open and recovered
// in closed.
func transition(t *testing.T, open, closed string, goal policy.GoalID) {
	t.Helper()
	if a := assess(t, open, goal); a.Need != domain.NeedDeficit || a.MethodUnavailable {
		t.Errorf("%s: %s %+v, want an actionable deficit", open, goal, a)
	}
	if a := assess(t, closed, goal); a.Need != domain.NeedRecovered {
		t.Errorf("%s: %s %+v, want recovered", closed, goal, a)
	}
}

// upkeep/fire, ticks 15 and 615: one small home fire latches
// MaintainFireSafety as an emergency, and the latch clears once no home
// fire remains.
func TestReplayHomeFireIsAnEmergencyUntilOut(t *testing.T) {
	transition(t, "testdata/upkeep-fire-burning.json", "testdata/upkeep-fire-out.json", policy.MaintainFireSafety)
	if a := assess(t, "testdata/upkeep-fire-burning.json", policy.MaintainFireSafety); a.Priority > 1 {
		t.Errorf("fire ranked at priority %d, not an emergency", a.Priority)
	}
	for path, want := range map[string]bool{"testdata/upkeep-fire-burning.json": true, "testdata/upkeep-fire-out.json": false} {
		r, err := Load(path)
		if err != nil {
			t.Fatal(err)
		}
		needs, err := r.Detect()
		if err != nil {
			t.Fatal(err)
		}
		if needs.Latches.Upkeep.Fire != want {
			t.Errorf("%s: fire latch %v, want %v", path, needs.Latches.Upkeep.Fire, want)
		}
	}
}

// upkeep/blocked, tick 15: medicine outdoors and a damaged Home wall,
// walled off from every worker. Both deficits stay visible rather than
// being dropped as unreachable.
func TestReplayUnreachableUpkeepStaysInDeficit(t *testing.T) {
	for _, goal := range []policy.GoalID{policy.SecureSupplies, policy.MaintainEssentialRepairs} {
		if a := assess(t, "testdata/upkeep-blocked.json", goal); a.Need != domain.NeedDeficit {
			t.Errorf("%s %+v, want deficit", goal, a)
		}
	}
}

// upkeep/storage-missing, ticks 62208 and 64083: exposed medicine with no
// storage accepting it keeps SecureSupplies open until the fallback
// stockpile holds it.
func TestReplayExposedMedicineSecuredInCreatedStorage(t *testing.T) {
	transition(t, "testdata/upkeep-storage-missing-exposed.json", "testdata/upkeep-storage-missing-stored.json", policy.SecureSupplies)
}

// upkeep/medicine, ticks 153652 and 161964: every medicine destroyed opens
// MaintainMedicalReserves, closed once harvested healroot restores the
// reserve.
func TestReplayMedicalReserveReplenished(t *testing.T) {
	transition(t, "testdata/upkeep-medicine-short.json", "testdata/upkeep-medicine-stocked.json", policy.MaintainMedicalReserves)
}

// upkeep/feed, ticks 16740 and 23296: a pet confined away from the stock
// opens MaintainAnimalFeed, closed once reachable kibble exists.
func TestReplayConfinedPetFed(t *testing.T) {
	transition(t, "testdata/upkeep-feed-hungry.json", "testdata/upkeep-feed-fed.json", policy.MaintainAnimalFeed)
}

// upkeep/feed-delivered, ticks 23939 and 30848: the confined pet with no
// bench inside its area stays unfed until kibble is delivered into it.
func TestReplayConfinedPetFedByDelivery(t *testing.T) {
	transition(t, "testdata/upkeep-feed-delivered-hungry.json", "testdata/upkeep-feed-delivered-fed.json", policy.MaintainAnimalFeed)
}

// upkeep/sleeping, ticks 40346 and 40501: one bed fewer than colonists
// keeps MaintainHousing open until every colonist sleeps in an owned bed.
func TestReplayMissingBedBuiltAndOwned(t *testing.T) {
	transition(t, "testdata/upkeep-sleeping-short.json", "testdata/upkeep-sleeping-bedded.json", policy.MaintainHousing)
}

// upkeep/cold, ticks 15 and 12145: sleeping spots in an enclosed room
// below 12 C open EnsureTemperatureSafety, closed once a heat source
// warms the measured sleeping temperature.
func TestReplayColdRoomHeated(t *testing.T) {
	transition(t, "testdata/upkeep-cold-room.json", "testdata/upkeep-cold-heated.json", policy.EnsureTemperatureSafety)
}

// stoneShellTargets replays ReviewStoneShell over path's owned
// constructions.
func stoneShellTargets(t *testing.T, path string) []string {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	owned, err := policy.OwnedConstructions(r.Facts.ConstructionClaims, r.Facts.CurrentConstruction)
	if err != nil {
		t.Fatal(err)
	}
	targets, err := policy.ReviewStoneShell(owned, r.Facts.StoneStructures)
	if err != nil {
		t.Fatal(err)
	}
	v, known := targets.Value()
	if !known {
		t.Fatal(path, "stone shell targets unknown")
	}
	return v
}

// upkeep/stone-shell, ticks 29, 95332 and 99820: the debug colony owns no
// walls until the power family shelters its battery in a wood room; those
// owned wood walls open MaintainStoneShell, and the replacement bundle
// takes Thing_Wall24289 off the targets while the rest stay open.
func TestReplayStoneShellReplacesOwnedWoodWall(t *testing.T) {
	if got := stoneShellTargets(t, "testdata/upkeep-stone-shell-unowned.json"); len(got) != 0 {
		t.Errorf("unowned colony has stone shell targets %v", got)
	}
	if a := assess(t, "testdata/upkeep-stone-shell-wood.json", policy.MaintainStoneShell); a.Need != domain.NeedDeficit {
		t.Errorf("owned wood walls: %+v, want deficit", a)
	}
	const replaced = "Thing_Wall24289"
	if got := stoneShellTargets(t, "testdata/upkeep-stone-shell-wood.json"); !slices.Contains(got, replaced) {
		t.Errorf("%s not a target before replacement: %v", replaced, got)
	}
	after := stoneShellTargets(t, "testdata/upkeep-stone-shell-replaced.json")
	if slices.Contains(after, replaced) || len(after) == 0 {
		t.Errorf("targets after replacement %v", after)
	}
}

// upkeep/scattered, ticks 15 and 13749: medicine outdoors beside a covered
// stockpile and a half-damaged Home wall open SecureSupplies and
// MaintainEssentialRepairs until both are observed secured and repaired;
// the outdoor dirt lies outside any workspace, so MaintainCleanFacilities
// never opens.
func TestReplayScatteredSuppliesSecuredAndWallRepaired(t *testing.T) {
	const open, closed = "testdata/upkeep-scattered.json", "testdata/upkeep-scattered-secured.json"
	transition(t, open, closed, policy.SecureSupplies)
	transition(t, open, closed, policy.MaintainEssentialRepairs)
	for _, path := range []string{open, closed} {
		if a := assess(t, path, policy.MaintainCleanFacilities); a.Need == domain.NeedDeficit {
			t.Errorf("%s: outdoor dirt opened MaintainCleanFacilities", path)
		}
	}
}
