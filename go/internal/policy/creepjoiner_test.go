package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// downsideDefs stands for a catalog whose downside defs add one trait and
// one hediff; the names are fixture data, not game constants.
var downsideDefs = CreepJoinerDownsides{Traits: map[string]bool{"TraitX": true}, Hediffs: map[string]bool{"HediffY": true}}

func hiddenJoiner(triggered domain.Fact[bool], traits, hediffs domain.Fact[[]string]) CreepJoinerPawn {
	return CreepJoinerPawn{CreepJoiner: domain.Known(&CreepJoiner{Form: domain.Known("Form"), Benefit: domain.Known("Benefit"), DownsideTriggered: triggered}), Traits: traits, Hediffs: hediffs}
}

func defNames(v ...string) domain.Fact[[]string] { return domain.Known(append([]string{}, v...)) }

// TestCreepJoinerDownsideRevealed: a downside shows when the game
// fired it or the pawn carries a trait or visible hediff a downside def adds
// (the catalog's defs, no names written here); otherwise it is hidden, and
// unknown while a fact it rests on is unread.
func TestCreepJoinerDownsideRevealed(t *testing.T) {
	no, yes := domain.Known(false), domain.Known(true)
	for _, tc := range []struct {
		name         string
		pawn         CreepJoinerPawn
		known, shown bool
	}{
		{"fired", hiddenJoiner(yes, defNames(), defNames()), true, true},
		{"downside trait", hiddenJoiner(no, defNames("Kind", "TraitX"), defNames()), true, true},
		{"downside hediff", hiddenJoiner(no, defNames("Kind"), defNames("HediffY")), true, true},
		{"nothing shows", hiddenJoiner(no, defNames("Kind"), defNames("Scar")), true, false},
		{"fired flag unread, nothing shows", hiddenJoiner(domain.Unknown[bool](), defNames(), defNames()), false, false},
		{"fired flag unread, a downside trait shows", hiddenJoiner(domain.Unknown[bool](), defNames("TraitX"), defNames()), true, true},
		{"hediffs unread, nothing shows", hiddenJoiner(no, defNames(), domain.Unknown[[]string]()), false, false},
		{"traits unread, nothing shows", hiddenJoiner(no, domain.Unknown[[]string](), defNames()), false, false},
		{"traits unread, a downside hediff shows", hiddenJoiner(no, domain.Unknown[[]string](), defNames("HediffY")), true, true},
	} {
		shown, known := downsideDefs.Revealed(tc.pawn).Value()
		if known != tc.known || shown != tc.shown {
			t.Errorf("%s: revealed=%v known=%v, want %v %v", tc.name, shown, known, tc.shown, tc.known)
		}
	}
}

// TestCreepJoinerUnrevealed: only a known creepjoiner whose downside
// has not shown, or cannot be read to have shown, is flagged for the gear
// constraint; a non-creepjoiner or an unread tracker is not.
func TestCreepJoinerUnrevealed(t *testing.T) {
	no, yes := domain.Known(false), domain.Known(true)
	plain := CreepJoinerPawn{CreepJoiner: domain.Known[*CreepJoiner](nil), Traits: defNames(), Hediffs: defNames()}
	unread := CreepJoinerPawn{CreepJoiner: domain.Unknown[*CreepJoiner](), Traits: defNames(), Hediffs: defNames()}
	for name, tc := range map[string]struct {
		pawn CreepJoinerPawn
		want bool
	}{
		"hidden":            {hiddenJoiner(no, defNames(), defNames()), true},
		"unreadable":        {hiddenJoiner(domain.Unknown[bool](), defNames(), defNames()), true},
		"fired":             {hiddenJoiner(yes, defNames(), defNames()), false},
		"downside trait":    {hiddenJoiner(no, defNames("TraitX"), defNames()), false},
		"not a creepjoiner": {plain, false},
		"tracker unread":    {unread, false},
	} {
		if got := downsideDefs.Unrevealed(tc.pawn); got != tc.want {
			t.Errorf("%s: Unrevealed=%v, want %v", name, got, tc.want)
		}
	}
}

// TestCreepJoinerArmsHold: only a creepjoiner whose downside has not shown
// (or cannot be read) is held back from arms, with a plain-English reason; a
// colonist known to be no creepjoiner never is, and an unread tracker holds.
func TestCreepJoinerArmsHold(t *testing.T) {
	no, yes := domain.Known(false), domain.Known(true)
	plain := CreepJoinerPawn{CreepJoiner: domain.Known[*CreepJoiner](nil), Traits: defNames(), Hediffs: defNames()}
	for _, tc := range []struct {
		name   string
		pawn   CreepJoinerPawn
		reason string
	}{
		{"not a creepjoiner", plain, ""},
		{"downside shown", hiddenJoiner(yes, defNames(), defNames()), ""},
		{"downside hidden", hiddenJoiner(no, defNames(), defNames()), "not revealed yet"},
		{"downside unreadable", hiddenJoiner(domain.Unknown[bool](), defNames(), defNames()), "could not be read"},
		{"tracker unreadable", CreepJoinerPawn{CreepJoiner: domain.Unknown[*CreepJoiner]()}, "could not be read"},
	} {
		got := downsideDefs.ArmsHold(tc.pawn)
		if tc.reason == "" && got != "" || !strings.Contains(got, tc.reason) {
			t.Errorf("%s: reason %q, want one with %q", tc.name, got, tc.reason)
		}
	}
	// With no downside def known (no catalog), a creepjoiner that has not
	// fired its downside stays held.
	if got := (CreepJoinerDownsides{}).ArmsHold(hiddenJoiner(no, defNames("TraitX"), defNames())); got == "" {
		t.Error("a creepjoiner was released without a catalog to read its downside from")
	}
}

func hand(id PawnID, p CreepJoinerPawn, available bool, weapon domain.Fact[string]) CreepJoinerHand {
	return CreepJoinerHand{Pawn: id, Facts: p, Available: domain.Known(available), Weapon: weapon}
}

// TestCreepJoinerWeaponDrops is the goal's decision over recorded colonist
// facts: a drop is owed for each available colonist held back from
// arms who holds a weapon, and for none else.
func TestCreepJoinerWeaponDrops(t *testing.T) {
	no, yes := domain.Known(false), domain.Known(true)
	hidden, shown := hiddenJoiner(no, defNames(), defNames()), hiddenJoiner(yes, defNames(), defNames())
	plain := CreepJoinerPawn{CreepJoiner: domain.Known[*CreepJoiner](nil), Traits: defNames(), Hediffs: defNames()}
	armed, unarmed := domain.Known("rifle"), domain.Known("")
	hands := []CreepJoinerHand{
		hand("z", hidden, true, armed),                // owed
		hand("a", hidden, true, domain.Known("club")), // owed, sorts first
		hand("shown", shown, true, armed),             // downside shown: may hold it
		hand("plain", plain, true, armed),             // no creepjoiner
		hand("bare", hidden, true, unarmed),           // nothing to drop
		hand("down", hidden, false, armed),            // cannot take an order now
	}
	drops, owed := downsideDefs.WeaponDrops(hands)
	if v, known := owed.Value(); !known || !v {
		t.Fatal("drops owed but not measured", owed)
	}
	if len(drops) != 2 || drops[0] != (WeaponDrop{Pawn: "a", Weapon: "club"}) || drops[1] != (WeaponDrop{Pawn: "z", Weapon: "rifle"}) {
		t.Fatal(drops)
	}
	// Nothing owed once the weapon is down or the downside shows.
	if drops, owed := downsideDefs.WeaponDrops(hands[2:]); len(drops) != 0 {
		t.Fatal(drops)
	} else if v, known := owed.Value(); !known || v {
		t.Fatal("nothing owed but not measured", owed)
	}
	// A held colonist's unread weapon leaves the measure unknown, never false.
	unread := hand("u", hidden, true, domain.Unknown[string]())
	if _, owed := downsideDefs.WeaponDrops([]CreepJoinerHand{unread}); func() bool { _, known := owed.Value(); return known }() {
		t.Fatal("an unread weapon was measured")
	}
	// Whereas an unread one of a colonist who is not held is no matter.
	if _, owed := downsideDefs.WeaponDrops([]CreepJoinerHand{hand("s", shown, true, domain.Unknown[string]())}); func() bool { v, known := owed.Value(); return !known || v }() {
		t.Fatal("a shown downside's unread weapon blocked the measure")
	}
}

// TestCreepJoinerOwedRaisesTheGoal: an owed drop raises ManageCreepJoiners,
// a measured none recovers it, an unknown raises nothing.
func TestCreepJoinerOwedRaisesTheGoal(t *testing.T) {
	raised := func(f domain.Fact[bool]) bool {
		r := stableRounds()
		r.CreepJoinerOwed = f
		res := needs(t, r, RoundsLatches{})
		for _, g := range res.Concerns {
			if g.ID == ManageCreepJoiners {
				return true
			}
		}
		return false
	}
	if !raised(domain.Known(true)) || raised(domain.Known(false)) || raised(domain.Unknown[bool]()) {
		t.Fatal("ManageCreepJoiners follows the measured fact")
	}
}

// TestHeldBackColonistIsNeverArmed: NoArms keeps a colonist out of
// every weapon decision: the equip assignment, the unarmed count the armory
// crafts for, the armory's fighter count and the fight loadout.
func TestHeldBackColonistIsNeverArmed(t *testing.T) {
	held := weaponPawn("joiner", 12)
	held.NoArms = "it is a creepjoiner whose downside is not revealed yet, so it is not armed"
	free := weaponPawn("free", 6)
	weapons := []EquipCandidateWeapon{{Thing: "rifle", Definition: "Gun_BoltActionRifle", Class: WeaponRanged, Facts: coreFacts("Gun_BoltActionRifle")}}
	pairs := AssignEquip([]EquipCandidatePawn{held, free}, weapons)
	if len(pairs) != 1 || pairs[0].Pawn != "free" {
		t.Fatal("the held-back colonist was assigned", pairs)
	}
	if got := AssignEquip([]EquipCandidatePawn{held}, weapons); len(got) != 0 {
		t.Fatal(got)
	}
	if ScoreWeapon(held, weapons[0]) != 0 || ScoreWeapon(free, weapons[0]) <= 0 {
		t.Fatal("score does not follow the hold")
	}
	if n := UnarmedFighters([]EquipCandidatePawn{held, free}, nil); n != 1 {
		t.Fatal("the held-back colonist counts as a fighter to arm", n)
	}
	if armoryFighter(held) || !armoryFighter(free) {
		t.Fatal("armory fighter ignores the hold")
	}
	if loadoutReady(LoadoutDefender{EquipCandidatePawn: held}) || !loadoutReady(LoadoutDefender{EquipCandidatePawn: free}) {
		t.Fatal("loadout readiness ignores the hold")
	}
	// A held-back colonist already holding a weapon is not swapped to a better
	// one either: dropping it is the goal's work.
	armed := held
	armed.Armed, armed.Current = domain.Known(true), &EquipCandidateWeapon{Thing: "club", Definition: "MeleeWeapon_Club", Class: WeaponMelee, Facts: coreFacts("MeleeWeapon_Club")}
	if got := AssignEquip([]EquipCandidatePawn{armed}, weapons); len(got) != 0 {
		t.Fatal(got)
	}
}
