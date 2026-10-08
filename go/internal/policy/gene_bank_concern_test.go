package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestMaintainGeneBankRaisesGoalOnlyWhereTheNeedIsKnown(t *testing.T) {
	t.Parallel()
	f := stableRounds()
	f.GeneBankOwed = domain.Unknown[bool]()
	r := needs(t, f, RoundsLatches{})
	if hasNeed(r, MaintainGeneBank) || assessment(t, r, MaintainGeneBank) != domain.FindingUnclear {
		t.Fatal("no gene-bank fact: an unknown assessment and no goal", r)
	}
	f.AvailableMethods = domain.Known([]ConcernID{MaintainGeneBank})
	f.GeneBankOwed = domain.Known(true)
	r = needs(t, f, r.Latches)
	if !hasNeed(r, MaintainGeneBank) {
		t.Fatal("an owed bank opens the goal", r)
	}
	if got, _ := RoundsDeficit(MaintainGeneBank, f, RoundsPolicy{}).Value(); got != 1 {
		t.Fatal("owed is a full deficit", got)
	}
	f.GeneBankOwed = domain.Known(false)
	r = needs(t, f, r.Latches)
	if hasNeed(r, MaintainGeneBank) || assessment(t, r, MaintainGeneBank) != domain.FindingMet {
		t.Fatal("no bank owed settles the goal", r)
	}
	if got, ok := RoundsDeficit(MaintainGeneBank, f, RoundsPolicy{}).Value(); !ok || got != 0 {
		t.Fatal("none owed is no deficit", got, ok)
	}
	f.GeneBankOwed = domain.Unknown[bool]()
	if _, ok := RoundsDeficit(MaintainGeneBank, f, RoundsPolicy{}).Value(); ok {
		t.Fatal("an unread need is an unknown deficit")
	}
}

func TestGeneBankNeedFromBanksAndLoosePacks(t *testing.T) {
	t.Parallel()
	bank := func(capacity int32, held int32) GeneBankSlot {
		return GeneBankSlot{Capacity: domain.Known(capacity), Held: held}
	}
	loose, held, unknownPack := domain.Known(true), domain.Known(false), domain.Unknown[bool]()
	for name, c := range map[string]struct {
		facts domain.Fact[GeneBankFacts]
		known bool
		owed  bool
	}{
		"section unread":         {domain.Unknown[GeneBankFacts](), false, false},
		"no packs, no bank":      {domain.Known(GeneBankFacts{}), true, false},
		"loose pack, no bank":    {domain.Known(GeneBankFacts{Packs: []domain.Fact[bool]{loose}}), true, true},
		"loose pack, room":       {domain.Known(GeneBankFacts{Banks: []GeneBankSlot{bank(4, 3)}, Packs: []domain.Fact[bool]{held, held, held, loose}}), true, false},
		"loose pack, bank full":  {domain.Known(GeneBankFacts{Banks: []GeneBankSlot{bank(4, 4)}, Packs: []domain.Fact[bool]{held, held, held, held, loose}}), true, true},
		"two banks hold six":     {domain.Known(GeneBankFacts{Banks: []GeneBankSlot{bank(4, 4), bank(4, 0)}, Packs: []domain.Fact[bool]{loose, loose, loose, loose}}), true, false},
		"all banked":             {domain.Known(GeneBankFacts{Banks: []GeneBankSlot{bank(4, 2)}, Packs: []domain.Fact[bool]{held, held}}), true, false},
		"unknown pack":           {domain.Known(GeneBankFacts{Banks: []GeneBankSlot{bank(4, 0)}, Packs: []domain.Fact[bool]{unknownPack}}), false, false},
		"unknown bank capacity":  {domain.Known(GeneBankFacts{Banks: []GeneBankSlot{{Capacity: domain.Unknown[int32]()}}, Packs: []domain.Fact[bool]{loose}}), false, false},
		"over-held bank no room": {domain.Known(GeneBankFacts{Banks: []GeneBankSlot{bank(4, 6)}, Packs: []domain.Fact[bool]{loose}}), true, true},
	} {
		owed, known := GeneBankNeed(c.facts).Value()
		if known != c.known || owed != c.owed {
			t.Errorf("%s: owed %v known %v", name, owed, known)
		}
	}
}
