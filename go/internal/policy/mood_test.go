package policy

import (
	"math"
	"reflect"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func moodPawn() MoodPawn {
	return MoodPawn{ID: "p", Mood: domain.Known(.2), Threshold: domain.Known(.3), Food: domain.Known(.1), Rest: domain.Known(.8), Joy: domain.Known(.8), Mental: domain.Known(false), Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), PlayerForced: domain.Known(false)}
}
func moodReview(t *testing.T, p MoodPawn, h MoodHistory) MoodHistory {
	t.Helper()
	out, err := ReviewMood(domain.Known([]MoodPawn{p}), h)
	if err != nil {
		t.Fatal(err)
	}
	return out
}

func TestMoodThresholdsPressureAndRetainedNeeds(t *testing.T) {
	p := moodPawn()
	p.Mood = domain.Known(.8)
	if h := moodReview(t, p, MoodHistory{}); len(h.States) != 0 {
		t.Fatal("low food alone invented mood emergency", h)
	}
	p.Mood = domain.Known(.3)
	h := moodReview(t, p, MoodHistory{})
	if len(h.States) != 1 || h.States[0].Need() != domain.NeedDeficit {
		t.Fatal(h)
	}
	p.Mood = domain.Known(.35)
	p.Food = domain.Known(.5)
	h = moodReview(t, p, h)
	if !h.States[0].Active {
		t.Fatal("exit equality recovered", h)
	}
	p.Mood = domain.Known(.351)
	h = moodReview(t, p, h)
	if h.States[0].Need() != domain.NeedRecovered {
		t.Fatal(h)
	}
	p.Target = domain.Known(.2)
	p.Mood = domain.Known(.8)
	h = moodReview(t, p, h)
	if !h.States[0].Active {
		t.Fatal("native thought pressure ignored", h)
	}
	p.Target = domain.Unknown[float64]()
	p.Food = domain.Known(.1)
	h = moodReview(t, p, h)
	p.Food = domain.Known(.4)
	h = moodReview(t, p, h)
	if !h.States[0].Active || len(h.States[0].Causes) != 1 {
		t.Fatal("cause hysteresis lost", h)
	}
	p.Food = domain.Unknown[float64]()
	h = moodReview(t, p, h)
	if !h.States[0].Active {
		t.Fatal("unknown need claimed recovery", h)
	}
	if _, known := h.States[0].Causes[0].Level.Value(); known {
		t.Fatal("old cause level became fresh evidence")
	}
	p.Food = domain.Known(.5)
	h = moodReview(t, p, h)
	if h.States[0].Need() != domain.NeedRecovered {
		t.Fatal(h)
	}
}

func TestMoodMissingDeathAndMentalBreak(t *testing.T) {
	p := moodPawn()
	h := moodReview(t, p, MoodHistory{})
	for _, observed := range []domain.Fact[[]MoodPawn]{domain.Unknown[[]MoodPawn](), domain.Known([]MoodPawn{})} {
		out, err := ReviewMood(observed, h)
		if err != nil || len(out.States) != 1 || !out.States[0].Missing || out.States[0].Need() != domain.NeedUnknown {
			t.Fatal(out, err)
		}
		out.States[0].Causes[0].Need = MoodJoy
		if h.States[0].Causes[0].Need != MoodFood {
			t.Fatal("history alias")
		}
	}
	p.Dead = domain.Known(true)
	p.Mood = domain.Known(.9)
	p.Food = domain.Known(.9)
	out := moodReview(t, p, h)
	if !out.States[0].Active || out.States[0].Need() != domain.NeedUnknown {
		t.Fatal("death certified recovery", out)
	}
	p = moodPawn()
	p.Mood = domain.Unknown[float64]()
	p.Mental = domain.Known(true)
	out = moodReview(t, p, MoodHistory{})
	if out.States[0].Priority() != 1 || out.States[0].Need() != domain.NeedDeficit {
		t.Fatal(out)
	}
	p.Mental = domain.Unknown[bool]()
	out = moodReview(t, p, out)
	if out.States[0].Need() != domain.NeedUnknown || out.States[0].Priority() != 1 {
		t.Fatal(out)
	}
}

func TestMoodMethodOrderingAndPlayerGuards(t *testing.T) {
	p := moodPawn()
	p.Rest = domain.Known(.1)
	p.Joy = domain.Known(.05)
	h := moodReview(t, p, MoodHistory{})
	s := h.States[0]
	if !reflect.DeepEqual([]MoodNeed{s.Causes[0].Need, s.Causes[1].Need, s.Causes[2].Need}, []MoodNeed{MoodJoy, MoodFood, MoodRest}) {
		t.Fatal(s)
	}
	for _, test := range []struct {
		name   string
		change func(*MoodState)
		used   []MoodNeed
		reason MoodMethodReason
		need   MoodNeed
	}{
		{"first", nil, nil, MoodRelief, MoodJoy},
		{"used", nil, []MoodNeed{MoodJoy}, MoodRelief, MoodFood},
		{"exhausted", nil, []MoodNeed{MoodJoy, MoodFood, MoodRest}, MoodExhausted, ""},
		{"forced", func(s *MoodState) { s.Pawn.PlayerForced = domain.Known(true) }, nil, MoodPlayerWork, ""},
		{"drafted", func(s *MoodState) { s.Pawn.Drafted = domain.Known(true) }, nil, MoodPlayerWork, ""},
		{"downed", func(s *MoodState) { s.Pawn.Downed = domain.Known(true) }, nil, MoodPlayerWork, ""},
		{"unknown job", func(s *MoodState) { s.Pawn.PlayerForced = domain.Unknown[bool]() }, nil, MoodPlayerWork, ""},
		{"mental", func(s *MoodState) { s.Pawn.Mental = domain.Known(true) }, nil, MoodMentalBreak, ""},
		{"missing", func(s *MoodState) { s.Missing = true }, nil, MoodUnavailable, ""},
	} {
		t.Run(test.name, func(t *testing.T) {
			row := s
			if test.change != nil {
				test.change(&row)
			}
			proposal, err := SelectMoodMethod(row, test.used)
			if err != nil || proposal.Reason != test.reason || proposal.Need != test.need {
				t.Fatal(proposal, err)
			}
			if _, known := proposal.MoodBenefit.Value(); known {
				t.Fatal("invented future mood benefit")
			}
		})
	}
}

func TestMoodValidationAndBoundedIdentity(t *testing.T) {
	p := moodPawn()
	for _, rows := range [][]MoodPawn{{p, p}, {MoodPawn{ID: ""}}, {func() MoodPawn { v := p; v.Target = domain.Known(math.NaN()); return v }()}} {
		if _, err := ReviewMood(domain.Known(rows), MoodHistory{}); err == nil {
			t.Fatal(rows)
		}
	}
	long := PawnID(strings.Repeat("x", 256))
	id := MoodGoal(long)
	if !IsMoodGoal(id) || len(id) > 64 || id != MoodGoal(long) || id == MoodGoal("other") {
		t.Fatal(id)
	}
	for _, id := range []GoalID{"EnsureMood-", "EnsureMood-\x00", "EnsureMoodHash-ABCDEF0123456789ABCDEF0123456789"} {
		if IsMoodGoal(id) {
			t.Fatal(id)
		}
	}
	h := moodReview(t, p, MoodHistory{})
	h.States[0].Causes = append(h.States[0].Causes, h.States[0].Causes[0])
	if h.Validate() == nil {
		t.Fatal("duplicate cause accepted")
	}
}
