package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// psylinkRoyalty is a recorded royalty read: the psylink neuroformer with the
// given stock, craftable and tradeable flags.
func psylinkRoyalty(held int, craftable, tradeable bool) domain.Fact[RoyaltyFacts] {
	return domain.Known(RoyaltyFacts{Neuroformers: map[string]Neuroformer{
		PsylinkNeuroformer: {Def: PsylinkNeuroformer, Held: domain.Known(held), Craftable: domain.Known(craftable), Tradeable: domain.Known(tradeable)},
	}})
}

// psylinkPawn is a colonist row: availability, whether the needs block was
// read and its psylink level (0 is none: the facts stay unknown).
func psylinkPawn(id string, available, needs bool, level int) WorkPawn {
	p := WorkPawn{ID: PawnID(id), Available: domain.Known(available)}
	if needs {
		p.Rest = domain.Known(0.8)
	}
	if level > 0 {
		p.PsylinkLevel, p.Psyfocus, p.PsyfocusTarget = domain.Known(level), domain.Known(0.4), domain.Known(0.5)
	}
	return p
}

func TestPsylinkCandidatesAreAvailableColonistsWithoutPsylink(t *testing.T) {
	pawns := domain.Known([]WorkPawn{
		psylinkPawn("zed", true, true, 0),
		psylinkPawn("amy", true, true, 0),
		psylinkPawn("psycaster", true, true, 2),
		psylinkPawn("downed", false, true, 0),
		psylinkPawn("unread", true, false, 0),
		{ID: "unknown"},
	})
	got, known := PsylinkCandidates(psylinkRoyalty(1, false, false), pawns).Value()
	if !known || !slices.Equal(got, []PawnID{"amy", "zed"}) {
		t.Fatal(got, known)
	}
}

func TestPsylinkCandidatesUnknownAndAbsent(t *testing.T) {
	pawns := domain.Known([]WorkPawn{psylinkPawn("amy", true, true, 0)})
	if _, known := PsylinkCandidates(domain.Unknown[RoyaltyFacts](), pawns).Value(); known {
		t.Fatal("candidates known without the royalty read")
	}
	if _, known := PsylinkCandidates(psylinkRoyalty(0, true, true), domain.Unknown[[]WorkPawn]()).Value(); known {
		t.Fatal("candidates known without pawn rows")
	}
	// No neuroformer def in the game: nobody can be offered one.
	none := domain.Known(RoyaltyFacts{Neuroformers: map[string]Neuroformer{}})
	if got, known := PsylinkCandidates(none, pawns).Value(); !known || len(got) != 0 {
		t.Fatal(got, known)
	}
}

func TestNeuroformerNeedsAskForOneWhileACandidateWaitsAndNoneIsHeld(t *testing.T) {
	waiting := domain.Known([]PawnID{"amy"})
	for name, c := range map[string]struct {
		royalty domain.Fact[RoyaltyFacts]
		who     domain.Fact[[]PawnID]
		want    int64
	}{
		"craftable":    {psylinkRoyalty(0, true, false), waiting, 1},
		"tradeable":    {psylinkRoyalty(0, false, true), waiting, 1},
		"held":         {psylinkRoyalty(1, true, true), waiting, 0},
		"unobtainable": {psylinkRoyalty(0, false, false), waiting, 0},
		"no candidate": {psylinkRoyalty(0, true, true), domain.Known([]PawnID{}), 0},
		"unknown":      {psylinkRoyalty(0, true, true), domain.Unknown[[]PawnID](), 0},
		"no royalty":   {domain.Unknown[RoyaltyFacts](), waiting, 0},
	} {
		got := NeuroformerNeeds(map[Resource]int64{"Steel": 50}, c.royalty, c.who)
		if got[PsylinkNeuroformer] != c.want || got["Steel"] != 50 {
			t.Fatal(name, got)
		}
	}
	// A configured floor above one is never lowered.
	if got := NeuroformerNeeds(map[Resource]int64{PsylinkNeuroformer: 3}, psylinkRoyalty(0, true, true), waiting); got[PsylinkNeuroformer] != 3 {
		t.Fatal(got)
	}
	// Unread stock buys nothing.
	unread := domain.Known(RoyaltyFacts{Neuroformers: map[string]Neuroformer{PsylinkNeuroformer: {Def: PsylinkNeuroformer, Tradeable: domain.Known(true)}}})
	if got := NeuroformerNeeds(nil, unread, waiting); len(got) != 0 {
		t.Fatal(got)
	}
}

func TestNextPsylinkUseIsDeterministic(t *testing.T) {
	use, ok := NextPsylinkUse([]PawnID{"zed", "amy"}, []string{"PsychicAmplifier9", "PsychicAmplifier2"})
	if !ok || use != (PsylinkUse{Pawn: "amy", Item: "PsychicAmplifier2"}) {
		t.Fatal(use, ok)
	}
	if _, ok := NextPsylinkUse(nil, []string{"i"}); ok {
		t.Fatal("use without a candidate")
	}
	if _, ok := NextPsylinkUse([]PawnID{"amy"}, nil); ok {
		t.Fatal("use without an item")
	}
}

func TestPsylinkOwedIsMeasuredNotAssumed(t *testing.T) {
	waiting, items := domain.Known([]PawnID{"amy"}), domain.Known([]string{"PsychicAmplifier2"})
	for name, c := range map[string]struct {
		who   domain.Fact[[]PawnID]
		items domain.Fact[[]string]
		known bool
		owed  bool
	}{
		"use ready":    {waiting, items, true, true},
		"no item":      {waiting, domain.Known([]string{}), true, false},
		"item unread":  {waiting, domain.Unknown[[]string](), false, false},
		"no candidate": {domain.Known([]PawnID{}), domain.Unknown[[]string](), true, false},
		"unknown":      {domain.Unknown[[]PawnID](), items, false, false},
	} {
		owed, known := PsylinkOwed(c.who, c.items).Value()
		if known != c.known || owed != c.owed {
			t.Fatal(name, owed, known)
		}
	}
}

func TestPsylinkOwedRaisesMaintainPsylink(t *testing.T) {
	f := stableRoutine()
	f.PsylinkOwed = domain.Known(true)
	r := needs(t, f, RoutineLatches{})
	for _, g := range r.Goals {
		if g.ID == MaintainPsylink {
			return
		}
	}
	t.Fatal("owed psylink use raised no MaintainPsylink goal", r.Goals)
}
