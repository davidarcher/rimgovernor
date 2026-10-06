package buildingruntime

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestDefenseTierSequenceRoamerBuildsRingFirst(t *testing.T) {
	record := store.DefenseLayoutRecord{Tiers: []store.DefenseTierRecord{
		{Name: policy.TierTurrets}, {Name: "perimeter-00"}, {Name: "perimeter-01", Built: true}, {Name: "perimeter-r1-02"}, {Name: "perimeter-outer-00"},
	}}
	plain := defenseTierSequence(record, false)
	if plain[0] != policy.TierChokepoint || slices.Index(plain, "perimeter-00") < len(defenseTierOrder) {
		t.Fatalf("no roamer must keep the ring after the fixed tiers: %v", plain)
	}
	roamer := defenseTierSequence(record, true)
	if roamer[0] != "perimeter-00" || roamer[1] != "perimeter-r1-02" || roamer[2] != policy.TierChokepoint {
		t.Fatalf("roamer must put the open core ring first: %v", roamer)
	}
	if slices.Index(roamer, "perimeter-outer-00") < slices.Index(roamer, policy.TierChokepoint) {
		t.Fatalf("outer ring must stay behind: %v", roamer)
	}
	closed := store.DefenseLayoutRecord{Tiers: []store.DefenseTierRecord{{Name: "perimeter-00", Built: true}}}
	if got := defenseTierSequence(closed, true); got[0] != policy.TierChokepoint {
		t.Fatalf("a closed ring changes nothing: %v", got)
	}
}

func TestDefenseRoamerOwned(t *testing.T) {
	var p observation.ColonyProjection
	if defenseRoamerOwned(p) {
		t.Fatal("unknown census is not a roamer")
	}
	p.Facts.AnimalUpkeep.Animals = domain.Known([]policy.UpkeepAnimal{{RequiresPen: domain.Known(false)}, {RequiresPen: domain.Unknown[bool]()}})
	if defenseRoamerOwned(p) {
		t.Fatal("non-roamer and unknown rows are not a roamer")
	}
	p.Facts.AnimalUpkeep.Animals = domain.Known([]policy.UpkeepAnimal{{RequiresPen: domain.Known(true)}})
	if !defenseRoamerOwned(p) {
		t.Fatal("pen-requiring animal is a roamer")
	}
}
