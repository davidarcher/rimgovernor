package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func artBench(bills ...ExistingProductionBill) ProductionBench {
	return ProductionBench{ID: "TableSculpting_1", Token: domain.Known("tok"), Usable: domain.Known(true),
		Recipes: []ProductionRecipe{{Name: SculptureRecipe, Available: domain.Known(true)}}, Bills: bills}
}

func artProfile(id PawnID, level int, passion string) PawnProfile {
	return PawnProfile{ID: id, Skills: map[string]ProfileSkill{"Artistic": {Name: "Artistic", Level: level, Passion: passion}}, Incapable: map[WorkType]bool{}}
}

func TestSelectArtBills(t *testing.T) {
	colonists := domain.Known[int64](3)
	artists := Artists([]PawnProfile{artProfile("b", 2, "Minor"), artProfile("a", 8, ""), artProfile("c", 6, "")})
	if len(artists) != 2 || artists[0] != "a" || artists[1] != "b" {
		t.Fatalf("artists = %v", artists)
	}
	for _, tc := range []struct {
		name    string
		artists []PawnID
		bench   ProductionBench
		want    []string
	}{
		{"two artists get two restricted bills", artists, artBench(), []string{"a", "b"}},
		{"no qualifier gets no bill", Artists([]PawnProfile{artProfile("c", 6, "")}), artBench(), nil},
		{"an active pinned bill stands", artists, artBench(ExistingProductionBill{ID: "Bill_1", Recipe: SculptureRecipe, Worker: domain.Known("a"), Active: domain.Known(true)}), []string{"b"}},
		{"a finished pinned bill is renewed", artists, artBench(ExistingProductionBill{ID: "Bill_1", Recipe: SculptureRecipe, Worker: domain.Known("a"), Active: domain.Known(false)}), []string{"a", "b"}},
		{"an unrestricted bill pins no one", artists, artBench(ExistingProductionBill{ID: "Bill_1", Recipe: SculptureRecipe, Worker: domain.Known(""), Active: domain.Known(true)}), []string{"a", "b"}},
	} {
		got := SelectArtBills(domain.Known([]ProductionBench{tc.bench}), colonists, tc.artists, ArtDemand{})
		if len(got) != len(tc.want) {
			t.Fatalf("%s: %+v", tc.name, got)
		}
		for i, s := range got {
			if s.Worker != tc.want[i] || s.Bench != "TableSculpting_1" || s.Recipe != SculptureRecipe || s.Mode != domain.GearBatch || s.Target != 1 || s.Token != "tok" {
				t.Fatalf("%s: %+v", tc.name, s)
			}
		}
	}
	// No art bench: no bill.
	if got := SelectArtBills(domain.Known([]ProductionBench{}), colonists, artists, ArtDemand{}); len(got) != 0 {
		t.Fatalf("no bench: %+v", got)
	}
}

func TestMaintainArtNeedsARoomAndAnArtist(t *testing.T) {
	f := stableRoutine()
	f.SculptureRoomsOwed = domain.Known(true)
	f.WorkProfiles = domain.Known([]PawnProfile{artProfile("c", 6, "")})
	if got := assessment(t, needs(t, f, RoutineLatches{}), MaintainArt); got != domain.NeedRecovered {
		t.Fatal("no artist:", got)
	}
	f.WorkProfiles = domain.Known([]PawnProfile{artProfile("a", 8, "")})
	if got := assessment(t, needs(t, f, RoutineLatches{}), MaintainArt); got != domain.NeedDeficit {
		t.Fatal("room and artist:", got)
	}
	f.SculptureRoomsOwed = domain.Unknown[bool]()
	if got := assessment(t, needs(t, f, RoutineLatches{}), MaintainArt); got != domain.NeedUnknown {
		t.Fatal("unknown rooms:", got)
	}
}

func sizedArtBench() ProductionBench {
	b := artBench()
	for _, r := range []string{"Make_SculptureLarge", "Make_SculptureGrand"} {
		b.Recipes = append(b.Recipes, ProductionRecipe{Name: r, Available: domain.Known(true)})
	}
	return b
}

// The art bill's size and stuff (#1191): Grand only with the gap, the
// space, the stock and the skill for it; otherwise the largest that fits,
// in the best stocked stuff.
func TestArtBillSizeAndStuff(t *testing.T) {
	stock := map[Resource]int64{"WoodLog": 500, "BlocksMarble": 500, "Silver": 60}
	for _, tc := range []struct {
		name          string
		demand        ArtDemand
		recipe, stuff string
	}{
		{"big gap, space, stock and skill: grand in marble", ArtDemand{Gap: 30, Fits: 3, Stock: stock, Skill: map[PawnID]int{"a": 12}}, "Make_SculptureGrand", "BlocksMarble"},
		{"no 3x3 spot: large", ArtDemand{Gap: 30, Fits: 2, Stock: stock, Skill: map[PawnID]int{"a": 12}}, "Make_SculptureLarge", "BlocksMarble"},
		{"modest gap: large", ArtDemand{Gap: 15, Fits: 3, Stock: stock, Skill: map[PawnID]int{"a": 12}}, "Make_SculptureLarge", "BlocksMarble"},
		{"mid skill: large at most", ArtDemand{Gap: 30, Fits: 3, Stock: stock, Skill: map[PawnID]int{"a": 7}}, "Make_SculptureLarge", "BlocksMarble"},
		{"passion without skill: small", ArtDemand{Gap: 30, Fits: 3, Stock: stock, Skill: map[PawnID]int{"a": 3}}, SculptureRecipe, "Silver"},
		{"small gap: small in the best stuff", ArtDemand{Gap: 5, Fits: 3, Stock: stock, Skill: map[PawnID]int{"a": 12}}, SculptureRecipe, "Silver"},
		{"stock short of grand: large", ArtDemand{Gap: 30, Fits: 3, Stock: map[Resource]int64{"WoodLog": 200}, Skill: map[PawnID]int{"a": 12}}, "Make_SculptureLarge", "WoodLog"},
		{"no stock: small in any stuff", ArtDemand{Gap: 30, Fits: 3, Skill: map[PawnID]int{"a": 12}}, SculptureRecipe, ""},
	} {
		got := SelectArtBills(domain.Known([]ProductionBench{sizedArtBench()}), domain.Known[int64](2), []PawnID{"a"}, tc.demand)
		if len(got) != 1 || got[0].Recipe != tc.recipe || got[0].Worker != "a" {
			t.Fatalf("%s: %+v", tc.name, got)
		}
		if tc.stuff == "" && len(got[0].Ingredients) != 0 || tc.stuff != "" && (len(got[0].Ingredients) != 1 || got[0].Ingredients[0] != tc.stuff) {
			t.Fatalf("%s: ingredients %v", tc.name, got[0].Ingredients)
		}
	}
	// A pinned large sculpture bill counts as the artist's bill.
	b := sizedArtBench()
	b.Bills = []ExistingProductionBill{{ID: "Bill_1", Recipe: "Make_SculptureLarge", Worker: domain.Known("a"), Active: domain.Known(true)}}
	if got := SelectArtBills(domain.Known([]ProductionBench{b}), domain.Known[int64](2), []PawnID{"a"}, ArtDemand{}); len(got) != 0 {
		t.Fatalf("pinned large: %+v", got)
	}
}

// The demand reads the first owed room's gap and its largest free square.
func TestNewArtDemand(t *testing.T) {
	obs, rooms, _ := upgradeFixture(t, RoomQuality{Wealth: 3000, Beauty: -1, Space: 25, Impressiveness: 35})
	targets := map[string]RoomTarget{"Room_1": {Room: "Room_1", Min: ImpressivenessSlightlyImpressive}}
	d := NewArtDemand(domain.Known(obs), targets, rooms, nil, []PawnProfile{artProfile("a", 9, "")})
	if d.Gap != ImpressivenessSlightlyImpressive-35 || d.Fits < 2 || d.Skill["a"] != 9 {
		t.Fatalf("demand = %+v", d)
	}
	if d := NewArtDemand(domain.Unknown[SleepingObservation](), targets, rooms, nil, nil); d.Gap != 0 || d.Fits != 0 {
		t.Fatalf("unknown = %+v", d)
	}
}

func TestInspiredArtistGetsPriorityBill(t *testing.T) {
	bench := artBench()
	bench.Recipes = append(bench.Recipes, ProductionRecipe{Name: InspiredArtRecipe, Available: domain.Known(true)})
	inspired := artProfile("b", 8, "")
	inspired.Inspiration = domain.Known(InspiredCreativity)
	plain := artProfile("a", 8, "")
	plain.Inspiration = domain.Known("")
	notArtist := artProfile("c", 2, "")
	notArtist.Inspiration = domain.Known(InspiredCreativity)
	profiles := []PawnProfile{plain, inspired, notArtist}
	benches := domain.Known([]ProductionBench{bench})
	got := SelectInspiredArtBills(benches, InspiredArtists(profiles))
	if len(got) != 1 || got[0].Worker != "b" || got[0].Recipe != InspiredArtRecipe || got[0].Target != 1 || got[0].Mode != domain.GearBatch {
		t.Fatalf("inspired: %+v", got)
	}
	// An active large bill pinned to the artist stands.
	bench.Bills = []ExistingProductionBill{{ID: "Bill_1", Recipe: InspiredArtRecipe, Worker: domain.Known("b"), Active: domain.Known(true)}}
	if got := SelectInspiredArtBills(domain.Known([]ProductionBench{bench}), InspiredArtists(profiles)); len(got) != 0 {
		t.Fatalf("active: %+v", got)
	}
	// An uninspired colony is unchanged.
	if got := InspiredArtists([]PawnProfile{plain, artProfile("d", 9, "")}); len(got) != 0 {
		t.Fatalf("uninspired: %v", got)
	}
	// The inspired artist holds MaintainArt open with no room owed.
	f := stableRoutine()
	f.SculptureRoomsOwed = domain.Known(false)
	f.WorkProfiles = domain.Known(profiles)
	if got := assessment(t, needs(t, f, RoutineLatches{}), MaintainArt); got != domain.NeedDeficit {
		t.Fatal("inspired, no room:", got)
	}
	f.WorkProfiles = domain.Known([]PawnProfile{plain})
	if got := assessment(t, needs(t, f, RoutineLatches{}), MaintainArt); got != domain.NeedRecovered {
		t.Fatal("uninspired, no room:", got)
	}
}
