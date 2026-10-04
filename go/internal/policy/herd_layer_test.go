package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func layerRace(mtb domain.Fact[float64]) AnimalRace {
	return AnimalRace{Def: "Chicken", MateMtbHours: mtb, Products: []RaceProduct{{Kind: "eggs", Def: "EggChickenUnfertilized", Amount: domain.Known(1.0), IntervalDays: domain.Known(2.0), FertilizedDef: "EggChickenFertilized", FertilizationCountMax: 1}}}
}

func flock(roosters, hens int) []UpkeepAnimal {
	var rows []UpkeepAnimal
	for i := 1; i <= roosters; i++ {
		a := playerAnimal(fmt.Sprintf("r%d", i), "Chicken", true)
		a.Gender = "Male"
		rows = append(rows, a)
	}
	for i := 1; i <= hens; i++ {
		a := playerAnimal(fmt.Sprintf("h%d", i), "Chicken", true)
		a.Gender = "Female"
		rows = append(rows, a)
	}
	return rows
}

func removedRoosters(got []herdRemoval) int {
	n := 0
	for _, r := range got {
		if r.animal.Gender == "Male" {
			n++
		}
	}
	return n
}

func TestHerdLayerRatioDerivesFromCatalog(t *testing.T) {
	// One mating a day against half an egg a hen-day: two hens per rooster.
	layer, ok := herdLayerOf(layerRace(domain.Known(24.0)))
	if ratio, known := layer.HensPerRooster.Value(); !ok || !known || ratio != 2 {
		t.Fatal(layer, ok)
	}
	if _, ok := herdLayerOf(AnimalRace{Def: "Cow", MateMtbHours: domain.Known(24.0)}); ok {
		t.Fatal("a race without an egg comp is no layer")
	}
	unknownLayer, _ := herdLayerOf(layerRace(domain.Unknown[float64]()))
	if _, known := unknownLayer.HensPerRooster.Value(); known {
		t.Fatal("unknown mateMtbHours stays unknown")
	}
}

func TestHerdLayerKeepsRoostersBelowHenTarget(t *testing.T) {
	layer, _ := herdLayerOf(layerRace(domain.Known(24.0)))
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Chicken": 8}, Layers: map[Resource]HerdLayer{"Chicken": layer}}
	got, unknown := herdSurplusCandidates(flock(4, 6), map[Resource]int64{"Chicken": 8}, false, herd)
	// Six hens at two per rooster keep three: the fourth is the excess male.
	if unknown || removedRoosters(got) != 1 {
		t.Fatal(got, unknown)
	}
	// Over a limit of 0 the cull still stops at the ratio.
	got, _ = herdSurplusCandidates(flock(4, 6), map[Resource]int64{"Chicken": 0}, false, herd)
	if removedRoosters(got) != 1 {
		t.Fatal("roosters below the ratio are never removed", got)
	}
}

func TestHerdLayerKeepsOneRoosterAtHenTarget(t *testing.T) {
	layer, _ := herdLayerOf(layerRace(domain.Known(24.0)))
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Chicken": 6}, Layers: map[Resource]HerdLayer{"Chicken": layer}}
	got, unknown := herdSurplusCandidates(flock(3, 6), map[Resource]int64{"Chicken": 6}, false, herd)
	if unknown || removedRoosters(got) != 2 {
		t.Fatal(got, unknown)
	}
	got, _ = herdSurplusCandidates(flock(3, 6), map[Resource]int64{"Chicken": 0}, false, herd)
	if removedRoosters(got) != 2 {
		t.Fatal("one fertile rooster stays", got)
	}
}

func TestHerdLayerUnknownRatioRemovesNothing(t *testing.T) {
	layer, _ := herdLayerOf(layerRace(domain.Unknown[float64]()))
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Chicken": 6}, Layers: map[Resource]HerdLayer{"Chicken": layer}}
	if got, unknown := herdSurplusCandidates(flock(3, 6), map[Resource]int64{"Chicken": 0}, false, herd); !unknown || len(got) != 0 {
		t.Fatal(got, unknown)
	}
	if wanted := sterilizeWanted(flock(3, 6), herd); len(wanted) != 0 {
		t.Fatal("an unknown ratio sterilizes nothing", wanted)
	}
}

func TestHerdLayerWithoutEggCompKeepsTheFifthRatio(t *testing.T) {
	got, unknown := herdSurplusCandidates(flock(3, 6), map[Resource]int64{"Chicken": 6}, false, HerdPolicy{})
	if unknown || removedRoosters(got) != 1 {
		t.Fatal(got, unknown)
	}
}
