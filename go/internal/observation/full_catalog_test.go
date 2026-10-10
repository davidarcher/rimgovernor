package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit/recordedcatalog"
)

// TestFullCatalogRecordingDecodes pins testdata/full_catalog.pb.gz: the whole
// definition catalog of the game with every expansion, untrimmed (every
// ThingDef, TerrainDef, RecipeDef and other def row, the research projects,
// stat table and DLC sections); recordedCatalog loads it for the planning tests.
func TestFullCatalogRecordingDecodes(t *testing.T) {
	wire := recordedcatalog.Wire(t)
	catalog := recordedcatalog.Catalog(t)
	if len(catalog.ThingDefs) < 3000 || len(catalog.TerrainDefs) < 300 || len(wire.GetDefs().GetRecipeDefs()) < 400 || wire.GetGameConstants().GetTileMutatorWorker_Stockpile() == nil || !catalog.HasAnomaly() {
		t.Fatalf("recorded catalog is trimmed: %d things, %d terrains, %d recipes", len(catalog.ThingDefs), len(catalog.TerrainDefs), len(wire.GetDefs().GetRecipeDefs()))
	}
}

// TestFullCatalogRecordingDerivesRaces pins the race, meat and medicine facts Go
// derives from the recorded rows (they equalled the native values on every race
// and thing row of the recording when the native fields were deleted, #2632).
func TestFullCatalogRecordingDerivesRaces(t *testing.T) {
	catalog := recordedcatalog.Catalog(t)
	races, err := catalog.AnimalRaces()
	if err != nil {
		t.Fatal(err)
	}
	cow, ok := races.Race("Cow")
	if !ok {
		t.Fatal("no Cow race")
	}
	if v, ok := cow.MilkableMinAgeTicks.Value(); !ok || v <= 0 {
		t.Errorf("Cow milkable age %v %v", v, ok)
	}
	if v, ok := cow.AdultMinAgeTicks.Value(); !ok || v <= 0 {
		t.Errorf("Cow adult age %v %v", v, ok)
	}
	if cow.MeatDef != "Meat_Cow" || len(cow.Trainables) == 0 {
		t.Errorf("Cow meat %q trainables %v", cow.MeatDef, cow.Trainables)
	}
	for _, name := range []string{"Human", "Mech_Lancer", "Corpse_Cow"} {
		if _, ok := races.Race(policy.Resource(name)); ok {
			t.Errorf("%s is no animal race", name)
		}
	}
	if mech, insect := catalog.RaceFlags("Mech_Lancer"); !mech || insect {
		t.Errorf("Lancer flags %v %v", mech, insect)
	}
	if mech, insect := catalog.RaceFlags("Megascarab"); mech || !insect {
		t.Errorf("Megascarab flags %v %v", mech, insect)
	}
	if raw, err := catalog.RawMeat("Meat_Cow"); err != nil || !raw {
		t.Errorf("Meat_Cow raw meat %v %v", raw, err)
	}
	if medicine, err := catalog.Medicine("MedicineIndustrial"); err != nil || !medicine {
		t.Errorf("MedicineIndustrial medicine %v %v", medicine, err)
	}
	if medicine, err := catalog.Medicine("Meat_Cow"); err != nil || medicine {
		t.Errorf("Meat_Cow medicine %v %v", medicine, err)
	}
}

// TestFullCatalogRecordingCarriesGameConstants pins the recorded GameConstants:
// a const (the calendar) and a static readonly curve with points.
func TestFullCatalogRecordingCarriesGameConstants(t *testing.T) {
	game, err := recordedcatalog.Catalog(t).GameConstants()
	if err != nil {
		t.Fatal(err)
	}
	if got := game.GetGenDate().GetTicksPerDay(); got != 60000 {
		t.Errorf("GenDate.TicksPerDay = %d, want 60000", got)
	}
	if len(game.GetFoodUtility().GetFoodOptimalityEffectFromMoodCurve().GetPoints()) == 0 {
		t.Error("FoodUtility.FoodOptimalityEffectFromMoodCurve has no points")
	}
}
