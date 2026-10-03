package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func joyCast(def string, target PsycastTarget, cost float64) Psycast {
	return Psycast{Def: def, Level: domain.Known(2), PsyfocusCost: domain.Known(cost), Entropy: domain.Known(0.1), Target: target, CooldownTicks: domain.Known(60000)}
}

func caster(id string, focus float64) WorkPawn {
	return WorkPawn{ID: PawnID(id), Available: domain.Known(true), Psyfocus: domain.Known(focus), PsylinkLevel: domain.Known(2)}
}

func moodRoyalty(casts map[PawnID][]Psycast) domain.Fact[RoyaltyFacts] {
	return domain.Known(RoyaltyFacts{Psycasts: casts})
}

func TestMoodCastPicksTheCasterWithMostPsyfocus(t *testing.T) {
	royalty := moodRoyalty(map[PawnID][]Psycast{
		"amy": {joyCast("WordOfJoy", PsycastTargetPawn, 0.1)},
		"bob": {joyCast("WordOfJoy", PsycastTargetPawn, 0.1)},
		"cat": {joyCast("WordOfJoy", PsycastTargetPawn, 0.1)},
	})
	pawns := domain.Known([]WorkPawn{caster("amy", 0.6), caster("bob", 0.9), caster("cat", 0.9), caster("dan", 1)})
	got, ok := SelectMoodCast("dan", royalty, pawns, nil)
	if !ok || got != (PsycastCall{Caster: "bob", Ability: "WordOfJoy", Target: "dan"}) {
		t.Fatal(got, ok)
	}
}

func TestMoodCastKeepsThePsyfocusReserveAndSkipsExhaustedCasters(t *testing.T) {
	royalty := moodRoyalty(map[PawnID][]Psycast{
		"amy": {joyCast("WordOfJoy", PsycastTargetPawn, 0.2)},
		"bob": {joyCast("wordofjoy", PsycastTargetPawn, 0.1)},
	})
	pawns := domain.Known([]WorkPawn{caster("amy", 0.4), caster("bob", 0.5), caster("zed", 1)})
	// amy would drop below the reserve (0.4 < 0.2+0.25); bob is the caster.
	got, ok := SelectMoodCast("zed", royalty, pawns, nil)
	if !ok || got.Caster != "bob" || got.Ability != "wordofjoy" {
		t.Fatal(got, ok)
	}
	if got, ok = SelectMoodCast("zed", royalty, pawns, func(c PawnID, _ string) bool { return c == "bob" }); ok {
		t.Fatal("exhausted caster cast", got)
	}
}

func TestMoodCastHoldsOnUnreadAndUnsuitableFacts(t *testing.T) {
	ok := joyCast("WordOfJoy", PsycastTargetPawn, 0.1)
	cases := map[string]struct {
		royalty domain.Fact[RoyaltyFacts]
		pawns   domain.Fact[[]WorkPawn]
	}{
		"royalty unread":      {domain.Unknown[RoyaltyFacts](), domain.Known([]WorkPawn{caster("amy", 1), caster("zed", 1)})},
		"pawns unread":        {moodRoyalty(map[PawnID][]Psycast{"amy": {ok}}), domain.Unknown[[]WorkPawn]()},
		"psyfocus unread":     {moodRoyalty(map[PawnID][]Psycast{"amy": {ok}}), domain.Known([]WorkPawn{{ID: "amy", Available: domain.Known(true)}})},
		"caster unavailable":  {moodRoyalty(map[PawnID][]Psycast{"amy": {ok}}), domain.Known([]WorkPawn{{ID: "amy", Available: domain.Known(false), Psyfocus: domain.Known(1.0)}})},
		"availability unread": {moodRoyalty(map[PawnID][]Psycast{"amy": {ok}}), domain.Known([]WorkPawn{{ID: "amy", Psyfocus: domain.Known(1.0)}})},
		"cost unread": {moodRoyalty(map[PawnID][]Psycast{"amy": {{Def: "WordOfJoy", Target: PsycastTargetPawn, Entropy: domain.Known(0.1), CooldownTicks: domain.Known(1)}}}),
			domain.Known([]WorkPawn{caster("amy", 1)})},
		"entropy unread": {moodRoyalty(map[PawnID][]Psycast{"amy": {{Def: "WordOfJoy", Target: PsycastTargetPawn, PsyfocusCost: domain.Known(0.1), CooldownTicks: domain.Known(1)}}}),
			domain.Known([]WorkPawn{caster("amy", 1)})},
		"cooldown unread": {moodRoyalty(map[PawnID][]Psycast{"amy": {{Def: "WordOfJoy", Target: PsycastTargetPawn, PsyfocusCost: domain.Known(0.1), Entropy: domain.Known(0.1)}}}),
			domain.Known([]WorkPawn{caster("amy", 1)})},
		"target kind unread": {moodRoyalty(map[PawnID][]Psycast{"amy": {joyCast("WordOfJoy", "", 0.1)}}), domain.Known([]WorkPawn{caster("amy", 1)})},
		"self-only cast":     {moodRoyalty(map[PawnID][]Psycast{"amy": {joyCast("WordOfJoy", PsycastTargetSelf, 0.1)}}), domain.Known([]WorkPawn{caster("amy", 1)})},
		"not a mood cast":    {moodRoyalty(map[PawnID][]Psycast{"amy": {joyCast("Skip", PsycastTargetPawn, 0.1), joyCast("Berserk", PsycastTargetPawn, 0.1)}}), domain.Known([]WorkPawn{caster("amy", 1)})},
		"no psycasts known":  {moodRoyalty(map[PawnID][]Psycast{}), domain.Known([]WorkPawn{caster("amy", 1)})},
	}
	for name, c := range cases {
		if got, cast := SelectMoodCast("zed", c.royalty, c.pawns, nil); cast {
			t.Error(name, "cast", got)
		}
	}
}

func TestMoodCastNeverCastsOnItself(t *testing.T) {
	royalty := moodRoyalty(map[PawnID][]Psycast{"amy": {joyCast("WordOfJoy", PsycastTargetPawn, 0.1)}})
	if got, cast := SelectMoodCast("amy", royalty, domain.Known([]WorkPawn{caster("amy", 1)}), nil); cast {
		t.Fatal(got)
	}
}
