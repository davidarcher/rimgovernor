package policy

import (
	"math"
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
	want := []MoodProvision{{EnsureComfort, -20}, {MaintainHousing, -4}}
	if len(s.Provision) != 2 || s.Provision[0] != want[0] || s.Provision[1] != want[1] {
		t.Fatalf("provision = %+v, want %+v", s.Provision, want)
	}
	proposal, err := SelectMoodMethod(s, nil)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Reason != MoodProvisioned || proposal.Concern != EnsureComfort || proposal.Need != "" {
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
	if deficits[EnsureComfort] != 1 || deficits[MaintainHousing] != 1 {
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
	if len(h.States[0].Provision) != 1 || h.States[0].Provision[0].Concern != EnsureComfort {
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
	if err := p.Validate(); err != nil {
		t.Fatal("positive thought refused", err)
	}
	p.Thoughts = domain.Known([]MoodThought{{"NeedJoy", math.NaN()}})
	if err := p.Validate(); err == nil {
		t.Fatal("NaN thought accepted")
	}
	p.Thoughts = domain.Known([]MoodThought{{"NeedJoy", -5}, {"NeedJoy", -5}})
	if err := p.Validate(); err == nil {
		t.Fatal("duplicate thought accepted")
	}
	s := MoodState{Pawn: moodPawn(), Active: true, Provision: []MoodProvision{{MaintainHousing, -4}, {EnsureComfort, -20}}}
	if err := (MoodHistory{States: []MoodState{s}}).Validate(); err == nil {
		t.Fatal("unordered provision accepted")
	}
	s.Provision = []MoodProvision{{EnsureComfort, -20}, {EnsureComfort, -4}}
	if err := (MoodHistory{States: []MoodState{s}}).Validate(); err == nil {
		t.Fatal("duplicate owner accepted")
	}
	for _, def := range []string{"AteWithoutTable", "NeedJoy", "SleptOutside", "SleptOnGround", "EnvironmentDark", "EnvironmentCold", "EnvironmentHot", "NeedBeauty", "NeedRoomSize"} {
		owners := thoughtOwners(def)
		if len(owners) == 0 {
			t.Fatal(def)
		}
		for _, goal := range owners {
			if !MoodProvisionConcern(goal) {
				t.Fatal(def, goal)
			}
		}
	}
	if len(thoughtOwners("SleptInBarracks")) > 0 {
		t.Fatal("bedrooms have no owner goal")
	}
	s = MoodState{Pawn: moodPawn(), Active: true, Unowned: []MoodThought{{"NeedJoy", -5}}}
	if err := (MoodHistory{States: []MoodState{s}}).Validate(); err == nil {
		t.Fatal("owned thought accepted as unowned pressure")
	}
	s.Unowned = []MoodThought{{"SleptInBarracks", -5}}
	s.Provision = []MoodProvision{{EnsureComfort, -20}}
	if err := (MoodHistory{States: []MoodState{s}}).Validate(); err == nil {
		t.Fatal("state both provisioned and unowned accepted")
	}
}

func TestMoodUnownedThoughtBlocker(t *testing.T) {
	p := moodPawn()
	p.Food = domain.Known(.8)
	p.Thoughts = domain.Known([]MoodThought{{"SleptInBarracks", -5}, {"Insulted", -3}})
	h := moodReview(t, p, MoodHistory{})
	if len(h.States) != 1 {
		t.Fatal(h)
	}
	s := h.States[0]
	if len(s.Provision) != 0 || len(s.Unowned) != 2 || s.Unowned[0] != (MoodThought{"SleptInBarracks", -5}) || s.Unowned[1] != (MoodThought{"Insulted", -3}) {
		t.Fatalf("unowned pressure not recorded: %+v", s)
	}
	if MoodProvisionDeficits(h) != nil {
		t.Fatal("unowned pressure raised a deficit")
	}
	proposal, err := SelectMoodMethod(s, nil)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Reason != MoodUnowned || proposal.Thought != "SleptInBarracks" || proposal.Need != "" {
		t.Fatalf("no explicit unowned blocker: %+v", proposal)
	}

	// A measured need still gets relief before the blocker; the blocker
	// names the remaining pressure once relief is exhausted.
	p.Joy = domain.Known(.1)
	h = moodReview(t, p, MoodHistory{})
	proposal, err = SelectMoodMethod(h.States[0], nil)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Reason != MoodRelief || proposal.Need != MoodJoy {
		t.Fatalf("relief withheld under unowned pressure: %+v", proposal)
	}
	proposal, err = SelectMoodMethod(h.States[0], []MoodNeed{MoodJoy})
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Reason != MoodUnowned || proposal.Thought != "SleptInBarracks" {
		t.Fatalf("exhausted relief did not surface the blocker: %+v", proposal)
	}

	// Owned provisioning wins over unowned pressure; non-dominant unowned
	// pressure records nothing; unknown thoughts retain it.
	p.Joy = domain.Known(.8)
	p.Thoughts = domain.Known([]MoodThought{{"SleptInBarracks", -5}, {"NeedJoy", -20}})
	h = moodReview(t, p, MoodHistory{})
	if len(h.States[0].Provision) == 0 || len(h.States[0].Unowned) != 0 {
		t.Fatalf("provisioned pawn also marked unowned: %+v", h.States[0])
	}
	p.Thoughts = domain.Known([]MoodThought{{"Insulted", -5}, {"NeedJoy", 4}})
	if h = moodReview(t, p, MoodHistory{}); len(h.States[0].Unowned) != 1 || h.States[0].Unowned[0].Def != "Insulted" {
		t.Fatal("social memory missing from the unowned bucket", h.States[0].Unowned)
	}
	p.Thoughts = domain.Known([]MoodThought{{"SleptInBarracks", -5}})
	h = moodReview(t, p, MoodHistory{})
	p.Thoughts = domain.Unknown[[]MoodThought]()
	if h = moodReview(t, p, h); len(h.States[0].Unowned) != 1 {
		t.Fatal("unknown thoughts dropped the retained unowned pressure", h.States[0])
	}
	p.Thoughts = domain.Known([]MoodThought{})
	if h = moodReview(t, p, h); len(h.States[0].Unowned) != 0 {
		t.Fatal("cleared thoughts kept unowned pressure", h.States[0])
	}
}

func TestDetectRoundsRaisesProvisionOwnerDeficit(t *testing.T) {
	f := stableRounds()
	f.ComfortRecovered, f.ComfortDeficit = domain.Known(false), domain.Known(.5)
	p := moodPawn()
	p.Food = domain.Known(.8)
	p.Thoughts = domain.Known([]MoodThought{{"NeedJoy", -20}})
	h := moodReview(t, p, MoodHistory{})
	f.Mood = h
	detected := needs(t, f, RoundsLatches{})
	found := false
	for _, g := range detected.Concerns {
		if g.ID == EnsureComfort {
			found = true
			if d, k := g.Deficit.Value(); !k || d != 1 {
				t.Fatalf("comfort deficit not raised by mood pressure: %v", g.Deficit)
			}
		}
	}
	if !found {
		t.Fatal("EnsureComfort missing", detected.Concerns)
	}
	// A recovered owner is not re-raised: the goal is simply absent.
	f.ComfortRecovered, f.ComfortDeficit = domain.Known(true), domain.Known(0.0)
	for _, g := range needs(t, f, RoundsLatches{}).Concerns {
		if g.ID == EnsureComfort {
			t.Fatal("recovered comfort re-raised by mood pressure")
		}
	}
}
