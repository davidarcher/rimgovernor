package bridge

import (
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func TestCreationCatalogCarriesGameConstants(t *testing.T) {
	catalog, err := DecodeCreationCatalog(&o.CreationDefinitionCatalog{Defs: &d.DefSets{WeatherDefs: []*d.WeatherDef{{DefName: "Clear"}}}, GameConstants: &d.GameConstants{GenDate: &d.GenDateConstants{TicksPerDay: 60000}}})
	if err != nil {
		t.Fatal(err)
	}
	game, err := catalog.GameConstants()
	if err != nil || game.GetGenDate().GetTicksPerDay() != 60000 {
		t.Fatalf("GameConstants() = %v, %v", game, err)
	}
}

func TestGameConstantsAbsentIsAnError(t *testing.T) {
	catalog, err := DecodeCreationCatalog(&o.CreationDefinitionCatalog{Defs: &d.DefSets{WeatherDefs: []*d.WeatherDef{{DefName: "Clear"}}}})
	if err != nil {
		t.Fatal(err)
	}
	if game, err := catalog.GameConstants(); err == nil {
		t.Fatalf("a catalog without game constants returned %v", game)
	}
	if _, err := (*DefinitionCatalog)(nil).GameConstants(); err == nil {
		t.Fatal("a nil catalog returned game constants")
	}
}
