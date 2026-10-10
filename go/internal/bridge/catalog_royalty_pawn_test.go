package bridge

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The neuroformer rows give what a trainer teaches and whether a trader sells
// it; the ability and title rows give the psycast and bedroom facts.
func TestRoyaltyRowsFromRecordedCatalog(t *testing.T) {
	catalog := sharedRecordedCatalog(t)
	colony, err := catalog.WithNeuroformerDefs(policy.RoyaltyColony{Neuroformers: map[string]policy.Neuroformer{
		"PsychicAmplifier": {Def: "PsychicAmplifier"}, "Psytrainer_Neuroquake": {Def: "Psytrainer_Neuroquake"},
	}})
	if err != nil {
		t.Fatal(err)
	}
	if got := colony.Neuroformers["Psytrainer_Neuroquake"].TeachesPsycast; got != "Neuroquake" {
		t.Fatalf("trainer teaches %q", got)
	}
	if got := colony.Neuroformers["PsychicAmplifier"].TeachesPsycast; got != "" {
		t.Fatalf("amplifier teaches %q", got)
	}
	if _, ok := colony.Neuroformers["PsychicAmplifier"].Tradeable.Value(); !ok {
		t.Fatal("tradeability unread")
	}
	if _, err := catalog.WithNeuroformerDefs(policy.RoyaltyColony{Neuroformers: map[string]policy.Neuroformer{"NoSuchThing": {}}}); err == nil {
		t.Fatal("neuroformer without a row accepted")
	}

	title, err := catalog.RoyalTitleOf("Knight", false, nil)
	if err != nil || title.Seniority == 0 || title.BedroomMinImpressiveness == 0 {
		t.Fatalf("knight %+v %v", title, err)
	}
	if ascetic, err := catalog.RoyalTitleOf("Knight", true, nil); err != nil || ascetic.BedroomMinImpressiveness != 0 || len(ascetic.BedroomThings) != 0 || ascetic.Seniority != title.Seniority {
		t.Fatalf("ascetic knight %+v %v", ascetic, err)
	}
	if _, err := catalog.RoyalTitleOf("NoSuchTitle", false, nil); err == nil {
		t.Fatal("title without a row accepted")
	}
}

// Biome diseases join the biome's disease records to their incidents' hediffs.
func TestBiomeDiseasesFromRecordedCatalog(t *testing.T) {
	catalog := sharedRecordedCatalog(t)
	diseases, err := catalog.BiomeDiseases("TemperateForest")
	if err != nil || len(diseases) == 0 || !slices.IsSorted(diseases) || !slices.Contains(diseases, "Malaria") {
		t.Fatalf("temperate forest diseases %v %v", diseases, err)
	}
	if _, err := catalog.BiomeDiseases("NoSuchBiome"); err == nil {
		t.Fatal("biome without a row accepted")
	}
}

// A refuelable's fuel is its fuelFilter's thing defs; a def without the comp has none.
func TestRefuelFuelsFromRecordedCatalog(t *testing.T) {
	catalog := sharedRecordedCatalog(t)
	if fuels, err := catalog.RefuelFuels("Campfire"); err != nil || !slices.Equal(fuels, []string{"WoodLog"}) {
		t.Fatalf("campfire fuels %v %v", fuels, err)
	}
	if fuels, err := catalog.RefuelFuels("Wall"); err != nil || len(fuels) != 0 {
		t.Fatalf("wall fuels %v %v", fuels, err)
	}
	if _, err := catalog.RefuelFuels("NoSuchThing"); err == nil {
		t.Fatal("def without a row accepted")
	}
}
