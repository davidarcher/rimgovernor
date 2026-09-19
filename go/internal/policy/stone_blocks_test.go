package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestStoneBlockTargetPicksTheMostCountedStone(t *testing.T) {
	stock := domain.Known([]Amount{
		{Resource: "ChunkGranite", Count: 3},
		{Resource: "ChunkSandstone", Count: 9},
		{Resource: "ChunkSlagSteel", Count: 40},
		{Resource: "WoodLog", Count: 200},
	})
	resource, target, ok, err := StoneBlockTarget(60, stock)
	if err != nil || !ok || resource != "BlocksSandstone" || target != 60 {
		t.Fatalf("got %q %d %v %v", resource, target, ok, err)
	}
}

func TestStoneBlockTargetTiesBreakOnBlockName(t *testing.T) {
	stock := domain.Known([]Amount{{Resource: "ChunkSlate", Count: 5}, {Resource: "ChunkMarble", Count: 5}})
	resource, _, ok, err := StoneBlockTarget(20, stock)
	if err != nil || !ok || resource != "BlocksMarble" {
		t.Fatalf("got %q %v %v", resource, ok, err)
	}
}

func TestStoneBlockTargetNeedsChunksAndAFloor(t *testing.T) {
	if _, _, ok, err := StoneBlockTarget(20, domain.Known([]Amount{{Resource: "ChunkSlagSteel", Count: 4}})); ok || err != nil {
		t.Fatalf("slag alone selected a target: %v %v", ok, err)
	}
	if _, _, ok, err := StoneBlockTarget(0, domain.Known([]Amount{{Resource: "ChunkGranite", Count: 4}})); ok || err != nil {
		t.Fatalf("zero floor selected a target: %v %v", ok, err)
	}
	if _, _, ok, err := StoneBlockTarget(20, domain.Unknown[[]Amount]()); ok || err != nil {
		t.Fatalf("unknown census selected a target: %v %v", ok, err)
	}
	if _, _, _, err := StoneBlockTarget(10001, domain.Known([]Amount{})); err == nil {
		t.Fatalf("expected an error for a floor past the bill bound")
	}
	if _, _, _, err := StoneBlockTarget(20, domain.Known([]Amount{{Resource: "ChunkGranite", Count: -1}})); err == nil {
		t.Fatalf("expected an error for a negative count")
	}
}

func TestEffectiveResourceTargetsMergesStoneBlocks(t *testing.T) {
	p := DefaultRoutinePolicy()
	p.ResourceTargets = map[Resource]int64{"MeleeWeapon_Club": 2}
	p.StoneBlockTarget = 40
	stock := domain.Known([]Amount{{Resource: "ChunkGranite", Count: 7}})
	targets, err := p.EffectiveResourceTargets(stock, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(targets) != 2 || targets["MeleeWeapon_Club"] != 2 || targets["BlocksGranite"] != 40 {
		t.Fatalf("got %v", targets)
	}
	if len(p.ResourceTargets) != 1 {
		t.Fatalf("operator targets mutated: %v", p.ResourceTargets)
	}
	p.ResourceTargets["BlocksGranite"] = 5
	targets, err = p.EffectiveResourceTargets(stock, map[Resource]int64{"BlocksGranite": 3, "Chemfuel": 30})
	if err != nil || targets["BlocksGranite"] != 5 || targets["Chemfuel"] != 30 {
		t.Fatalf("operator floor should win and derived needs merge: %v %v", targets, err)
	}
	if !p.ResourceGoalConfigured() || !p.TracksResource("BlocksSlate") || p.TracksResource("Steel") {
		t.Fatalf("configuration predicates disagree")
	}
	if err := p.Validate(); err != nil {
		t.Fatalf("policy should validate: %v", err)
	}
	p.StoneBlockTarget = -1
	if err := p.Validate(); err == nil {
		t.Fatalf("negative stone block target should fail validation")
	}
}

func TestEffectiveResourceTargetsWithoutChunksIsTheOperatorMap(t *testing.T) {
	p := DefaultRoutinePolicy()
	p.StoneBlockTarget = 40
	targets, err := p.EffectiveResourceTargets(domain.Known([]Amount{}), nil)
	if err != nil || len(targets) != 0 {
		t.Fatalf("got %v %v", targets, err)
	}
	if p.ResourceTargets != nil || !p.ResourceGoalConfigured() {
		t.Fatalf("stone floor alone should still configure the goal")
	}
}
