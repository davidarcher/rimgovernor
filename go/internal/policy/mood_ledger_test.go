package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func ledgerPawn(id string, thoughts ...MoodThought) MoodLedgerPawn {
	return MoodLedgerPawn{ID: PawnID(id), Thoughts: domain.Known(thoughts), Traits: domain.Known([]string{}), Precepts: domain.Known([]string{}), Expectation: domain.Known("Moderate")}
}

func TestMoodLedgerRanksColonySourcesAndUnownedBucket(t *testing.T) {
	pawns := []MoodLedgerPawn{
		ledgerPawn("a", MoodThought{"Slighted", -4}, MoodThought{"NeedJoy", -6}, MoodThought{"KnowBuriedInSarcophagus", 5}),
		ledgerPawn("b", MoodThought{"Slighted", -4}, MoodThought{"Insulted", -3}),
		ledgerPawn("c", MoodThought{"NeedJoy", -2}),
	}
	l := BuildMoodLedger(pawns, nil)
	want := []MoodLedgerSource{
		{Def: "NeedJoy", Pawns: 2, Lost: -8, Owners: []ConcernID{EnsureComfort}, Unverified: 2},
		{Def: "Slighted", Pawns: 2, Lost: -8, Unverified: 2},
		{Def: "Insulted", Pawns: 1, Lost: -3, Unverified: 1},
	}
	if len(l.Sources) != 3 {
		t.Fatalf("sources = %+v", l.Sources)
	}
	for i, w := range want {
		g := l.Sources[i]
		if g.Def != w.Def || g.Pawns != w.Pawns || g.Lost != w.Lost || g.Unverified != w.Unverified || len(g.Owners) != len(w.Owners) {
			t.Fatalf("source %d = %+v, want %+v", i, g, w)
		}
	}
	if len(l.Unowned) != 2 || l.Unowned[0].Def != "Slighted" || l.Unowned[1].Def != "Insulted" {
		t.Fatalf("unowned bucket = %+v", l.Unowned)
	}
	lost, known := l.Pawns[0].Lost.Value()
	if !known || len(lost) != 2 || lost[0].Def != "NeedJoy" {
		t.Fatalf("pawn a loss = %+v", lost)
	}
}

func TestMoodLedgerImmunityAndExpectationGating(t *testing.T) {
	facts := map[string]ThoughtFacts{
		"Insulted":         {Def: "Insulted", NullifyingTraits: []string{"Kind"}},
		"SkyHighGrumble":   {Def: "SkyHighGrumble", MinExpectation: "High"},
		"NeedsPsychopathy": {Def: "NeedsPsychopathy", RequiredTraits: []string{"Psychopath"}},
		"Cursed":           {Def: "Cursed", NullifyingPrecepts: []string{"Blessed"}},
	}
	rows := []MoodThought{{"Insulted", -3}, {"SkyHighGrumble", -5}, {"NeedsPsychopathy", -2}, {"Cursed", -4}}
	p := ledgerPawn("a", rows...)
	p.Traits, p.Precepts = domain.Known([]string{"Kind"}), domain.Known([]string{"Blessed"})
	l := BuildMoodLedger([]MoodLedgerPawn{p}, facts)
	if len(l.Sources) != 0 {
		t.Fatalf("immune, under-expectation and unqualified pawn still loses mood: %+v", l.Sources)
	}

	p = ledgerPawn("b", rows...)
	p.Traits, p.Expectation = domain.Known([]string{"Psychopath"}), domain.Known("SkyHigh")
	l = BuildMoodLedger([]MoodLedgerPawn{p}, facts)
	if len(l.Sources) != 4 {
		t.Fatalf("applicable thoughts dropped: %+v", l.Sources)
	}
	for _, s := range l.Sources {
		if s.Unverified != 0 {
			t.Fatalf("verified thought flagged unverified: %+v", s)
		}
	}
}

func TestMoodLedgerUnknownIsNeverZero(t *testing.T) {
	facts := map[string]ThoughtFacts{
		"Insulted":       {Def: "Insulted", NullifyingTraits: []string{"Kind"}},
		"SkyHighGrumble": {Def: "SkyHighGrumble", MinExpectation: "High"},
	}
	p := ledgerPawn("a", MoodThought{"Insulted", -3}, MoodThought{"SkyHighGrumble", -5}, MoodThought{"NoFacts", -1})
	p.Traits, p.Expectation = domain.Unknown[[]string](), domain.Unknown[string]()
	unreadable := MoodLedgerPawn{ID: "b", Thoughts: domain.Unknown[[]MoodThought]()}
	l := BuildMoodLedger([]MoodLedgerPawn{p, unreadable}, facts)
	if len(l.Sources) != 3 || l.Sources[0].Lost != -5 {
		t.Fatalf("unknown attributes dropped loss: %+v", l.Sources)
	}
	for _, s := range l.Sources {
		if s.Unverified != 1 {
			t.Fatalf("source not marked unverified: %+v", s)
		}
	}
	if len(l.Unowned) != 3 {
		t.Fatalf("missing facts and unaudited defs must be unowned: %+v", l.Unowned)
	}
	if _, known := l.Pawns[1].Lost.Value(); known || l.UnknownPawns != 1 {
		t.Fatalf("unreadable thoughts read as empty: %+v", l.Pawns[1])
	}
}

func TestMoodLedgerExpectationIsAVisibleSource(t *testing.T) {
	a, b, c := ledgerPawn("a"), ledgerPawn("b"), ledgerPawn("c")
	b.Expectation, c.Expectation = domain.Known("High"), domain.Unknown[string]()
	l := BuildMoodLedger([]MoodLedgerPawn{a, b, c}, nil)
	if len(l.Expectation) != 2 || l.Expectation[0] != (MoodLedgerExpectation{"High", 1}) && l.Expectation[0] != (MoodLedgerExpectation{"Moderate", 1}) {
		t.Fatalf("expectation = %+v", l.Expectation)
	}
	if l.Expectation[0].Level != "High" || l.Expectation[1].Level != "Moderate" {
		t.Fatalf("equal counts not ordered by level: %+v", l.Expectation)
	}
}
