package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// readyVet is a vet room with a built bed and its allowed area.
var readyVet = VetRoom{Ready: domain.Known(true), Area: domain.Known("Area_Vet")}

// fertileRow is an animal with the sterilize and area facts read.
func fertileRow(id string, def Resource, gender string) UpkeepAnimal {
	a := planAnimal(id, def, gender)
	a.Sterilized, a.SterilizeQueued = domain.Known(false), domain.Known(false)
	a.SupportsAreas, a.AllowedArea = domain.Known(true), domain.Known("")
	return a
}

// goatsAwaitingCows is a goat herd (2 bucks, 3 does) the plan will replace by
// cows it has not yet got: the goats keep working and are superseded.
func goatsAwaitingCows() []UpkeepAnimal {
	return []UpkeepAnimal{fertileRow("g1", "Goat", "Male"), fertileRow("g2", "Goat", "Male"), fertileRow("g3", "Goat", "Female"),
		fertileRow("g4", "Goat", "Female"), fertileRow("g5", "Goat", "Female")}
}

func TestHerdPlanMarksAJobHolderTheBetterRaceWillReplaceAsSuperseded(t *testing.T) {
	plan := PlanHerd(milkInput(goatHerd(), wildOf("Cow")))
	if r := plan.Roles["Goat"]; !r.Superseded || r.Retiring {
		t.Fatal(r)
	}
	// Once the cows cover the job the goats retire instead.
	plan = PlanHerd(milkInput(append(goatHerd(), planAnimal("c1", "Cow", "Male"), planAnimal("c2", "Cow", "Female")), wildOf("Cow")))
	if r := plan.Roles["Goat"]; r.Superseded || !r.Retiring {
		t.Fatal(r)
	}
	// No better race to obtain: the goats are the target, not superseded.
	if r := PlanHerd(milkInput(goatHerd())).Roles["Goat"]; r.Superseded {
		t.Fatal(r)
	}
}

func TestSterilizeSupersededRaceSurplusToItsPair(t *testing.T) {
	herd := PlanHerd(milkInput(goatsAwaitingCows(), wildOf("Cow"))).Policy
	got := sterilizeWanted(goatsAwaitingCows(), herd)
	// One buck and two does stay fertile; the lowest-ID buck and doe go.
	if len(got) != 2 || !got["g1"] || !got["g3"] {
		t.Fatal(got)
	}
}

func TestSterilizeNeverBreaksTheFertilePair(t *testing.T) {
	herd := PlanHerd(milkInput(goatHerd(), wildOf("Cow"))).Policy
	if got := sterilizeWanted(goatHerd(), herd); len(got) != 0 {
		t.Fatal("a bare pair was sterilized", got)
	}
	rows := goatsAwaitingCows()
	rows[0].Sterilized = domain.Known(true) // one buck is already sterile and the other has a bill queued: no fertile male is left
	rows[1].SterilizeQueued = domain.Known(true)
	if got := sterilizeWanted(rows, herd); len(got) != 0 {
		t.Fatal("no fertile male is left", got)
	}
}

func TestSterilizeSurplusMalesOfAPreferredRace(t *testing.T) {
	// Cows are the plan's pick: 7 bulls and 5 cows keep a ratio of one bull per five.
	rows := []UpkeepAnimal{}
	for i := range 3 {
		rows = append(rows, fertileRow("bull"+string(rune('a'+i)), "Cow", "Male"))
	}
	for i := range 5 {
		rows = append(rows, fertileRow("cow"+string(rune('a'+i)), "Cow", "Female"))
	}
	herd := PlanHerd(milkInput(rows, wildOf("Cow"))).Policy
	if herd.Roles["Cow"].Superseded || herd.Roles["Cow"].Founder {
		t.Fatal(herd.Roles["Cow"])
	}
	got := sterilizeWanted(rows, herd)
	if len(got) != 2 || !got["bulla"] || !got["bullb"] {
		t.Fatal("males beyond ceil(5/5)=1 go", got)
	}
	// A founder is never sterilized.
	founder := []UpkeepAnimal{fertileRow("bulla", "Cow", "Male"), fertileRow("bullb", "Cow", "Male")}
	if got := sterilizeWanted(founder, PlanHerd(milkInput(founder, wildOf("Cow"))).Policy); len(got) != 0 {
		t.Fatal(got)
	}
	// Unread facts and removal designations bar an animal.
	rows[0].Sterilized = domain.Unknown[bool]()
	rows[1].Slaughter = domain.Known(true)
	if got := sterilizeWanted(rows, herd); got["bulla"] || got["bullb"] {
		t.Fatal(got)
	}
}

func TestSterilizeNeedsAReadyVetRoom(t *testing.T) {
	herd := PlanHerd(milkInput(goatsAwaitingCows(), wildOf("Cow"))).Policy
	animals := domain.Known(goatsAwaitingCows())
	for _, vet := range []VetRoom{{}, {Ready: domain.Known(false), Area: domain.Known("Area_Vet")}, {Ready: domain.Known(true)},
		{Ready: domain.Unknown[bool](), Area: domain.Known("Area_Vet")}} {
		if got := SterilizeChoice(animals, herd, vet); got.Method != "" || got.Reason != HusbandryNoDeficit {
			t.Fatal(vet, got)
		}
	}
	if got := SterilizeChoice(domain.Unknown[[]UpkeepAnimal](), herd, readyVet); got.Method != "" {
		t.Fatal(got)
	}
}

// The step is derived from each cycle's facts: in, sterilize, out.
func TestSterilizeSequencesThroughTheVetRoom(t *testing.T) {
	herd := PlanHerd(milkInput(goatsAwaitingCows(), wildOf("Cow"))).Policy
	rows := goatsAwaitingCows()
	step := func() HusbandryChoice { return SterilizeChoice(domain.Known(rows), herd, readyVet) }
	if got := step(); got.Method != domain.HusbandryAllowedArea || got.Animal != "g1" || got.Argument != "Area_Vet" {
		t.Fatal("let in", got)
	}
	rows[0].AllowedArea = domain.Known("Area_Vet")
	if got := step(); got.Method != domain.HusbandrySterilize || got.Animal != "g1" {
		t.Fatal("sterilize", got)
	}
	rows[0].SterilizeQueued = domain.Known(true)
	if got := step(); got.Method != "" || got.Reason != HusbandryNoDeficit {
		t.Fatal("waits on the bill and lets nobody else in", got)
	}
	rows[0].Sterilized, rows[0].SterilizeQueued = domain.Known(true), domain.Known(false)
	if got := step(); got.Method != domain.HusbandryAllowedArea || got.Animal != "g1" || got.Argument != "" {
		t.Fatal("let out", got)
	}
	rows[0].AllowedArea = domain.Known("")
	if got := step(); got.Method != domain.HusbandryAllowedArea || got.Animal != "g3" || got.Argument != "Area_Vet" {
		t.Fatal("next animal", got)
	}
	// An animal in the room no longer wanted, with no bill, is let out.
	rows[2].AllowedArea = domain.Known("Area_Vet")
	rows[2].Slaughter = domain.Known(true)
	if got := step(); got.Method != domain.HusbandryAllowedArea || got.Animal != "g3" || got.Argument != "" {
		t.Fatal(got)
	}
	// An animal that cannot carry an allowed area is never sent.
	rows[2].AllowedArea, rows[2].Slaughter = domain.Known(""), domain.Known(false)
	rows[2].SupportsAreas = domain.Known(false)
	if got := step(); got.Method != "" {
		t.Fatal(got)
	}
}

func TestSterilizedAnimalsAreNoPartOfThePair(t *testing.T) {
	// A pair of cows and a sterile one: the sterile cow is removable even
	// though cows are at the pair size; the fertile two are not.
	rows := cows(3)
	rows[3].Sterilized = domain.Known(true)
	herd := HerdPolicy{PopulationMax: map[Resource]int64{"Cow": 3}}
	rows[1].Release = domain.Known(true)
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, domain.Known(FoodPlan{})); got.Animal != "cowa" || got.Method != domain.HusbandryCancelRelease {
		t.Fatal("a fertile cow may not leave", got)
	}
	rows[1].Release, rows[3].Release = domain.Known(false), domain.Known(true)
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, domain.Known(FoodPlan{})); got.Reason != HusbandryNoDeficit {
		t.Fatal("a sterile cow may leave", got)
	}
	// The surplus cull takes a sterile cow before breaking the pair.
	plain := cows(2)
	plain[1].Sterilized = domain.Known(true)
	got, unknown := herdSurplusCandidates(plain, map[Resource]int64{"Cow": 2}, false, HerdPolicy{})
	if unknown || len(got) != 1 || got[0].animal.ID != "cowa" {
		t.Fatal(got, unknown)
	}
	plain[1].Sterilized = domain.Known(false)
	if got, _ := herdSurplusCandidates(plain, map[Resource]int64{"Cow": 2}, false, HerdPolicy{}); len(got) != 0 {
		t.Fatal("the pair of cows stays", got)
	}
}

func TestPlanHerdPairCountsOnlyFertileAnimals(t *testing.T) {
	cow := planAnimal("c1", "Cow", "Male")
	sterile := planAnimal("c2", "Cow", "Female")
	sterile.Sterilized = domain.Known(true)
	plan := PlanHerd(milkInput([]UpkeepAnimal{cow, sterile}, wildOf("Cow")))
	if r := plan.Roles["Cow"]; !r.Founder || !r.WantFemale || r.WantMale {
		t.Fatal("a sterile female is no pair:", r)
	}
}
