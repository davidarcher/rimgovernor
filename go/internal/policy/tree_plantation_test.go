package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// treeCrop is a plantation species with TreeBase's sowing facts (sowWork 4000,
// harvestWork 800, harvestMinGrowth 0.40, skill 6), native to TemperateForest.
func treeCrop(name string, days, wood float64) CropChoice {
	c := resourceCrop(name, "WoodLog", days, wood)
	c.BlockAdjacentSow, c.HarvestDestroys, c.MustBeWildToSow = domain.Known(true), domain.Known(true), domain.Known(true)
	c.HarvestMinGrowth, c.SowWork, c.HarvestWork = domain.Known(0.4), domain.Known(4000.0), domain.Known(800.0)
	c.SowMinSkill = domain.Known(int32(6))
	c.WildBiomes = domain.Known([]string{"TemperateForest"})
	return c
}

func treeRequest(deficit float64, crops ...CropChoice) ResourceFieldRequest {
	r := resourceRequest(deficit, crops...)
	r.Resource = "WoodLog"
	r.Climate.Biome = domain.Known("TemperateForest")
	r.GrowerSkill = domain.Known(int32(8))
	return r
}

func TestTreePricingFellPoint(t *testing.T) {
	oak := treeCrop("Plant_TreeOak", 30, 46)
	// Land 4 cells x 30 days x 250 = 30000 per full tree: felling at the
	// harvestable point halves the wood but costs far less land time.
	fraction, wood, _, ok := treePricing(oak)
	if !ok || fraction != 0.4 || math.Abs(wood-23) > 1e-9 {
		t.Fatalf("oak fells early: %v %v %v", fraction, wood, ok)
	}
	// A fast, cheap-land species keeps its trees to full growth.
	bamboo := treeCrop("Plant_Bamboo", 12, 10)
	if fraction, wood, _, _ := treePricing(bamboo); fraction != 1 || wood != 10 {
		t.Fatalf("bamboo fells at maturity: %v %v", fraction, wood)
	}
	oak.SowWork = domain.Unknown[float64]()
	if _, _, _, ok := treePricing(oak); ok {
		t.Fatal("priced a tree on an unknown sow work")
	}
}

func TestPlanTreePlantation(t *testing.T) {
	oak := treeCrop("Plant_TreeOak", 30, 46)
	t.Run("lattice sized zone", func(t *testing.T) {
		// 23 wood a tree at the fell point: 100 wood is 5 trees, 20 cells.
		plan, ok := PlanFieldByResource(treeRequest(100, oak))
		if !ok || plan.Needed != 5*domain.TreeCellsPerTree || plan.Sites.Cells != plan.Needed {
			t.Fatal(plan.Explain())
		}
		field, ok := ResourceFieldCandidate("WoodLog", plan)
		if !ok {
			t.Fatal("tree plantation unpriced")
		}
		if lead, _ := field.LeadDays.Value(); lead != 30*0.4 {
			t.Fatalf("lead %v", lead)
		}
		if setup, _ := field.UpfrontCost.LaborTicks.Value(); setup != 5*4000 {
			t.Fatalf("setup %v", setup)
		}
	})
	t.Run("best price wins over a richer slower species", func(t *testing.T) {
		teak := treeCrop("Plant_TreeTeak", 32.5, 60)
		poplar := treeCrop("Plant_TreePoplar", 15, 27)
		// More wood a tree, but over twice the land time: a worse price.
		slow := treeCrop("Plant_TreeSlow", 70, 90)
		plan, ok := PlanFieldByResource(treeRequest(100, oak, teak, poplar, slow))
		if !ok || plan.Crop.Name != "Plant_TreeTeak" {
			t.Fatal(plan.Explain())
		}
		fraction, ok := TreeFellFraction([]CropChoice{oak, teak, poplar, slow}, "WoodLog", domain.Known("TemperateForest"), domain.Known(int32(8)))
		if !ok || fraction != 0.4 {
			t.Fatalf("fell fraction %v %v", fraction, ok)
		}
	})
	t.Run("non-native species is skipped", func(t *testing.T) {
		foreign := treeCrop("Plant_TreePalm", 12, 30)
		foreign.WildBiomes = domain.Known([]string{"TropicalRainforest"})
		plan, ok := PlanFieldByResource(treeRequest(100, foreign, oak))
		if !ok || plan.Crop.Name != "Plant_TreeOak" {
			t.Fatal(plan.Explain())
		}
		if _, ok := PlanFieldByResource(treeRequest(100, foreign)); ok {
			t.Fatal("planned a species the biome does not grow wild")
		}
	})
	t.Run("refusals", func(t *testing.T) {
		for name, mutate := range map[string]func(*ResourceFieldRequest){
			"no research":     func(r *ResourceFieldRequest) { r.Choices[0].Available = domain.Known(false) },
			"unskilled":       func(r *ResourceFieldRequest) { r.GrowerSkill = domain.Known(int32(5)) },
			"unknown skill":   func(r *ResourceFieldRequest) { r.GrowerSkill = domain.Unknown[int32]() },
			"unknown biome":   func(r *ResourceFieldRequest) { r.Climate.Biome = domain.Unknown[string]() },
			"other biome":     func(r *ResourceFieldRequest) { r.Climate.Biome = domain.Known("Tundra") },
			"no wild biomes":  func(r *ResourceFieldRequest) { r.Choices[0].WildBiomes = domain.Unknown[[]string]() },
			"unknown density": func(r *ResourceFieldRequest) { r.Choices[0].BlockAdjacentSow = domain.Unknown[bool]() },
		} {
			r := treeRequest(100, oak)
			r.Choices = []CropChoice{oak}
			mutate(&r)
			if plan, ok := PlanFieldByResource(r); ok {
				t.Fatalf("%s: planned %s", name, plan.Explain())
			}
		}
	})
	t.Run("no fell fraction without a sowable species", func(t *testing.T) {
		if _, ok := TreeFellFraction([]CropChoice{oak}, "WoodLog", domain.Known("Tundra"), domain.Known(int32(8))); ok {
			t.Fatal("gated chop on a species the colony cannot sow")
		}
	})
}

func TestStandingTreeYieldCountsLatticeTrees(t *testing.T) {
	oak := treeCrop("Plant_TreeOak", 30, 46)
	// 20 cells hold 5 trees of 23 wood at the fell point.
	if got := StandingFieldYield(oak, "WoodLog", domain.Known(uint32(20))); math.Abs(got-115) > 1e-9 {
		t.Fatalf("standing yield %v", got)
	}
}

func TestGrowerSkill(t *testing.T) {
	skill := func(pawns ...WorkPawn) (int32, bool) {
		return GrowerSkill(domain.Known(pawns)).Value()
	}
	team := workTeam(false)
	if got, ok := skill(team...); !ok || got != 15 {
		t.Fatalf("best Plants level %v %v", got, ok)
	}
	if got, ok := skill(team[0]); !ok || got != 1 {
		t.Fatalf("one colonist %v %v", got, ok)
	}
	off := testWorkPawn("away", false, false, []WorkSkill{{Name: "Plants", Level: 18}})
	off.Available = domain.Known(false)
	if got, _ := skill(team[0], off); got != 1 {
		t.Fatalf("an unavailable colonist sows nothing: %v", got)
	}
	unread := testWorkPawn("unread", false, false, nil)
	unread.Skills = domain.Unknown[[]WorkSkill]()
	if _, ok := skill(team[0], unread); ok {
		t.Fatal("an unread pawn must leave the skill unknown")
	}
}
