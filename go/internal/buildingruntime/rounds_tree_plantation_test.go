package buildingruntime

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func oakChoice() policy.CropChoice {
	return policy.CropChoice{Name: "Plant_TreeOak", Available: domain.Known(true), Edible: domain.Known(false),
		GrowDays: domain.Known(30.0), FertilityMin: domain.Known(0.5), FertilitySensitivity: domain.Known(1.0), HarvestWork: domain.Known(800.0),
		SowTags: domain.Known([]string{"Ground"}), Harvests: domain.Known(policy.Resource("WoodLog")), UnitsPerCell: domain.Known(46.0),
		BlockAdjacentSow: domain.Known(true), HarvestDestroys: domain.Known(true), MustBeWildToSow: domain.Known(true),
		HarvestMinGrowth: domain.Known(0.4), SowWork: domain.Known(4000.0), SowMinSkill: domain.Known(int32(6)),
		WildBiomes: domain.Known([]string{"TemperateForest"})}
}

// A wood deficit opens a lattice-sized oak zone; a standing plantation counts
// only its growing trees, so a second one is not opened for the same deficit
// and a grown tree (already a chop row) is not counted twice.
func TestTreePlantationPlannerSizesAndCountsStanding(t *testing.T) {
	var cells []policy.SiteCell
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			cells = append(cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Zone: domain.Known(false),
				Roofed: domain.Known(false), Fertility: domain.Known(1.0)})
		}
	}
	f := &resourceFieldPlanner{
		choices: []policy.CropChoice{oakChoice()},
		climate: policy.CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(40.0), OutdoorsDark: domain.Known(false), Biome: domain.Known("TemperateForest")},
		skill:   domain.Known(int32(7)),
		siteRead: func() (policy.FarmSiteRequest, bool, error) {
			return policy.FarmSiteRequest{Bounds: policy.Bounds{Width: 20, Height: 20}, Anchor: domain.Cell{X: 10, Z: 10}, Cells: cells}, true, nil
		},
	}
	_, plan, ok, err := f.candidate("WoodLog", 100)
	if err != nil || !ok || plan.Needed != 5*domain.TreeCellsPerTree || plan.Sites.Cells != plan.Needed {
		t.Fatalf("%v %v %s", ok, err, plan.Explain())
	}
	f.skill = domain.Known(int32(5))
	if _, _, ok, _ = f.candidate("WoodLog", 100); ok {
		t.Fatal("no sower at the sow skill: nothing opens")
	}
	f.skill = domain.Known(int32(7))
	f.climate.Biome = domain.Known("Tundra")
	if _, _, ok, _ = f.candidate("WoodLog", 100); ok {
		t.Fatal("a species not native to the biome is not sown")
	}
	// 20 standing cells are 5 trees of 23 wood at the fell point.
	f.farms = []observation.FarmZoneFact{{ID: "z1", Crop: "Plant_TreeOak", UsableCells: domain.Known(uint32(20))}}
	if got := f.standing("WoodLog"); got != 115 {
		t.Fatalf("standing %d", got)
	}
	rows := []policy.AcquisitionSource{{ID: "grown", Resource: "WoodLog", Tree: true, Plantation: true, Yield: 46}, {ID: "wild", Resource: "WoodLog", Tree: true, Yield: 30}}
	if got := grownPlantationYield(rows, "WoodLog"); got != 46 {
		t.Fatalf("only the plantation row counts, got %d", got)
	}
}

// The chop gate is the fell point of the best species the colony can sow, and
// the configured gate when none can.
func TestChopMinGrowthFollowsTheTreePlan(t *testing.T) {
	r := &Rounder{policy: policy.RoundsPolicy{ChopMinGrowth: 0.7}}
	projection := observation.ColonyProjection{
		CropClimate: policy.CropClimate{Biome: domain.Known("TemperateForest")},
		Definitions: []observation.PlanningDefinition{{Name: "Plant_TreeOak", Available: domain.Known(true), Edible: domain.Known(false),
			GrowDays: domain.Known(30.0), HarvestedThingDef: domain.Known("WoodLog"), HarvestYield: domain.Known(46.0),
			BlockAdjacentSow: domain.Known(true), HarvestDestroysPlant: domain.Known(true), MustBeWildToSow: domain.Known(true),
			HarvestMinGrowth: domain.Known(0.4), SowWork: domain.Known(4000.0), HarvestWork: domain.Known(800.0), SowMinSkill: domain.Known(int32(6)),
			WildBiomes: domain.Known([]string{"TemperateForest"})}},
		WorkPawns: domain.Known([]policy.WorkPawn{}),
	}
	if got := r.chopMinGrowth(projection); got != 0.7 {
		t.Fatalf("no sower: the configured gate holds, got %v", got)
	}
	sower := policy.WorkPawn{ID: "sower", Available: domain.Known(true), Skills: domain.Known([]policy.WorkSkill{{Name: "Plants", Level: 8}}),
		Work: domain.Known([]policy.WorkPriority{{Work: policy.WorkGrowing, Priority: 3, Skill: "Plants"}}), Traits: domain.Known([]policy.PawnTrait{}), Incapable: domain.Known([]policy.WorkType{}), Age: domain.Known(30.0)}
	projection.WorkPawns = domain.Known([]policy.WorkPawn{sower})
	// Oak is felled as soon as harvestable.
	if got := r.chopMinGrowth(projection); got != 0.4 {
		t.Fatalf("a skilled sower: the oak fell point gates the chop, got %v", got)
	}
	projection.WorkPawns = domain.Unknown[[]policy.WorkPawn]()
	if got := r.chopMinGrowth(projection); math.Abs(got-0.7) > 1e-9 {
		t.Fatalf("unknown pawns: the configured gate holds, got %v", got)
	}
}
