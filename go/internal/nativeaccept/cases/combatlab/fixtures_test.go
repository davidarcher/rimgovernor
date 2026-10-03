package combatlab

import (
	"encoding/json"
	"fmt"
	"reflect"
	"slices"
	"testing"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// The lab is LabMapSize square; its centre is what test/lab_start replies.
func labCenter() (int, int) { return na.LabMapSize / 2, na.LabMapSize / 2 }

func TestFixturesFitTheLabWithoutOverlap(t *testing.T) {
	cx, cz := labCenter()
	for _, name := range append(slices.Clone(Names), "lab-prison", "lab-infestation", "lab-horror") {
		f, err := Build(name, cx, cz)
		if err != nil {
			t.Fatal(err)
		}
		used := map[string]string{}
		claim := func(what string, x, z int) {
			if x < 0 || z < 0 || x >= na.LabMapSize || z >= na.LabMapSize {
				t.Errorf("%s: %s at %d,%d is off the %d lab", name, what, x, z, na.LabMapSize)
			}
			key := fmt.Sprintf("%d,%d", x, z)
			if prior, ok := used[key]; ok {
				t.Errorf("%s: %s and %s share %s", name, what, prior, key)
			}
			used[key] = what
		}
		for _, th := range f.Things {
			claim(th.Def, th.X, th.Z)
		}
		seen := map[int]bool{}
		for _, p := range f.Pawns {
			if p.Side == Hostile && f.Arrival != "" {
				// An arrival drops every hostile around the one cell.
				continue
			}
			claim(p.Side+" "+p.Weapon, p.X, p.Z)
			if p.Side == Colonist {
				if p.Index < 0 || p.Index >= f.Colonists || seen[p.Index] {
					t.Errorf("%s: colonist index %d bad or repeated (lab has %d)", name, p.Index, f.Colonists)
				}
				seen[p.Index] = true
			}
		}
		if len(seen) != f.Colonists {
			t.Errorf("%s: places %d of %d colonists", name, len(seen), f.Colonists)
		}
		if f.Colonists > 8 {
			t.Errorf("%s: %d colonists, test/lab_start allows 8", name, f.Colonists)
		}
		if _, err := json.Marshal(f); err != nil {
			t.Errorf("%s: %v", name, err)
		}
	}
}

// lab-choke's room must have exactly one opening, in its north wall, so
// every hostile path into the room goes through the choke cell.
func TestChokeRoomHasOneOpening(t *testing.T) {
	cx, cz := labCenter()
	f := choke(cx, cz)
	walls := map[[2]int]bool{}
	for _, th := range f.Things {
		walls[[2]int{th.X, th.Z}] = true
	}
	var gaps [][2]int
	for x := cx - chokeHalf; x <= cx+chokeHalf; x++ {
		for z := cz - chokeHalf; z <= cz+chokeHalf; z++ {
			edge := x == cx-chokeHalf || x == cx+chokeHalf || z == cz-chokeHalf || z == cz+chokeHalf
			if edge && !walls[[2]int{x, z}] {
				gaps = append(gaps, [2]int{x, z})
			}
		}
	}
	if len(gaps) != 1 || gaps[0] != [2]int{cx, cz + chokeHalf} {
		t.Fatalf("openings %v, want only %d,%d", gaps, cx, cz+chokeHalf)
	}
	for _, p := range f.Pawns {
		inside := p.X > cx-chokeHalf && p.X < cx+chokeHalf && p.Z > cz-chokeHalf && p.Z < cz+chokeHalf
		if inside != (p.Side == Colonist) {
			t.Errorf("%s at %d,%d: inside=%v", p.Side, p.X, p.Z, inside)
		}
	}
}

// lab-ranged: riflemen one empty cell apart (the wiki's spacing), each
// directly behind a sandbag, and the raiders at 25 cells.
func TestRangedLineIsSpacedBehindCover(t *testing.T) {
	cx, cz := labCenter()
	f := ranged(cx, cz)
	bags := map[[2]int]bool{}
	for _, th := range f.Things {
		bags[[2]int{th.X, th.Z}] = true
	}
	var xs []int
	for _, p := range f.Pawns {
		switch p.Side {
		case Colonist:
			if !bags[[2]int{p.X, p.Z + 1}] {
				t.Errorf("colonist at %d,%d has no sandbag north of it", p.X, p.Z)
			}
			xs = append(xs, p.X)
		case Hostile:
			if d := p.Z - (cz - 9); d != 25 {
				t.Errorf("raider at %d,%d is %d cells out, want 25", p.X, p.Z, d)
			}
		}
	}
	for i := 1; i < len(xs); i++ {
		if xs[i]-xs[i-1] != 2 {
			t.Errorf("colonists at x %v are not one empty cell apart", xs)
		}
	}
}

func TestBuildRefusesUnknownFixture(t *testing.T) {
	if _, err := Build("lab-nope", 50, 50); err == nil {
		t.Fatal("want an error")
	}
}

// lab-ranged-shield (#1153) is lab-ranged unchanged plus a fifth,
// melee-armed colonist in a shield belt.
func TestRangedShieldAddsBeltedBrawler(t *testing.T) {
	cx, cz := labCenter()
	plain, shield := ranged(cx, cz), rangedShield(cx, cz)
	if !slices.Equal(plain.Things, shield.Things) || !reflect.DeepEqual(plain.Layout, shield.Layout) {
		t.Fatal("lab-ranged-shield changes lab-ranged's line")
	}
	if len(shield.Pawns) != len(plain.Pawns)+1 || !reflect.DeepEqual(shield.Pawns[:len(plain.Pawns)], plain.Pawns) {
		t.Fatalf("pawns %+v, want lab-ranged's plus one", shield.Pawns)
	}
	tank := shield.Pawns[len(plain.Pawns)]
	if tank.Side != Colonist || tank.Index != 4 || shield.Colonists != 5 || tank.Apparel != "Apparel_ShieldBelt" || tank.Weapon != longsword {
		t.Errorf("tank %+v, colonists %d", tank, shield.Colonists)
	}
}
