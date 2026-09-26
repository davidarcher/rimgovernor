package snapshot

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recorded from acceptance run quest/wanderer at 04b0a98c (#749): a native
// WandererJoin letter pending on the joiner baseline, first with no
// population policy, then after the player declared maximum 20 and one
// food day. Both reviews ran at tick 15.
const (
	wandererNoPolicy = "testdata/quest-wanderer-no-policy.json"
	wandererCapacity = "testdata/quest-wanderer-capacity-declared.json"
	wandererPawn     = domain.PawnID("Thing_Human44732")
)

// Recorded from acceptance run quest/wanderer-defense at 04b0a98c (#749):
// the same letter under a policy with raid threshold 1 at 35 raid points,
// first with no built defense tier (tick 15), then after a barricade was
// built and recorded as a firing tier (tick 34554).
const (
	wandererUndefended = "testdata/quest-wanderer-undefended.json"
	wandererDefended   = "testdata/quest-wanderer-firing-cover-built.json"
)

func TestWandererLetterRefusedWithoutDefenseAboveRaidThreshold(t *testing.T) {
	if _, _, ok := joinerLetter(t, wandererUndefended); ok {
		t.Fatal("letter answered with no defense tier above the raid threshold")
	}
}

func TestWandererLetterAnsweredOnceFiringCoverIsBuilt(t *testing.T) {
	r, letter, ok := joinerLetter(t, wandererDefended)
	if !ok || letter.Token == "" {
		t.Fatal("letter not selected after firing cover", letter, ok)
	}
	a, err := r.Assessment(policy.MaintainPopulation)
	if err != nil || a.Need != domain.NeedDeficit {
		t.Fatal(a, err)
	}
}

func joinerLetter(t *testing.T, path string) (Routine, policy.JoinerLetterOffer, bool) {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	letter, ok := policy.SelectJoinerLetter(r.Facts.JoinerLetters, policy.JoinerCapacity(r.Facts.JoinerCapacity()))
	return r, letter, ok
}

func TestWandererLetterUnansweredWithoutPopulationPolicy(t *testing.T) {
	r, _, ok := joinerLetter(t, wandererNoPolicy)
	if ok {
		t.Fatal("letter answered without a population policy")
	}
	if a, err := r.Assessment(policy.MaintainPopulation); err == nil && a.Need == domain.NeedDeficit {
		t.Fatal("population deficit without capacity", a)
	}
}

func TestWandererLetterAnsweredOnceCapacityIsDeclared(t *testing.T) {
	r, letter, ok := joinerLetter(t, wandererCapacity)
	if !ok || letter.Pawn != wandererPawn || letter.Token == "" {
		t.Fatal("letter not selected", letter, ok)
	}
	a, err := r.Assessment(policy.MaintainPopulation)
	if err != nil || a.Need != domain.NeedDeficit {
		t.Fatal(a, err)
	}
}
