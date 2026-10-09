package policy

import (
	"fmt"
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
	deficits := provisionDeficits(p)
	if math.Abs(deficits[EnsureComfort]-20.0/29) > 1e-9 || math.Abs(deficits[MaintainHousing]-4.0/29) > 1e-9 {
		t.Fatal(deficits)
	}

	// Social pressure outweighing the environment thoughts provisions no pawn
	// relief, but the colony aggregate still weighs the removable share (4 of 9).
	p.Thoughts = domain.Known([]MoodThought{{"SleptOutside", -4}, {"Insulted", -5}})
	h = moodReview(t, p, MoodHistory{})
	if len(h.States[0].Provision) != 0 {
		t.Fatal("non-dominant environment pressure provisioned", h.States[0].Provision)
	}
	if got := provisionDeficits(p)[MaintainHousing]; math.Abs(got-4.0/9) > 1e-9 {
		t.Fatal("housing weight", got)
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
	if len(thoughtOwners("Slighted")) > 0 {
		t.Fatal("Slighted has no owner goal")
	}
	s = MoodState{Pawn: moodPawn(), Active: true, Unowned: []MoodThought{{"NeedJoy", -5}}}
	if err := (MoodHistory{States: []MoodState{s}}).Validate(); err == nil {
		t.Fatal("owned thought accepted as unowned pressure")
	}
	s.Unowned = []MoodThought{{"Slighted", -5}}
	s.Provision = []MoodProvision{{EnsureComfort, -20}}
	if err := (MoodHistory{States: []MoodState{s}}).Validate(); err == nil {
		t.Fatal("state both provisioned and unowned accepted")
	}
}

func TestMoodUnownedThoughtBlocker(t *testing.T) {
	p := moodPawn()
	p.Food = domain.Known(.8)
	p.Thoughts = domain.Known([]MoodThought{{"Slighted", -5}, {"Insulted", -3}})
	h := moodReview(t, p, MoodHistory{})
	if len(h.States) != 1 {
		t.Fatal(h)
	}
	s := h.States[0]
	if len(s.Provision) != 0 || len(s.Unowned) != 2 || s.Unowned[0] != (MoodThought{"Slighted", -5}) || s.Unowned[1] != (MoodThought{"Insulted", -3}) {
		t.Fatalf("unowned pressure not recorded: %+v", s)
	}
	if provisionDeficits(p) != nil {
		t.Fatal("unowned pressure raised a deficit")
	}
	proposal, err := SelectMoodMethod(s, nil)
	if err != nil {
		t.Fatal(err)
	}
	if proposal.Reason != MoodUnowned || proposal.Thought != "Slighted" || proposal.Need != "" {
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
	if proposal.Reason != MoodUnowned || proposal.Thought != "Slighted" {
		t.Fatalf("exhausted relief did not surface the blocker: %+v", proposal)
	}

	// Owned provisioning wins over unowned pressure; non-dominant unowned
	// pressure records nothing; unknown thoughts retain it.
	p.Joy = domain.Known(.8)
	p.Thoughts = domain.Known([]MoodThought{{"Slighted", -5}, {"NeedJoy", -20}})
	h = moodReview(t, p, MoodHistory{})
	if len(h.States[0].Provision) == 0 || len(h.States[0].Unowned) != 0 {
		t.Fatalf("provisioned pawn also marked unowned: %+v", h.States[0])
	}
	p.Thoughts = domain.Known([]MoodThought{{"Insulted", -5}, {"NeedJoy", 4}})
	if h = moodReview(t, p, MoodHistory{}); len(h.States[0].Unowned) != 1 || h.States[0].Unowned[0].Def != "Insulted" {
		t.Fatal("social memory missing from the unowned bucket", h.States[0].Unowned)
	}
	p.Thoughts = domain.Known([]MoodThought{{"Slighted", -5}})
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

// moodColony is n pawns carrying the same thoughts, the first low of them
// under the entry margin and the rest comfortably above it.
func moodColony(n, low int, thoughts ...MoodThought) []MoodPawn {
	var pawns []MoodPawn
	for i := 0; i < n; i++ {
		p := moodPawn()
		p.ID = PawnID(fmt.Sprintf("p%d", i))
		p.Mood = domain.Known(.9)
		if i < low {
			p.Mood = domain.Known(.2)
		}
		p.Thoughts = domain.Known(thoughts)
		pawns = append(pawns, p)
	}
	return pawns
}

func moodColonyLedger(pawns []MoodPawn) MoodLedger {
	var ledger []MoodLedgerPawn
	for _, p := range pawns {
		ledger = append(ledger, MoodLedgerPawn{ID: p.ID, Thoughts: p.Thoughts, Traits: domain.Known([]string{}), Precepts: domain.Known([]string{}), Expectation: domain.Known("Moderate")})
	}
	return BuildMoodLedger(ledger, nil)
}

// provisionDeficits is the aggregate over a census and the ledger built from it.
func provisionDeficits(pawns ...MoodPawn) map[ConcernID]float64 {
	return MoodProvisionDeficits(pawns, moodColonyLedger(pawns))
}

func TestMoodProvisionDeficitIsTheLedgerWeightedShareUnderTheMargin(t *testing.T) {
	near := func(name string, got, want float64) {
		t.Helper()
		if math.Abs(got-want) > 1e-9 {
			t.Fatalf("%s = %v, want %v", name, got, want)
		}
	}
	joy := MoodThought{"NeedJoy", -20}
	// One pawn in ten under the margin is a tenth, not a full deficit; N pawns
	// raise proportionally.
	near("one pawn", provisionDeficits(moodColony(10, 1, joy)...)[EnsureComfort], .1)
	near("four pawns", provisionDeficits(moodColony(10, 4, joy)...)[EnsureComfort], .4)
	near("all pawns", provisionDeficits(moodColony(10, 10, joy)...)[EnsureComfort], 1)
	// Weights follow the ledger: the owner's share of each pawn's loss.
	d := provisionDeficits(moodColony(4, 4, joy, MoodThought{"SleptOutside", -10}, MoodThought{"Insulted", -10})...)
	near("comfort weight", d[EnsureComfort], .5)
	near("housing weight", d[MaintainHousing], .25)
	// A pawn exactly at the entry margin counts; one above does not.
	edge := moodColony(2, 0, joy)
	edge[0].Mood = domain.Known(.3 + moodEntryMargin)
	edge[1].Mood = domain.Known(.3 + moodEntryMargin + .001)
	near("entry margin", provisionDeficits(edge...)[EnsureComfort], .5)
	// An unreadable pawn is left out of the denominator, not counted as zero.
	unknown := moodColony(3, 3, joy)
	unknown[0].Thoughts = domain.Unknown[[]MoodThought]()
	near("unknown thoughts", provisionDeficits(unknown...)[EnsureComfort], 1)
	unknown = moodColony(3, 3, joy)
	unknown[1].Mood = domain.Unknown[float64]()
	unknown[2].Threshold = domain.Unknown[float64]()
	near("unknown mood and threshold", provisionDeficits(unknown...)[EnsureComfort], 1)
	// Every pawn unknown raises nothing.
	all := moodColony(2, 2, joy)
	all[0].Thoughts, all[1].Thoughts = domain.Unknown[[]MoodThought](), domain.Unknown[[]MoodThought]()
	if d := provisionDeficits(all...); d != nil {
		t.Fatal("all-unknown colony raised", d)
	}
}

func TestDetectRoundsRaisesProvisionOwnerDeficit(t *testing.T) {
	joy := MoodThought{"NeedJoy", -20}
	comfort := func(pawns []MoodPawn, recovered bool) (float64, bool) {
		f := stableRounds()
		f.ComfortRecovered, f.ComfortDeficit = domain.Known(recovered), domain.Known(.05)
		if recovered {
			f.ComfortDeficit = domain.Known(0.0)
		}
		f.MoodPawns = domain.Known(pawns)
		f.MoodLedger = domain.Known(moodColonyLedger(pawns))
		for _, g := range needs(t, f, RoundsLatches{}).Concerns {
			if g.ID == EnsureComfort {
				return g.Deficit.Value()
			}
		}
		return 0, false
	}
	// One outlier in ten raises a tenth; four raise four tenths.
	if d, k := comfort(moodColony(10, 1, joy), false); !k || math.Abs(d-.1) > 1e-9 {
		t.Fatalf("one outlier deficit = %v", d)
	}
	if d, k := comfort(moodColony(10, 4, joy), false); !k || math.Abs(d-.4) > 1e-9 {
		t.Fatalf("four pawn deficit = %v", d)
	}
	// A recovered owner is not re-raised: the goal is simply absent.
	if _, found := comfort(moodColony(10, 10, joy), true); found {
		t.Fatal("recovered comfort re-raised by mood pressure")
	}
}
