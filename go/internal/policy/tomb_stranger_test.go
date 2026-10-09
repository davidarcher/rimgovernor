package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func strangerCorpse(id string) WasteItem {
	return WasteItem{ID: id, Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger}
}

func TestStrangerStagedUnderTheCapWhenFunded(t *testing.T) {
	plan, room := tombFixture()
	waste := []WasteItem{strangerCorpse("Corpse_s1")}
	step := NextTombStep(plan, waste, nil, testShapes, true, StrangerTomb{Live: 3, Funded: true})
	if step.Kind != TombReconcile || !step.Room.Same(room) || step.Dead != 1 {
		t.Fatalf("stranger under the cap: %+v", step)
	}
}

func TestStrangerRefusedAtTheCapUnfundedOrUnread(t *testing.T) {
	plan, _ := tombFixture()
	waste := []WasteItem{strangerCorpse("Corpse_s1")}
	for name, s := range map[string]StrangerTomb{
		"capped":   {Live: StrangerTombStackCap, Funded: true},
		"unfunded": {Live: 0},
		"unread":   {},
	} {
		if step := NextTombStep(plan, waste, nil, testShapes, true, s); step.Kind != TombNone || step.Dead != 0 {
			t.Fatalf("%s: %+v", name, step)
		}
	}
	// No sarcophagus can be had: the grave path never takes a stranger.
	if step := NextTombStep(plan, waste, nil, testShapes, false, StrangerTomb{Funded: true}); step.Kind != TombNone {
		t.Fatalf("grave path: %+v", step)
	}
	// The morgue still takes the refused stranger.
	if !MorgueWaiting(waste) {
		t.Fatal("morgue did not take the refused stranger")
	}
}

func TestStrangerStagingIsBoundedByTheStacksLeft(t *testing.T) {
	plan, _ := tombFixture()
	waste := []WasteItem{strangerCorpse("a"), strangerCorpse("b"), strangerCorpse("c")}
	step := NextTombStep(plan, waste, nil, testShapes, true, StrangerTomb{Live: 3, Funded: true})
	if step.Dead != 1 {
		t.Fatalf("one stack left owes one body: %+v", step)
	}
}

func TestColonistPathUnchangedByStrangers(t *testing.T) {
	plan, room := tombFixture()
	waste := []WasteItem{{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseColonist}, strangerCorpse("Corpse_s1")}
	step := NextTombStep(plan, waste, nil, testShapes, true, StrangerTomb{})
	if step.Kind != TombReconcile || !step.Room.Same(room) || step.Dead != 1 {
		t.Fatalf("colonist with strangers refused: %+v", step)
	}
}

func TestKnowBuriedStacks(t *testing.T) {
	pawn := func(id string, offset float64) MoodPawn {
		return MoodPawn{ID: PawnID(id), Thoughts: domain.Known([]MoodThought{{Def: KnowBuriedInSarcophagusThought, Offset: offset}})}
	}
	for offset, want := range map[float64]int{4: 1, 6: 2, 7: 3, 7.5: 4} {
		if got, known := KnowBuriedStacks(domain.Known([]MoodPawn{pawn("a", 0), pawn("b", offset)})); !known || got != want {
			t.Fatalf("offset %v: %d %v, want %d", offset, got, known, want)
		}
	}
	if got, known := KnowBuriedStacks(domain.Known([]MoodPawn{{ID: "a", Thoughts: domain.Known([]MoodThought{})}})); !known || got != 0 {
		t.Fatalf("no memory: %d %v", got, known)
	}
	if _, known := KnowBuriedStacks(domain.Known([]MoodPawn{{ID: "a"}})); known {
		t.Fatal("unknown thoughts read as known")
	}
	if _, known := KnowBuriedStacks(domain.Unknown[[]MoodPawn]()); known {
		t.Fatal("unknown census read as known")
	}
}

func TestPositiveThoughtsAreCarriedButNeverPressure(t *testing.T) {
	thoughts := domain.Known([]MoodThought{{Def: "EnvironmentDark", Offset: -4}, {Def: "Slighted", Offset: -1}, {Def: KnowBuriedInSarcophagusThought, Offset: 20}})
	if err := validateMoodThoughts(thoughts); err != nil {
		t.Fatal(err)
	}
	rows := moodProvisioning(thoughts)
	if len(rows) != 1 || rows[0].Concern != MaintainLighting || rows[0].Offset != -4 {
		t.Fatalf("positive offset skewed the dominance test: %+v", rows)
	}
	if psychicDroneMargin(domain.Known([]MoodThought{{Def: PsychicDroneThought, Offset: 5}})) != 0 {
		t.Fatal("positive drone offset widened the margin")
	}
}
