package bridge

import (
	"fmt"
	"slices"
	"strings"
	"testing"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// retiredProspectGood and retiredProspectBad are the prospect trait lists
// policy typed by hand before the worth was derived from the rows.
var (
	retiredProspectGood = map[traitDegree]bool{
		{"Industriousness", 1}: true, {"Industriousness", 2}: true, {"Tough", 0}: true,
		{"TooSmart", 0}: true, {"Nerves", 1}: true, {"Nerves", 2}: true,
		{"NaturalMood", 1}: true, {"NaturalMood", 2}: true, {"FastLearner", 0}: true,
		{"Kind", 0}: true,
	}
	retiredProspectBad = map[traitDegree]bool{
		{"Pyromaniac", 0}: true, {"Industriousness", -1}: true, {"Industriousness", -2}: true,
		{"Nerves", -1}: true, {"Nerves", -2}: true, {"NaturalMood", -1}: true, {"NaturalMood", -2}: true,
		{"Wimp", 0}: true, {"Abrasive", 0}: true, {"DrugDesire", 2}: true, {"Gourmand", 0}: true,
	}
)

const (
	situational = "was neutral; its situational thought stages go negative (worst stage counted as mood risk)"
	moveSpeed   = "was neutral; a MoveSpeed offset is now valued"
	unlisted    = "was neutral; the hand list never covered it, the rows now give a net effect"
)

// prospectDifferences explains every vanilla trait degree whose derived
// worth verdict differs from the retired lists, as "derived verdict: reason".
// Every degree the retired lists named keeps its verdict; a degree not listed
// here must agree (neutral stays neutral).
var prospectDifferences = map[traitDegree]string{
	{"Ascetic", 0}:        "bad: " + situational,
	{"BodyPurist", 0}:     "bad: " + situational + " (Ideology precept thoughts, worst stages summed)",
	{"Brawler", 0}:        "bad: " + situational + " (BrawlerUnhappy -10 while carrying a ranged weapon)",
	{"Delicate", 0}:       "bad: was neutral; IncomingDamageFactor 1.15 is a stat factor above 1",
	{"DrugDesire", 1}:     "bad: " + situational + " (DrugDesireInterest unsatisfied)",
	{"Greedy", 0}:         "bad: " + situational,
	{"Immunity", 1}:       "good: was neutral; ImmunityGainSpeed +0.3 is now valued",
	{"Jealous", 0}:        "bad: " + situational,
	{"Neurotic", 1}:       "good: " + unlisted + " (WorkSpeedGlobal +0.2 outweighs MentalBreakThreshold +0.08)",
	{"Neurotic", 2}:       "good: " + unlisted + " (WorkSpeedGlobal +0.4 outweighs MentalBreakThreshold +0.14)",
	{"NightOwl", 0}:       "bad: " + situational,
	{"Nudist", 0}:         "bad: " + situational,
	{"Recluse", 0}:        "bad: " + situational,
	{"SlowLearner", 0}:    "bad: " + unlisted + " (GlobalLearningFactor -0.75)",
	{"SpeedOffset", -1}:   "bad: " + moveSpeed,
	{"SpeedOffset", 1}:    "good: " + moveSpeed,
	{"SpeedOffset", 2}:    "good: " + moveSpeed,
	{"TorturedArtist", 0}: "bad: " + situational,
	{"Transhumanist", 0}:  "bad: " + situational,
	{"Undergrounder", 0}:  "bad: " + situational,
}

func worthVerdict(worth float64) string {
	switch {
	case worth > 0:
		return "good"
	case worth < 0:
		return "bad"
	}
	return "neutral"
}

// TestProspectWorthMatchesTheRetiredLists derives the worth of every trait
// degree of the full game recording and compares its verdict with the lists
// it replaces: all 21 degrees the lists named keep their verdict, and every
// other difference is pinned in prospectDifferences with its reason.
func TestProspectWorthMatchesTheRetiredLists(t *testing.T) {
	catalog := fullCatalog(t)
	var problems []string
	seen := map[traitDegree]bool{}
	for name, row := range catalogDefs[*d.TraitDef](catalog) {
		for _, entry := range row.(*d.TraitDef).GetDegreeDatas() {
			key := traitDegree{name, int(entry.GetValue().GetDegree())}
			seen[key] = true
			balance, err := catalog.TraitBalance(key.Name, key.Degree)
			if err != nil {
				t.Fatal(err)
			}
			old := "neutral"
			if retiredProspectGood[key] {
				old = "good"
			}
			if retiredProspectBad[key] {
				old = "bad"
			}
			got := worthVerdict(balance.Worth())
			reason, explained := prospectDifferences[key]
			switch {
			case got == old && explained:
				problems = append(problems, fmt.Sprintf("%s/%d: agrees (%s) but is listed as a difference", name, key.Degree, got))
			case got != old && !explained:
				problems = append(problems, fmt.Sprintf("%s/%d: derived %s, retired %s, no explanation", name, key.Degree, got, old))
			case got != old && !strings.HasPrefix(reason, got+":"):
				problems = append(problems, fmt.Sprintf("%s/%d: derived %s, explanation says %q", name, key.Degree, got, reason))
			}
		}
	}
	for key := range prospectDifferences {
		if !seen[key] {
			problems = append(problems, fmt.Sprintf("%s/%d: explained but not in the catalog", key.Name, key.Degree))
		}
	}
	for _, list := range []map[traitDegree]bool{retiredProspectGood, retiredProspectBad} {
		for key := range list {
			if !seen[key] {
				problems = append(problems, fmt.Sprintf("%s/%d: in a retired list, not in the catalog", key.Name, key.Degree))
			}
		}
	}
	slices.Sort(problems)
	if len(problems) > 0 {
		t.Errorf("derived worth differs from the retired lists:\n%s", fmt.Sprint(problems))
	}
}

// TestTraitBalanceErrors: a trait or degree the catalog lacks is a contract
// error, and a neutral trait weighs exactly zero.
func TestTraitBalanceErrors(t *testing.T) {
	catalog := fullCatalog(t)
	if _, err := catalog.TraitBalance("VTE_SomeModTrait", 0); err == nil {
		t.Error("a trait the catalog lacks resolved")
	}
	if _, err := catalog.TraitBalance("Industriousness", 0); err == nil {
		t.Error("a degree the trait lacks resolved")
	}
	balance, err := catalog.TraitBalance("Bisexual", 0)
	if err != nil || balance.Worth() != 0 {
		t.Errorf("a neutral trait: %+v %v", balance, err)
	}
}

// TestGourmandAndSociableAreDerived: the hunger rate row gives Gourmand and
// the one code-applied table gives Kind and Abrasive; GreatMemory has no
// effect left to read.
func TestGourmandAndSociableAreDerived(t *testing.T) {
	catalog := fullCatalog(t)
	for _, c := range []struct {
		name     string
		gourmand bool
		sociable int
	}{{"Gourmand", true, 0}, {"Kind", false, 1}, {"Abrasive", false, -1}, {"GreatMemory", false, 0}, {"Bisexual", false, 0}} {
		got, err := catalog.TraitEffects(c.name, 0)
		if err != nil {
			t.Fatal(err)
		}
		if got.Gourmand != c.gourmand || got.Sociable != c.sociable {
			t.Errorf("%s: gourmand %v sociable %d", c.name, got.Gourmand, got.Sociable)
		}
	}
}
