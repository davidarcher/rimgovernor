package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestMoodProvisioningDominantEnvironmentThoughts(t *testing.T) {
	p := moodPawn()
	p.Food = domain.Known(.8)
	p.Joy = domain.Known(.1)
	p.Thoughts = domain.Known([]MoodThought{{"NeedJoy", -20}, {"SleptOutside", -4}, {"Insulted", -5}})
	h := moodReview(t, p, MoodHistory{})
	if len(h.States) != 1 {
		t.Fatal(h)
	}
	s := h.States[0]
	want := []MoodProvision{{EnsureComfort, -20}, {EnsureInitialShelter, -4}}
	if len(s.Provision) != 2 || s.Provision[0] != want[0] || s.Provision[1] != want[1] {
		t.Fatalf("provision = %+v, want %+v", s.Provision, want)
	}
	proposal, err := SelectMoodMethod(s, nil)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Reason != MoodProvisioned || proposal.Goal != EnsureComfort || proposal.Need != "" {
		t.Fatalf("provisioning did not defer to the owner: %+v", proposal)
	}
	proposal, err = SelectMoodMethod(s.WithoutProvision(), nil)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Reason != MoodRelief || proposal.Need != MoodJoy {
		t.Fatalf("relief fallback lost: %+v", proposal)
	}
	deficits := MoodProvisionDeficits(h)
	if deficits[EnsureComfort] != 1 || deficits[EnsureInitialShelter] != 1 {
		t.Fatal(deficits)
	}

	// Social pressure outweighing the environment thoughts provisions nothing.
	p.Thoughts = domain.Known([]MoodThought{{"SleptOutside", -4}, {"Insulted", -5}})
	h = moodReview(t, p, MoodHistory{})
	if len(h.States[0].Provision) != 0 {
		t.Fatal("non-dominant environment pressure provisioned", h.States[0].Provision)
	}
	if MoodProvisionDeficits(h) != nil {
		t.Fatal("deficits without provisioning")
	}

	// Unknown thoughts retain the prior provisioning; known empty clears it.
	p.Thoughts = domain.Known([]MoodThought{{"AteWithoutTable", -3}})
	h = moodReview(t, p, MoodHistory{})
	p.Thoughts = domain.Unknown[[]MoodThought]()
	h = moodReview(t, p, h)
	if len(h.States[0].Provision) != 1 || h.States[0].Provision[0].Goal != EnsureComfort {
		t.Fatal("unknown thoughts dropped the retained provisioning", h.States[0].Provision)
	}
	p.Thoughts = domain.Known([]MoodThought{})
	h = moodReview(t, p, h)
	if len(h.States[0].Provision) != 0 {
		t.Fatal("cleared thoughts kept provisioning", h.States[0].Provision)
	}
}

func TestMoodProvisionValidation(t *testing.T) {
	p := moodPawn()
	p.Thoughts = domain.Known([]MoodThought{{"NeedJoy", 5}})
	if err := p.Validate(); err == nil {
		t.Fatal("positive thought accepted as pressure")
	}
	p.Thoughts = domain.Known([]MoodThought{{"NeedJoy", -5}, {"NeedJoy", -5}})
	if err := p.Validate(); err == nil {
		t.Fatal("duplicate thought accepted")
	}
	s := MoodState{Pawn: moodPawn(), Active: true, Provision: []MoodProvision{{EnsureInitialShelter, -4}, {EnsureComfort, -20}}}
	if err := (MoodHistory{States: []MoodState{s}}).Validate(); err == nil {
		t.Fatal("unordered provision accepted")
	}
	s.Provision = []MoodProvision{{EnsureComfort, -20}, {EnsureComfort, -4}}
	if err := (MoodHistory{States: []MoodState{s}}).Validate(); err == nil {
		t.Fatal("duplicate owner accepted")
	}
	for _, def := range []string{"AteWithoutTable", "NeedJoy", "SleptOutside", "SleptOnGround", "EnvironmentDark", "EnvironmentCold", "EnvironmentHot", "NeedBeauty", "NeedRoomSize"} {
		goal, ok := MoodProvisionOwner(def)
		if !ok || !MoodProvisionGoal(goal) {
			t.Fatal(def, goal)
		}
	}
	if _, ok := MoodProvisionOwner("SleptInBarracks"); ok {
		t.Fatal("bedrooms have no owner goal")
	}
}

func TestDetectRoutineRaisesProvisionOwnerDeficit(t *testing.T) {
	f := stableRoutine()
	f.ComfortRecovered, f.ComfortDeficit = domain.Known(false), domain.Known(.5)
	p := moodPawn()
	p.Food = domain.Known(.8)
	p.Thoughts = domain.Known([]MoodThought{{"NeedJoy", -20}})
	h := moodReview(t, p, MoodHistory{})
	f.Mood = h
	detected := needs(t, f, RoutineLatches{})
	found := false
	for _, g := range detected.Goals {
		if g.ID == EnsureComfort {
			found = true
			if d, k := g.Deficit.Value(); !k || d != 1 {
				t.Fatalf("comfort deficit not raised by mood pressure: %v", g.Deficit)
			}
		}
	}
	if !found {
		t.Fatal("EnsureComfort missing", detected.Goals)
	}
	// A recovered owner is not re-raised: the goal is simply absent.
	f.ComfortRecovered, f.ComfortDeficit = domain.Known(true), domain.Known(0.0)
	for _, g := range needs(t, f, RoutineLatches{}).Goals {
		if g.ID == EnsureComfort {
			t.Fatal("recovered comfort re-raised by mood pressure")
		}
	}
}
