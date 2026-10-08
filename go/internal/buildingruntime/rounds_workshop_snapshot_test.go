package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// facility/workshop (#738, replaced by this test): tribal8 served with
// --routine-resource-target MeleeWeapon_Club:3. Recorded at 424c47cb9, review
// 16878-2. The review's own floors (the wood latch's WoodLog floor, the medical reserve's MedicineHerbal:24)
// merge with the operator's club floor, and RankResourceTargets puts the
// club last: wood, then herbal medicine (which the run went on to commit an
// acquisition plan to), then MeleeWeapon_Club:3. This is what the policy
// does today, and why the native case never saw the workshop admitted
// inside its window.
//
// The recording holds no MaintainResource workshop step read: the step
// that would stage a CraftingSpot (stepWorkshops) never ran on a club
// deficit, and its recipe-catalog and bench-census reads are not part of
// the step recording, so the bench choice and the club bill are not
// asserted here.
func TestSnapshotWorkshopClubRanksLastBehindReviewFloors(t *testing.T) {
	t.Parallel()
	r := loadRecorded(t, "workshop-club-ranked-last")
	if got := r.Policy.ResourceTargets["MeleeWeapon_Club"]; got != 3 {
		t.Fatalf("recorded policy club floor %d, want the case's 3", got)
	}
	// resourceTargets over the review, minus the defensive layout's needs
	// (the recording's colony has no standing layout).
	var needs map[policy.Resource]int64
	needs = policy.ResourceConcernTargets(needs, policy.ConstructionDemandOf(r.Facts, r.Policy.Seasonal(r.Facts.Calendar, r.Facts.DisasterConditions), r.Review.Latches))
	needs = policy.ResourceConcernTargets(needs, policy.ResourceRunwayTargets(r.Review.ResourceRunwayState()))
	targets, err := r.Policy.EffectiveResourceTargets(r.Facts.Resources, needs)
	if err != nil {
		t.Fatal(err)
	}
	ranked, err := policy.RankResourceTargets(targets, r.Facts.Resources)
	if err != nil {
		t.Fatal(err)
	}
	club := -1
	for i, target := range ranked {
		if target.Resource == "MeleeWeapon_Club" {
			club = i
		}
	}
	want := []policy.ResourceTarget{{Resource: "WoodLog", Target: 1240}, {Resource: "MeleeWeapon_Club", Target: 3}}
	if len(ranked) != len(want) || club != len(want)-1 {
		t.Fatalf("ranked targets %+v: want %+v", ranked, want)
	}
	for i := range want {
		if ranked[i] != want[i] {
			t.Fatalf("ranked targets %+v: want %+v", ranked, want)
		}
	}
}
