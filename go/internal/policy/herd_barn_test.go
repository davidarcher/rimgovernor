package policy

import (
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestBarnCellsAreTheStandingBarnInterior(t *testing.T) {
	plan := herdTestPlan(t, 20)
	barn := plan.HerdRooms(PlannedBarn)[0]
	if got := plan.BarnCells(standing(barn)); len(got) != len(rectCells(barn.Interior)) {
		t.Fatal("a standing barn's area is its interior", len(got))
	}
	if got := plan.BarnCells(RoomObservation{}); len(got) != 0 {
		t.Fatal("a barn that is not a room yet has no area", len(got))
	}
}

// barnShelterFacts is a herd of two pen animals (a muffalo and a thrumbo) with a
// Barn area, the given conditions and outdoor temperature.
func barnShelterFacts(temp float64, conditions ...string) RoundsFacts {
	pen := func(id string, def Resource, area string) UpkeepAnimal {
		a := planAnimal(id, def, "Male")
		a.RequiresPen, a.SupportsAreas, a.AllowedArea = domain.Known(true), domain.Known(true), domain.Known(area)
		return a
	}
	rows := []DisasterCondition{}
	for _, c := range conditions {
		rows = append(rows, DisasterCondition{ID: c, Definition: c})
	}
	race := func(def Resource, lo, hi float64) AnimalRace {
		return AnimalRace{Def: def, Comfort: domain.Known(AnimalComfort{Min: lo, Max: hi})}
	}
	var f RoundsFacts
	f.AnimalUpkeep.Animals = domain.Known([]UpkeepAnimal{pen("a1", "Muffalo", ""), pen("a2", "Thrumbo", "")})
	f.AnimalUpkeep.AnimalRaces = AnimalRaceCatalog{Races: map[Resource]AnimalRace{"Muffalo": race("Muffalo", -40, 50), "Thrumbo": race("Thrumbo", -10, 30)}}
	f.DisasterConditions, f.OutdoorTemperature, f.BarnArea = domain.Known(rows), domain.Known(temp), domain.Known("Area_Barn")
	f.Hostiles = domain.Known(int64(0))
	f.PaddockClosed = domain.Known(true)
	return f
}

func TestAnimalAreasByKindAndRing(t *testing.T) {
	animal := func(id string, pen, bonded, predator bool, area string) UpkeepAnimal {
		a := planAnimal(id, "Muffalo", "Male")
		a.RequiresPen, a.Bonded = domain.Known(pen), domain.Known(bonded)
		a.SupportsAreas, a.AllowedArea = domain.Known(true), domain.Known(area)
		a.Herd.Predator = predator
		return a
	}
	cases := []struct {
		name   string
		animal UpkeepAnimal
		closed domain.Fact[bool]
		want   string
		moves  bool
	}{
		{"roamer ring open", animal("a", true, false, false, ""), domain.Known(false), "Area_Barn", true},
		{"roamer ring open stays", animal("a", true, false, false, "Area_Barn"), domain.Known(false), "", false},
		{"roamer ring closed released", animal("a", true, false, false, "Area_Barn"), domain.Known(true), "", true},
		{"roamer ring closed free", animal("a", true, false, false, ""), domain.Known(true), "", false},
		{"companion ring open", animal("a", false, true, false, ""), domain.Known(false), "Area_Companion", true},
		{"companion ring closed", animal("a", false, true, false, ""), domain.Known(true), "Area_Companion", true},
		{"companion in place", animal("a", false, true, false, "Area_Companion"), domain.Known(true), "", false},
		{"unbonded non-roamer", animal("a", false, false, false, ""), domain.Known(true), "", false},
		{"predator ring open", animal("a", false, false, true, ""), domain.Known(false), "Area_Wild", true},
		{"bonded cat ring open", animal("a", false, true, true, ""), domain.Known(false), "Area_Companion", true},
		{"bonded cat ring closed", animal("a", false, true, true, ""), domain.Known(true), "Area_Companion", true},
		{"bonded cat in place", animal("a", false, true, true, "Area_Companion"), domain.Known(true), "", false},
		{"bonded tamed warg", animal("a", true, true, true, ""), domain.Known(true), "Area_Wild", true},
		{"tamed warg ring closed", animal("a", true, false, true, ""), domain.Known(true), "Area_Wild", true},
		{"tamed warg leaves the barn", animal("a", true, false, true, "Area_Barn"), domain.Known(false), "Area_Wild", true},
		{"unknown ring roamer", animal("a", true, false, false, ""), domain.Unknown[bool](), "", false},
		{"unknown ring companion", animal("a", false, true, false, ""), domain.Unknown[bool](), "", false},
		{"unknown ring predator", animal("a", true, false, true, ""), domain.Unknown[bool](), "", false},
		{"vet room left alone", animal("a", true, false, true, "Area_Vet"), domain.Known(true), "", false},
	}
	for _, c := range cases {
		f := barnShelterFacts(20)
		f.AnimalUpkeep.Animals = domain.Known([]UpkeepAnimal{c.animal})
		f.PaddockClosed = c.closed
		f.CompanionArea, f.WildArea = domain.Known("Area_Companion"), domain.Known("Area_Wild")
		got, err := f.AnimalShelterChoice()
		if err != nil || (got.Method != "") != c.moves || got.Argument != c.want {
			t.Errorf("%s: %+v %v", c.name, got, err)
		}
	}
}

func TestPaddockAndWildCellsPartitionTheMap(t *testing.T) {
	plan := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	yard := plan.PaddockCells()
	if len(yard) == 0 {
		t.Fatal("a ringed plan has a yard")
	}
	bounds := Bounds{Width: 400, Height: 400}
	if got := plan.WildCells(bounds); len(got)+len(yard) != 400*400 {
		t.Fatal("wild plus yard is the map", len(got), len(yard))
	}
	if got := (LayoutPlan{}).WildCells(bounds); len(got) != 0 {
		t.Fatal("no ring, no wild area", len(got))
	}
}

// moveAll applies a choice to the animal rows the way native does.
func moveAll(f RoundsFacts, c HusbandryChoice) RoundsFacts {
	rows, _ := f.AnimalUpkeep.Animals.Value()
	out := append([]UpkeepAnimal(nil), rows...)
	for i := range out {
		if out[i].ID == c.Animal {
			out[i].AllowedArea = domain.Known(c.Argument)
		}
	}
	f.AnimalUpkeep.Animals = domain.Known(out)
	return f
}

func TestAnimalShelterMovesPenAnimalsIntoTheBarnAndReleasesThem(t *testing.T) {
	// A cold snap endangers both races: one write per cycle, lowest ID first.
	f := barnShelterFacts(20, "ColdSnap")
	for _, id := range []PawnID{"a1", "a2"} {
		c, err := f.AnimalShelterChoice()
		if err != nil || c.Animal != id || c.Method != domain.HusbandryAllowedArea || c.Argument != "Area_Barn" {
			t.Fatalf("shelter %s: %+v %v", id, c, err)
		}
		f = moveAll(f, c)
	}
	if c, err := f.AnimalShelterChoice(); err != nil || c.Reason != HusbandryNoDeficit {
		t.Fatalf("both sheltered: %+v %v", c, err)
	}
	// The snap ends: the area is re-derived (cleared), no prior state read.
	calm := barnShelterFacts(20)
	calm.AnimalUpkeep.Animals = f.AnimalUpkeep.Animals
	for _, id := range []PawnID{"a1", "a2"} {
		c, err := calm.AnimalShelterChoice()
		if err != nil || c.Animal != id || c.Method != domain.HusbandryAllowedArea || c.Argument != "" {
			t.Fatalf("release %s: %+v %v", id, c, err)
		}
		calm = moveAll(calm, c)
	}
	if c, _ := calm.AnimalShelterChoice(); c.Reason != HusbandryNoDeficit {
		t.Fatalf("both released: %+v", c)
	}
}

func TestAnimalShelterFollowsEachRacesComfortRange(t *testing.T) {
	// 35 C is above the thrumbo's range only.
	f := barnShelterFacts(35)
	c, err := f.AnimalShelterChoice()
	if err != nil || c.Animal != "a2" || c.Argument != "Area_Barn" {
		t.Fatalf("%+v %v", c, err)
	}
}

func TestAnimalShelterLeavesOtherAnimalsAlone(t *testing.T) {
	f := barnShelterFacts(20, "ToxicFallout")
	rows, _ := f.AnimalUpkeep.Animals.Value()
	rows = append([]UpkeepAnimal(nil), rows...)
	rows[0].AllowedArea = domain.Known("Area_Vet")           // in the vet room
	rows[1].Slaughter = domain.Known(true)                   // leaving
	rows = append(rows, planAnimal("a3", "Muffalo", "Male")) // no pen, no area facts
	f.AnimalUpkeep.Animals = domain.Known(rows)
	if c, err := f.AnimalShelterChoice(); err != nil || c.Reason != HusbandryNoDeficit {
		t.Fatalf("%+v %v", c, err)
	}
	f = barnShelterFacts(20, "ToxicFallout")
	f.BarnArea = domain.Known("")
	if c, err := f.AnimalShelterChoice(); err != nil || c.Reason != HusbandryNoDeficit {
		t.Fatalf("no barn area: %+v %v", c, err)
	}
}

func TestAnimalShelterFailsLoudlyOnUnknownExposure(t *testing.T) {
	f := barnShelterFacts(20)
	f.AnimalUpkeep.AnimalRaces = AnimalRaceCatalog{Races: map[Resource]AnimalRace{"Muffalo": {Def: "Muffalo"}, "Thrumbo": {Def: "Thrumbo"}}}
	if _, err := f.AnimalShelterChoice(); !errors.Is(err, ErrAnimalExposure) {
		t.Fatal("a race without a comfort range", err)
	}
	f = barnShelterFacts(20)
	f.OutdoorTemperature = domain.Unknown[float64]()
	if _, err := f.AnimalShelterChoice(); !errors.Is(err, ErrAnimalExposure) {
		t.Fatal("an unread temperature", err)
	}
	f = barnShelterFacts(20)
	f.AnimalUpkeep.AnimalRaces = AnimalRaceCatalog{}
	if _, err := f.AnimalShelterChoice(); !errors.Is(err, ErrAnimalExposure) {
		t.Fatal("a race the catalog lacks", err)
	}
}

func TestAnimalShelterFromHostileThreat(t *testing.T) {
	// A threat shelters both races in mild weather, one per cycle.
	f := barnShelterFacts(20)
	f.Hostiles = domain.Known(int64(2))
	for _, id := range []PawnID{"a1", "a2"} {
		c, err := f.AnimalShelterChoice()
		if err != nil || c.Animal != id || c.Method != domain.HusbandryAllowedArea || c.Argument != "Area_Barn" {
			t.Fatalf("shelter %s: %+v %v", id, c, err)
		}
		f = moveAll(f, c)
	}
	// The threat clears: both are released.
	f.Hostiles = domain.Known(int64(0))
	for _, id := range []PawnID{"a1", "a2"} {
		c, err := f.AnimalShelterChoice()
		if err != nil || c.Animal != id || c.Argument != "" || c.Method != domain.HusbandryAllowedArea {
			t.Fatalf("release %s: %+v %v", id, c, err)
		}
		f = moveAll(f, c)
	}
	// No standing Barn area does nothing.
	f = barnShelterFacts(20)
	f.Hostiles, f.BarnArea = domain.Known(int64(1)), domain.Known("")
	if c, err := f.AnimalShelterChoice(); err != nil || c.Reason != HusbandryNoDeficit {
		t.Fatalf("no barn area: %+v %v", c, err)
	}
}

func TestAnimalShelterThreatIsIndependentOfTheWeatherReads(t *testing.T) {
	f := barnShelterFacts(20)
	f.Hostiles = domain.Known(int64(1))
	f.OutdoorTemperature = domain.Unknown[float64]()
	f.DisasterConditions = domain.Unknown[[]DisasterCondition]()
	f.AnimalUpkeep.AnimalRaces = AnimalRaceCatalog{}
	if c, err := f.AnimalShelterChoice(); err != nil || c.Animal != "a1" || c.Argument != "Area_Barn" {
		t.Fatalf("%+v %v", c, err)
	}
}

func TestAnimalShelterFailsLoudlyOnUnknownHostiles(t *testing.T) {
	f := barnShelterFacts(20)
	f.Hostiles = domain.Unknown[int64]()
	if _, err := f.AnimalShelterChoice(); !errors.Is(err, ErrAnimalExposure) {
		t.Fatal(err)
	}
}
