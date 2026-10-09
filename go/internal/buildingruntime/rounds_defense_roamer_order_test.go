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

func TestDefensePromotedReadsRaidPointsAgainstCapacity(t *testing.T) {
	var p observation.ColonyProjection
	if defensePromoted(p) {
		t.Fatal("unknown raid points and capacity never promote")
	}
	p.Facts.RaidPoints, p.Facts.DefenseCapacity = domain.Known(600.0), domain.Known(100.0)
	if !defensePromoted(p) {
		t.Fatal("600 raid points over 100 capacity promote")
	}
	p.Facts.RaidPoints = domain.Known(250.0)
	if defensePromoted(p) {
		t.Fatal("below the 300 floor never promotes")
	}
}

func TestDefenseBuildsStateSecureThenPromoted(t *testing.T) {
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "BlocksGranite")
	if err != nil {
		t.Fatal(err)
	}
	placed, err := domain.NewBuildingAction("a", wall, policy.DefenseBuildTier(false, false, false))
	if err != nil {
		t.Fatal(err)
	}
	if tier, _ := placed.Tier().Value(); tier != domain.TierSecure {
		t.Fatalf("unpromoted defense placed at %v, want Secure", tier)
	}
	lifted, err := domain.NewBuildingAction("b", wall, domain.TierSurvive)
	if err != nil {
		t.Fatal(err)
	}
	if lifted, err = lifted.WithTier(domain.TierSurvive, "Wall123"); err != nil {
		t.Fatal(err)
	}
	if tier, _ := lifted.Tier().Value(); tier != domain.TierSurvive || lifted.ConstructionTarget() != "Wall123" {
		t.Fatalf("set-tier action = %v on %q", tier, lifted.ConstructionTarget())
	}
}
