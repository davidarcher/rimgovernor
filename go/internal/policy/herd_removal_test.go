package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestReconcileHerdRemoval(t *testing.T) {
	for _, method := range []domain.HusbandryMethod{domain.HusbandryCancelRelease, domain.HusbandryCancelSlaughter} {
		t.Run(string(method), func(t *testing.T) {
			v := animalFixture(0)
			rows, _ := v.Animals.Value()
			rows[0].Training = []HusbandryTrainable{trainable("Obedience", true, false)}
			rows[0].Release = domain.Known(method == domain.HusbandryCancelRelease)
			rows[0].Slaughter = domain.Known(method == domain.HusbandryCancelSlaughter)
			v.Animals = domain.Known(rows)
			herd := HerdPolicy{PopulationMin: map[Resource]int64{"Muffalo": 1}, PopulationMax: map[Resource]int64{"Muffalo": 1}, Roles: jobHerd.Roles}
			// Reconstructed native facts, including after restart/save-load, must
			// choose cancellation without needing an earlier controller action.
			for range 2 {
				got := ReconcileHerdRemoval(v.Animals, herd, domain.Known(FoodPlan{}))
				if got.Method != method || got.Animal != "muffalo" {
					t.Fatal(got)
				}
			}
			rows[0].Release, rows[0].Slaughter = domain.Known(false), domain.Known(false)
			v.Animals = domain.Known(rows)
			if got := ReconcileHerdRemoval(v.Animals, herd, domain.Known(FoodPlan{})); got.Reason != HusbandryNoDeficit {
				t.Fatal(got)
			}
			wild := domain.Known([]UpkeepAnimal{wildAnimal("replacement", "Muffalo", true, false)})
			if got := SelectHusbandryMethod(v.Animals, wild, feedFine, herd, anyTamer); got.Method != domain.HusbandryTrain {
				t.Fatal(got)
			}
			rows[0].Training = nil
			if got := SelectHusbandryMethod(domain.Known(rows), wild, feedFine, herd, anyTamer); got.Method != "" {
				t.Fatal("unwanted replacement", got)
			}
			upkeep, err := ReviewAnimalUpkeep(v, AnimalUpkeepHistory{}, DefaultFoodReserveDays)
			feed, fk := upkeep.Feed.Value()
			contain, ck := upkeep.Containment.Value()
			if err != nil || !fk || !ck || len(feed) != 1 || len(contain) != 1 {
				t.Fatal(upkeep, err)
			}
		})
	}
}

// cows is a herd of one bull and n cows, none designated.
func cows(n int) []UpkeepAnimal {
	rows := []UpkeepAnimal{{ID: "bull", Definition: "Cow", Gender: "Male", Release: domain.Known(false), Bonded: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true), SafeToRelease: domain.Known(true), Herd: HerdFacts{SlaughterBarred: domain.Known(false), EatingBarred: domain.Known(false)}}}
	for i := range n {
		rows = append(rows, UpkeepAnimal{ID: PawnID("cow" + string(rune('a'+i))), Definition: "Cow", Gender: "Female", Release: domain.Known(false), Bonded: domain.Known(false), Slaughter: domain.Known(false), SafeToSlaughter: domain.Known(true), SafeToRelease: domain.Known(true), Herd: HerdFacts{SlaughterBarred: domain.Known(false), EatingBarred: domain.Known(false)}})
	}
	return rows
}

func TestPendingRemovalBudgetsAndPolicyChanges(t *testing.T) {
	rows := cows(3)
	rows[1].Release = domain.Known(true)
	herd := HerdPolicy{PopulationMax: map[Resource]int64{"Cow": 3}}
	food := domain.Known(FoodPlan{})
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Reason != HusbandryNoDeficit {
		t.Fatal(got)
	}
	herd.PopulationMax["Cow"] = 4
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Method != domain.HusbandryCancelRelease {
		t.Fatal("no surplus left", got)
	}
	herd.PopulationMax["Cow"] = 3
	rows[2].Release = domain.Known(true)
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Animal != "cowb" || got.Method != domain.HusbandryCancelRelease {
		t.Fatal("second release breaks the pair", got)
	}
	rows[1].Release, rows[1].Slaughter = domain.Known(false), domain.Known(true)
	rows[2].Release = domain.Known(false)
	herd = HerdPolicy{PopulationMax: map[Resource]int64{"Cow": 30}}
	food = domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: FoodChannel{Kind: FoodHunt, ID: "slaughter:cowa"}, Decision: FoodPlanOpen}}})
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Reason != HusbandryNoDeficit {
		t.Fatal(got)
	}
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, domain.Unknown[FoodPlan]()); got.Reason != HusbandryUnknown {
		t.Fatal(got)
	}
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, domain.Known(FoodPlan{})); got.Method != domain.HusbandryCancelSlaughter {
		t.Fatal(got)
	}
	herd.PopulationMin = map[Resource]int64{"Cow": 4}
	if got := ReconcileHerdRemoval(domain.Known(rows), herd, food); got.Method != domain.HusbandryCancelSlaughter {
		t.Fatal(got)
	}
}

func TestFoodOfferRetainsPendingSlaughterWithoutDuplicate(t *testing.T) {
	herdRows := cows(3)
	herdRows[1].Slaughter, herdRows[1].SafeToSlaughter = domain.Known(true), domain.Known(false)
	animals := domain.Known(herdRows)
	rows := []SlaughterFoodAnimal{{ID: "cowa", Race: "Cow", MeatNutrition: domain.Known(15.0), FeedPerDay: domain.Known(1.0), ReproductionDays: domain.Known(10.0)}}
	herd := HerdPolicy{}
	channels := SlaughterFoodChannels(rows, animals, herd)
	if len(channels) != 1 {
		t.Fatal(channels)
	}
	food := domain.Known(FoodPlan{Portfolio: []FoodPlanEntry{{Channel: channels[0], Decision: FoodPlanOpen}}})
	if got := ReconcileHerdRemoval(animals, herd, food); got.Reason != HusbandryNoDeficit {
		t.Fatal(got)
	}
	if got := FoodSlaughterChoice(food, animals, herd); got.Method != "" {
		t.Fatal("duplicate removal", got)
	}
}

func TestPrioritizeSlaughterChoice(t *testing.T) {
	handler := func(id PawnID, level int, incapable bool) PawnProfile {
		p := PawnProfile{ID: id, WorkSkill: testWorkSkill, Skills: map[string]ProfileSkill{"Animals": {Name: "Animals", Level: level}}}
		if incapable {
			p.Incapable = map[WorkType]bool{WorkHandling: true}
		}
		return p
	}
	v := animalFixture(0)
	rows, _ := v.Animals.Value()
	rows[0].Release, rows[0].Slaughter = domain.Known(false), domain.Known(true)
	rows[0].Herd.SlaughterBarred, rows[0].Herd.EatingBarred = domain.Known(false), domain.Known(false)
	animals := domain.Known(rows)
	two := domain.Known([]PawnProfile{handler("a", 4, false), handler("b", 9, false), handler("c", 15, true)})
	got := PrioritizeSlaughterChoice(animals, two)
	if got.Method != domain.HusbandryPrioritizeSlaughter || got.Animal != "muffalo" || got.Handler != "b" {
		t.Fatal(got)
	}
	if got := PrioritizeSlaughterChoice(animals, domain.Known([]PawnProfile{handler("c", 15, true)})); got.Method != "" || got.Reason != HusbandryNoDeficit {
		t.Fatal("no capable handler must leave the designation to native", got)
	}
	rows[0].Slaughter = domain.Known(false)
	if got := PrioritizeSlaughterChoice(domain.Known(rows), two); got.Method != "" {
		t.Fatal("an undesignated animal got an order", got)
	}
	rows[0].Slaughter, rows[0].Release = domain.Known(true), domain.Known(true)
	if got := PrioritizeSlaughterChoice(domain.Known(rows), two); got.Method != "" {
		t.Fatal("a release-marked animal got an order", got)
	}
}

// A bonded animal is never the removal pick: native refuses it for slaughter
// and release (SafeToSlaughter, SafeToRelease read false), so the unbonded
// animal of the same race goes instead (#1645). The sale and prioritized
// slaughter paths keep their own bonded check, which native does not apply
// for them (a player-designated slaughter).
func TestBondedAnimalSkippedByEveryRemovalPath(t *testing.T) {
	bonded := bondedAs(planAnimal("a1", "Cow", "None"), true)
	bonded.BondedPawns = []string{"p1"}
	bonded.SafeToSlaughter, bonded.SafeToRelease = domain.Known(false), domain.Known(false)
	free := bondedAs(planAnimal("a2", "Cow", "None"), false)
	limits := map[Resource]int64{"Cow": 1}
	got, unknown := herdSurplusCandidates([]UpkeepAnimal{bonded, free}, limits, true, HerdPolicy{})
	if unknown || len(got) != 1 || got[0].animal.ID != "a2" {
		t.Fatalf("surplus pick = %+v, want a2", got)
	}
	free.SafeToSlaughter, free.SafeToRelease = domain.Known(false), domain.Known(true)
	if got, _ := herdSurplusCandidates([]UpkeepAnimal{bonded, free}, limits, true, HerdPolicy{}); len(got) != 1 || got[0].animal.ID != "a2" || got[0].method != domain.HusbandryRelease {
		t.Fatalf("release pick = %+v, want a2", got)
	}
	retired := HerdPolicy{PopulationMax: limits, Retired: map[Resource]bool{"Cow": true}}
	free.Master, bonded.Master = domain.Known(""), domain.Known("")
	if sale := HerdSaleAnimals(domain.Known([]UpkeepAnimal{bonded, free}), retired); !reflect.DeepEqual(sale, map[PawnID]bool{"a2": true}) {
		t.Fatalf("sale = %v, want a2", sale)
	}
	bonded.Slaughter, free.Slaughter = domain.Known(true), domain.Known(true)
	handlers := domain.Known([]PawnProfile{{ID: "h", WorkSkill: testWorkSkill, Skills: map[string]ProfileSkill{"Animals": {Name: "Animals", Level: 5}}}})
	if got := PrioritizeSlaughterChoice(domain.Known([]UpkeepAnimal{bonded, free}), handlers); got.Animal != "a2" {
		t.Fatalf("prioritized slaughter = %+v, want a2", got)
	}
	if got := PrioritizeSlaughterChoice(domain.Known([]UpkeepAnimal{bonded}), handlers); got.Method != "" {
		t.Fatalf("bonded animal got a slaughter order: %+v", got)
	}
}

// A venerated or precept-barred race is never slaughtered, ordered or
// offered as food; an unread precept plans none of them.
func TestSlaughterBarredRaceIsNeverRemoved(t *testing.T) {
	rows := cows(3)
	for i := range rows {
		rows[i].Herd.SlaughterBarred = domain.Known(true)
		rows[i].Herd.Venerated = domain.Known(true)
	}
	rows[1].Slaughter, rows[1].SafeToSlaughter = domain.Known(true), domain.Known(false)
	animals := domain.Known(rows)
	handler := domain.Known([]PawnProfile{{ID: "h", WorkSkill: testWorkSkill, Skills: map[string]ProfileSkill{"Animals": {Name: "Animals", Level: 9}}}})
	if got := PrioritizeSlaughterChoice(animals, handler); got.Method != "" {
		t.Fatal("prioritized a barred slaughter", got)
	}
	food := []SlaughterFoodAnimal{{ID: "cowa", Race: "Cow", MeatNutrition: domain.Known(15.0), FeedPerDay: domain.Known(1.0), ReproductionDays: domain.Known(10.0)}}
	if got := SlaughterFoodChannels(food, animals, HerdPolicy{}); len(got) != 0 {
		t.Fatal("barred race offered as food", got)
	}
	unread := cows(3)
	unread[1].Herd.SlaughterBarred = domain.Unknown[bool]()
	unread[1].Slaughter = domain.Known(true)
	if got := PrioritizeSlaughterChoice(domain.Known(unread), handler); got.Reason != HusbandryUnknown {
		t.Fatal("unread precept must fail", got)
	}
}

// Vanilla has no rule against selling venerated animals: a barred race still
// sells (herdSurplusCandidates lists it through release).
func TestSaleIgnoresSlaughterBar(t *testing.T) {
	g := bondedAs(planAnimal("g1", "Goat", "Male"), false)
	g.Herd.SlaughterBarred, g.SafeToRelease = domain.Known(true), domain.Known(true)
	rows, plan := retiredGoatsPlan([]UpkeepAnimal{g})
	if got := HerdSaleAnimals(domain.Known(rows), plan.Policy); !got["g1"] {
		t.Fatal("slaughter bar blocked a sale", got)
	}
}

// ApplyHerdPrecepts binds slaughter and eating to the game's history events
// and reads the stance from the shared rule (#1644).
func TestApplyHerdPreceptsFromRule(t *testing.T) {
	ideo := domain.Known(ruleIdeoligion(
		PreceptDef{Name: "Venerated", Effects: []PreceptEffect{took(eventSlaughteredVeneratedAnimal, -8), took(eventAteVeneratedAnimalMeat, -6)}},
		PreceptDef{Name: "Carnivore", Effects: []PreceptEffect{took(eventAteMeat, 2)}},
	))
	plain, venerated, unread := cows(1)[0], cows(1)[0], cows(1)[0]
	plain.Herd.Venerated, venerated.Herd.Venerated = domain.Known(false), domain.Known(true)
	rows := func(i domain.Fact[Ideoligion], active bool) []UpkeepAnimal {
		got, _ := ApplyHerdPrecepts(domain.Known([]UpkeepAnimal{plain, venerated, unread}), i, active).Value()
		return got
	}
	got := rows(ideo, true)
	for i, want := range []struct{ slaughter, eating, known bool }{{false, false, true}, {true, true, true}, {false, false, false}} {
		s, sk := got[i].Herd.SlaughterBarred.Value()
		e, ek := got[i].Herd.EatingBarred.Value()
		if sk != want.known || ek != want.known || (want.known && (s != want.slaughter || e != want.eating)) {
			t.Fatalf("animal %d: slaughter %v/%v eating %v/%v, want %+v", i, s, sk, e, ek, want)
		}
	}
	// An unread ideoligion holds; a load without Ideology bars nothing.
	for _, a := range rows(domain.Unknown[Ideoligion](), true) {
		if _, k := a.Herd.SlaughterBarred.Value(); k {
			t.Fatal("unread ideoligion must hold", a.Herd)
		}
	}
	for _, a := range rows(domain.Unknown[Ideoligion](), false) {
		if b, k := a.Herd.SlaughterBarred.Value(); !k || b {
			t.Fatal("no Ideology must bar nothing", a.Herd)
		}
	}
}

// A race the ideoligion dislikes eating is not a food source, though it may
// still be slaughtered for removal.
func TestEatingBarredRaceIsNotFood(t *testing.T) {
	rows := cows(3)
	for i := range rows {
		rows[i].Herd.SlaughterBarred, rows[i].Herd.EatingBarred = domain.Known(false), domain.Known(true)
	}
	food := []SlaughterFoodAnimal{{ID: "cowa", Race: "Cow", MeatNutrition: domain.Known(15.0), FeedPerDay: domain.Known(1.0), ReproductionDays: domain.Known(10.0)}}
	if got := SlaughterFoodChannels(food, domain.Known(rows), HerdPolicy{}); len(got) != 0 {
		t.Fatal("eating-barred race offered as food", got)
	}
}
