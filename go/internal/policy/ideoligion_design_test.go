package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"reflect"
	"slices"
	"testing"
)

func designTestOptions() DesignOptions {
	return DesignOptions{
		Needs:    domain.Known(DesignNeeds{Believers: 1, WorkTypes: []string{"Construction"}, Thoughts: domain.Known([]MoodThought{})}),
		Memes:    []DesignMeme{{Name: "Structure", Structure: true, Allowed: true, Supported: true}, {Name: "Normal", Allowed: true, Supported: true, InitialFluid: true, RequireOne: [][]string{{"Allowed", "Refused"}}}},
		Precepts: []DesignPrecept{{Name: "Refused", Issue: "Work", WorkTypes: []string{"Construction"}, Default: true, Allowed: true, Supported: true, Score: DesignScore{Restrictions: 1, MoodCost: 5}}, {Name: "Allowed", Issue: "Work", Default: true, Allowed: true, Supported: true}, {Name: "Unknown", Issue: "Work", Default: true, Allowed: true, Supported: false, Score: DesignScore{MoodBenefit: 100}}},
	}
}
func TestStartingDesignDeterministicAndLegal(t *testing.T) {
	options := designTestOptions()
	want, known := ChooseStartingIdeoligion(options).Value()
	if !known || !reflect.DeepEqual(want.Design.Precepts, []string{"Allowed"}) {
		t.Fatalf("choice %+v known %v", want, known)
	}
	slices.Reverse(options.Memes)
	slices.Reverse(options.Precepts)
	got, known := ChooseStartingIdeoligion(options).Value()
	if !known || !reflect.DeepEqual(got.Design, want.Design) {
		t.Fatalf("catalog ordering changed choice: %+v", got)
	}
	options.RequiredMemes = []string{"Absent"}
	if _, known := ChooseStartingIdeoligion(options).Value(); known {
		t.Fatal("selected without required meme")
	}
}

func TestReformRequiresObservedBenefitAndCoversTransitionCost(t *testing.T) {
	current := domain.IdeoligionDesign{Memes: []string{"Structure", "Normal"}, Precepts: []string{"Refused"}, Fluid: true}
	development := domain.Known(IdeoDevelopment{Fluid: true, CanReform: true})
	for _, tt := range []struct {
		name          string
		thoughts      []MoodThought
		candidateCost float64
		want          bool
	}{
		{"no observed pressure", nil, 0, false},
		{"relief exceeds new cost", []MoodThought{{Def: "Penalty", Offset: -5}}, 2, true},
		{"new cost consumes relief", []MoodThought{{Def: "Penalty", Offset: -2}}, 2, false},
		{"positive thought lost", []MoodThought{{Def: "Penalty", Offset: -5}, {Def: "Reward", Offset: 2}}, 0, false},
	} {
		t.Run(tt.name, func(t *testing.T) {
			options := designTestOptions()
			options.Precepts[0].Thoughts = []string{"Penalty", "Reward"}
			options.Precepts[1].Score.MoodCost = tt.candidateCost
			options.Needs = domain.Known(DesignNeeds{Believers: 1, Thoughts: domain.Known(tt.thoughts)})
			choice, known := ChooseIdeoligionReform(options, current, development, domain.Known(true)).Value()
			if !known || (len(choice.Design.Memes) > 0) != tt.want {
				t.Fatalf("choice %+v known %v", choice, known)
			}
		})
	}
	options := designTestOptions()
	options.Needs = domain.Unknown[DesignNeeds]()
	if _, known := ChooseIdeoligionReform(options, current, development, domain.Known(true)).Value(); known {
		t.Fatal("unknown colony needs admitted")
	}
}

func TestIdeoligionNeedsRequiresCompleteBelievers(t *testing.T) {
	ideo := IdeoligionFacts{IdeoID: "primary", Believers: 1}
	workers := domain.Known([]WorkPawn{{ID: "pawn", PolicyInputs: domain.Known(PawnPolicyInputs{Ideo: "primary"}), Work: domain.Known([]WorkPriority{{Work: "Construction", Priority: 1}})}})
	moods := domain.Known([]MoodPawn{{ID: "pawn", Thoughts: domain.Known([]MoodThought{{Def: "Penalty", Offset: -3}})}})
	needs, known := IdeoligionNeeds(ideo, workers, moods).Value()
	if !known || !reflect.DeepEqual(needs.WorkTypes, []string{"Construction"}) {
		t.Fatal(needs, known)
	}
	ideo.Believers = 2
	if _, known := IdeoligionNeeds(ideo, workers, moods).Value(); known {
		t.Fatal("missing believer treated as zero cost")
	}
}
func TestReformHoldsUnknownEmergencyFixedAndNoImprovement(t *testing.T) {
	options := designTestOptions()
	current := domain.IdeoligionDesign{Memes: []string{"Structure", "Normal"}, Precepts: []string{"Refused"}, Fluid: true}
	development := domain.Known(IdeoDevelopment{Fluid: true, CanReform: true, Points: 10, NextPoints: 10})
	choice, known := ChooseIdeoligionReform(options, current, development, domain.Known(true)).Value()
	if !known || !reflect.DeepEqual(choice.Design.Precepts, []string{"Allowed"}) {
		t.Fatalf("reform %+v", choice)
	}
	for _, calm := range []domain.Fact[bool]{domain.Unknown[bool](), domain.Known(false)} {
		if choice, known := ChooseIdeoligionReform(options, current, development, calm).Value(); known && len(choice.Design.Memes) > 0 {
			t.Fatal("unsafe reform")
		}
	}
	if _, known := ChooseIdeoligionReform(options, current, domain.Unknown[IdeoDevelopment](), domain.Known(true)).Value(); known {
		t.Fatal("unknown eligibility")
	}
	if choice, known := ChooseIdeoligionReform(options, current, domain.Known(IdeoDevelopment{}), domain.Known(true)).Value(); !known || len(choice.Design.Memes) > 0 {
		t.Fatal("fixed reform")
	}
	if choice, known := ChooseIdeoligionReform(options, choice.Design, development, domain.Known(true)).Value(); !known || len(choice.Design.Memes) > 0 {
		t.Fatal("repeated/no-improvement reform")
	}
	current.Precepts = []string{"Unknown"}
	if _, known := ChooseIdeoligionReform(options, current, development, domain.Known(true)).Value(); known {
		t.Fatal("unknown baseline scored as zero")
	}
}

func TestReformRetainsUsefulBenefitAndDeterministicTies(t *testing.T) {
	options := designTestOptions()
	options.Precepts[0].Score.MoodBenefit = 5
	current := domain.IdeoligionDesign{Memes: []string{"Structure", "Normal"}, Precepts: []string{"Refused"}, Fluid: true}
	development := domain.Known(IdeoDevelopment{Fluid: true, CanReform: true})
	if choice, known := ChooseIdeoligionReform(options, current, development, domain.Known(true)).Value(); !known || len(choice.Design.Memes) > 0 {
		t.Fatal("improvement discarded an existing useful benefit")
	}
	options.Precepts[0].Score.MoodBenefit = 0
	options.Precepts = append(options.Precepts, DesignPrecept{Name: "AnotherAllowed", Issue: "Work", Allowed: true, Supported: true})
	options.Memes[1].RequireOne = nil
	want, known := ChooseIdeoligionReform(options, current, development, domain.Known(true)).Value()
	if !known {
		t.Fatal("no improvement")
	}
	slices.Reverse(options.Precepts)
	got, known := ChooseIdeoligionReform(options, current, development, domain.Known(true)).Value()
	if !known || !reflect.DeepEqual(got.Design, want.Design) {
		t.Fatal("input order changed tied reform")
	}
}

func TestStartingDesignRejectsIncompatibilityAndUnsupportedObligations(t *testing.T) {
	options := designTestOptions()
	options.Memes[0].Exclusions = []string{"Conflict"}
	options.Memes[1].Exclusions = []string{"Conflict"}
	if _, known := ChooseStartingIdeoligion(options).Value(); known {
		t.Fatal("conflicting memes selected")
	}
	options = designTestOptions()
	options.Memes[1].Supported = false
	options.Memes[1].Obligations = 1
	if _, known := ChooseStartingIdeoligion(options).Value(); known {
		t.Fatal("unsupported mandatory obligation selected")
	}
	options = designTestOptions()
	options.Memes[1].RequireOne = [][]string{{"Unknown"}}
	if _, known := ChooseStartingIdeoligion(options).Value(); known {
		t.Fatal("unknown required effect selected")
	}
}
