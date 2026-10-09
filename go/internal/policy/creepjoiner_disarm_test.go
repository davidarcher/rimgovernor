package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func disarmDefs() CreepJoinerDisarm {
	return CreepJoinerDisarm{
		Sites: map[string][]DisarmSite{"Colonist": {
			{Index: 5, Part: "Jaw", Ancestors: []int{2, 0}},
			{Index: 9, Part: "Hand", Ancestors: []int{8, 7, 0}},
			{Index: 14, Part: "Hand", Ancestors: []int{13, 12, 0}},
		}},
		InstallValue: map[string]float64{"InstallDenture": 0, "InstallWoodenHand": 1.2, "InstallBionicHand": 400},
	}
}

func installOp(recipe string, part int, medicine float64) SurgeryOperation {
	name := map[int]string{5: "Jaw", 9: "Hand", 14: "Hand"}[part]
	return SurgeryOperation{
		Recipe: domain.Known(recipe), PartIndex: domain.Known(part), PartDefName: domain.Known(name), Kind: SurgeryInstall,
		EligibleDoctors: domain.Known(1), IngredientsOnMap: domain.Known(true), Violation: domain.Known(false), MedicineValue: domain.Known(medicine),
	}
}

func removeOp(part int, added string) SurgeryOperation {
	return SurgeryOperation{
		Recipe: domain.Known("RemoveBodyPart"), PartIndex: domain.Known(part), Kind: SurgeryAmputate, AddedPart: domain.Known(added),
		EligibleDoctors: domain.Known(1), IngredientsOnMap: domain.Known(true), Violation: domain.Known(false),
	}
}

func joinerPrisoner(id string, ops []SurgeryOperation, missing ...int) PrisonerFacts {
	var parts []MissingPart
	for _, m := range missing {
		parts = append(parts, MissingPart{PartIndex: domain.Known(m)})
	}
	return PrisonerFacts{
		Pawn: domain.PawnID(id), Kind: "Colonist", CreepJoiner: domain.Known(true), Dead: domain.Known(false),
		Operations: domain.Known(ops), MissingParts: domain.Known(parts), QueuedSurgeries: domain.Known(0),
	}
}

// TestDisarmOrdersInstallTheCheapestThenRemove: an arrested
// creepjoiner is ordered, one bill at a time, the cheapest install that is no
// violation on a site (item plus medicine), the removal of an installed part
// before any further install, and nothing once every site is missing.
func TestDisarmOrdersInstallTheCheapestThenRemove(t *testing.T) {
	d := disarmDefs()
	jawOps := []SurgeryOperation{installOp("InstallDenture", 5, 8), installOp("InstallBionicJaw", 5, 8)}
	handOps := []SurgeryOperation{installOp("InstallWoodenHand", 9, 8), installOp("InstallBionicHand", 9, 8), installOp("InstallWoodenHand", 14, 8)}

	d.InstallValue["InstallBionicJaw"] = 900
	got := d.Orders([]PrisonerFacts{joinerPrisoner("p", append(append([]SurgeryOperation{}, jawOps...), handOps...))})
	want := DisarmOrder{Pawn: "p", Recipe: "InstallDenture", Part: 5}
	if len(got.Orders) != 1 || got.Orders[0] != want {
		t.Fatalf("orders = %+v, want %+v", got, want)
	}

	// Hand price beats a bionic one: the wooden hand (1.2 + 8) over 400 + 8.
	got = d.Orders([]PrisonerFacts{joinerPrisoner("p", handOps, 5)})
	if want := (DisarmOrder{Pawn: "p", Recipe: "InstallWoodenHand", Part: 9}); len(got.Orders) != 1 || got.Orders[0] != want {
		t.Fatalf("orders = %+v, want %+v", got, want)
	}

	// A removal anywhere goes before a later install; the jaw is missing.
	ops := append([]SurgeryOperation{removeOp(14, "WoodenHand")}, handOps[:2]...)
	got = d.Orders([]PrisonerFacts{joinerPrisoner("p", ops, 5)})
	if want := (DisarmOrder{Pawn: "p", Recipe: "RemoveBodyPart", Part: 14, Remove: true}); len(got.Orders) != 1 || got.Orders[0] != want {
		t.Fatalf("orders = %+v, want %+v", got, want)
	}

	// Done: every site is missing, one by itself and one by its ancestor.
	if got = d.Orders([]PrisonerFacts{joinerPrisoner("p", nil, 5, 9, 12)}); len(got.Orders) != 0 || len(got.Waiting) != 0 {
		t.Fatalf("a disarmed prisoner is owed %+v", got)
	}
}

// TestDisarmOrdersRefuseWhatTheGameFaults: a violation, no doctor, no stock, an
// unpriced recipe, an unread fact, a queued bill and a prisoner who is no
// creepjoiner order nothing; the unread and refused ones say why.
func TestDisarmOrdersRefuseWhatTheGameFaults(t *testing.T) {
	d := CreepJoinerDisarm{Sites: map[string][]DisarmSite{"Colonist": {{Index: 5, Part: "Jaw"}}}, InstallValue: map[string]float64{"InstallDenture": 0}}
	op := func(edit func(*SurgeryOperation)) PrisonerFacts {
		o := installOp("InstallDenture", 5, 0)
		edit(&o)
		return joinerPrisoner("p", []SurgeryOperation{o})
	}
	for name, tc := range map[string]struct {
		p    PrisonerFacts
		why  string
		none bool
	}{
		"violation":    {op(func(o *SurgeryOperation) { o.Violation = domain.Known(true) }), "violation", false},
		"unread":       {op(func(o *SurgeryOperation) { o.Violation = domain.Unknown[bool]() }), "could not be read", false},
		"no doctor":    {op(func(o *SurgeryOperation) { o.EligibleDoctors = domain.Known(0) }), "no eligible doctor", false},
		"no stock":     {op(func(o *SurgeryOperation) { o.IngredientsOnMap = domain.Known(false) }), "not on the map", false},
		"unpriced":     {op(func(o *SurgeryOperation) { o.Recipe = domain.Known("InstallMystery") }), "no known price", false},
		"no operation": {joinerPrisoner("p", nil), "offers no install", false},
	} {
		got := d.Orders([]PrisonerFacts{tc.p})
		if len(got.Orders) != 0 || len(got.Waiting) != 1 || !strings.Contains(got.Waiting[0], tc.why) {
			t.Errorf("%s: %+v, want a wait mentioning %q", name, got, tc.why)
		}
	}
	queued := joinerPrisoner("p", []SurgeryOperation{installOp("InstallDenture", 5, 0)})
	queued.QueuedSurgeries = domain.Known(1)
	plain := joinerPrisoner("q", []SurgeryOperation{installOp("InstallDenture", 5, 0)})
	plain.CreepJoiner = domain.Known(false)
	unreadJoiner := joinerPrisoner("r", nil)
	unreadJoiner.CreepJoiner = domain.Unknown[bool]()
	unreadOps := joinerPrisoner("s", nil)
	unreadOps.Operations = domain.Unknown[[]SurgeryOperation]()
	dead := joinerPrisoner("t", []SurgeryOperation{installOp("InstallDenture", 5, 0)})
	dead.Dead = domain.Known(true)
	got := d.Orders([]PrisonerFacts{queued, plain, unreadJoiner, unreadOps, dead})
	if len(got.Orders) != 0 || len(got.Waiting) != 2 {
		t.Fatalf("orders %+v: want none, and waits for the unread creepjoiner and unread operations only", got)
	}
	if foreign := (CreepJoinerDisarm{}).Orders([]PrisonerFacts{joinerPrisoner("p", nil)}); len(foreign.Waiting) != 1 || !strings.Contains(foreign.Waiting[0], "no melee tool part") {
		t.Fatalf("a kind with no sites: %+v", foreign)
	}
}
