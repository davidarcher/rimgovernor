package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func squadPawn(id PawnID, age float64, shooting, melee int, passion string, incapable ...WorkType) WorkPawn {
	return WorkPawn{ID: id, Age: domain.Known(age), Incapable: domain.Known(append([]WorkType{}, incapable...)),
		Skills: domain.Known([]WorkSkill{{Name: "Shooting", Level: shooting, Passion: passion}, {Name: "Melee", Level: melee}})}
}

func TestSoldierSquadSize(t *testing.T) {
	t.Parallel()
	for colonists, want := range map[int]int{0: 1, 1: 1, 3: 1, 4: 2, 6: 2, 7: 3, 9: 3, 10: 4} {
		if got := SoldierSquadSize(colonists); got != want {
			t.Errorf("SoldierSquadSize(%d) = %d, want %d", colonists, got, want)
		}
	}
}

func TestReviewSoldierSquad(t *testing.T) {
	t.Parallel()
	pawns := []WorkPawn{
		squadPawn("a", 30, 4, 2, ""),
		squadPawn("b", 30, 2, 12, ""),
		squadPawn("c", 30, 15, 0, "", "Violent"),
		squadPawn("d", 10, 18, 0, ""),
		squadPawn("e", 30, 8, 0, ""),
		squadPawn("f", 30, 8, 0, "Major"),
	}
	squad, ok := ReviewSoldierSquad(SoldierSquad{}, pawns)
	if want := []PawnID{"b", "f"}; !ok || !reflect.DeepEqual(squad.Members, want) {
		t.Fatalf("best fighters: %v %v, want %v", ok, squad.Members, want)
	}
	if !squad.IsSoldier("b") || squad.IsSoldier("c") {
		t.Fatal("IsSoldier")
	}
	// Stable: a non-member growing past a member does not displace it.
	pawns[4] = squadPawn("e", 30, 20, 0, "")
	if again, _ := ReviewSoldierSquad(squad, pawns); !reflect.DeepEqual(again, squad) {
		t.Fatalf("unstable: %v", again.Members)
	}
	// A dead member (gone from the colonists) is replaced by the next best.
	alive := append([]WorkPawn{}, pawns[:1]...)
	alive = append(alive, pawns[2:]...)
	alive = append(alive, squadPawn("g", 30, 1, 1, ""))
	if next, _ := ReviewSoldierSquad(squad, alive); !reflect.DeepEqual(next.Members, []PawnID{"e", "f"}) {
		t.Fatalf("replacement: %v", next.Members)
	}
	// A member turned violence-incapable is replaced too.
	pawns[1] = squadPawn("b", 30, 2, 12, "", "Violent")
	if next, _ := ReviewSoldierSquad(squad, pawns); !reflect.DeepEqual(next.Members, []PawnID{"e", "f"}) {
		t.Fatalf("incapable: %v", next.Members)
	}
	// A shrunken target drops the weakest member.
	if next, _ := ReviewSoldierSquad(squad, pawns[3:]); !reflect.DeepEqual(next.Members, []PawnID{"f"}) {
		t.Fatalf("shrink: %v", next.Members)
	}
	// Unknown facts keep the previous squad.
	pawns[0].Age = domain.Unknown[float64]()
	if kept, ok := ReviewSoldierSquad(squad, pawns); ok || !reflect.DeepEqual(kept, squad) {
		t.Fatalf("unknown: %v %v", ok, kept.Members)
	}
}
