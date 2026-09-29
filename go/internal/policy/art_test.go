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
		got := SelectArtBills(domain.Known([]ProductionBench{tc.bench}), colonists, tc.artists)
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
	if got := SelectArtBills(domain.Known([]ProductionBench{}), colonists, artists); len(got) != 0 {
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
