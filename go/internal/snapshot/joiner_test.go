package snapshot

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recorded from acceptance run quest/wanderer at 04b0a98c (#749): a native
// WandererJoin letter pending on the joiner baseline at tick 15. The player
// had declared no population policy; since #1032 the bot owns the target and
// beds plus food alone admit the joiner.
const (
	wandererNoPolicy = "testdata/quest-wanderer-no-policy.json"
	wandererPawn     = domain.PawnID("Thing_Human44732")
)

func TestWandererLetterAnsweredWithoutAPlayerPolicy(t *testing.T) {
	r, err := Load(wandererNoPolicy)
	if err != nil {
		t.Fatal(err)
	}
	letter, ok := policy.SelectJoinerLetter(r.Facts.JoinerLetters, policy.JoinerCapacity(r.Facts.JoinerCapacity()))
	if !ok || letter.Pawn != wandererPawn || letter.Token == "" {
		t.Fatal("letter not selected", letter, ok)
	}
	a, err := r.Assessment(policy.MaintainPopulation)
	if err != nil || a.Need != domain.NeedDeficit {
		t.Fatal(a, err)
	}
}
