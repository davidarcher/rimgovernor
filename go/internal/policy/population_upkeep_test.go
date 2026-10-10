package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// strong is a prospect any colony wants: two high passionate skills and a
// good trait. weak is an able-bodied prospect nobody recruits.
var (
	strong = PrisonerProspect{Age: 30, Health: 1, Skills: []PrisonerSkill{{"Construction", 12, "Major", false}, {"Plants", 9, "Minor", false}}, Traits: []PrisonerTrait{{"Industriousness", 1, 0.2}}}
	weak   = PrisonerProspect{Age: 30, Health: 1, Skills: []PrisonerSkill{{"Construction", 3, "None", false}}, Traits: []PrisonerTrait{{"Industriousness", -1, -0.2}}}
	frail  = PrisonerProspect{Age: 80, Health: 0.3, Incapable: []string{"Mining", "Hauling", "Cleaning", "Growing", "Construction"}}
)

func prisonerRow(id string, recruitable bool, current domain.PrisonerInteractionMode, resistance float64, heldDays float64, prospect PrisonerProspect) PrisonerFacts {
	return PrisonerFacts{
		Pawn: domain.PawnID(id), Dead: domain.Known(false), Prisoner: domain.Known(true),
		Recruitable: domain.Known(recruitable), CurrentInteraction: domain.Known(current),
		Resistance: domain.Known(resistance), HeldTicks: domain.Known(int64(heldDays * 60000)),
		Prospect: domain.Known(prospect),
	}
}

// slaveryIdeology is an ideoligion whose slavery precept has the given
// effects (the game's EnslavedPrisoner event).
func slaveryIdeology(effects ...PreceptEffect) domain.Fact[Ideoligion] {
	return domain.Known(ruleIdeoligion(PreceptDef{Name: "Slavery_Test", Effects: effects}))
}

var (
	core      = PrisonerColony{Colonists: 4, BestSkill: map[string]int{"Construction": 6, "Plants": 8}}
	slavers   = PrisonerColony{Colonists: 4, BestSkill: core.BestSkill, IdeologyActive: true, Ideo: "Ideo_1", Ideology: slaveryIdeology()}
	abhorrent = PrisonerColony{Colonists: 4, BestSkill: core.BestSkill, IdeologyActive: true, Ideo: "Ideo_1", Ideology: slaveryIdeology(took("EnslavedPrisoner", -2))}
)

func TestPrisonerUseDecisions(t *testing.T) {
	p := PrisonerPolicy{ReleaseAfterDays: 10, FoodTargetDays: 7}
	foreign := prisonerRow("p", true, domain.PrisonerInteractionMaintain, 5, 1, strong)
	foreign.Ideo = "Ideo_2"
	same := foreign
	same.Ideo = "Ideo_1"
	wild := prisonerRow("p", true, domain.PrisonerInteractionMaintain, 5, 1, weak)
	wild.WildMan = true
	classic := slavers
	classic.ClassicIdeo = true
	cases := []struct {
		name   string
		row    PrisonerFacts
		colony PrisonerColony
		food   float64
		want   domain.PrisonerInteractionMode
	}{
		{"worth recruiting recruits", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 5, 1, strong), core, 10, domain.PrisonerInteractionRecruit},
		{"already recruiting is settled", prisonerRow("p", true, domain.PrisonerInteractionRecruit, 5, 30, strong), core, 1, ""},
		{"foreign ideoligion converts first", foreign, slavers, 10, domain.PrisonerInteractionConvert},
		{"converted prospect recruits", same, slavers, 10, domain.PrisonerInteractionRecruit},
		{"classic mode never converts", foreign, classic, 10, domain.PrisonerInteractionRecruit},
		{"weak prospect enslaved when slavery acceptable", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 5, 1, weak), slavers, 10, domain.PrisonerInteractionEnslave},
		{"unrecruitable strong prospect enslaved", prisonerRow("p", false, domain.PrisonerInteractionMaintain, 5, 1, strong), slavers, 10, domain.PrisonerInteractionEnslave},
		{"disapproving ideoligion never enslaves", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 5, 1, weak), abhorrent, 3, domain.PrisonerInteractionRelease},
		{"wild man never enslaved", wild, slavers, 3, domain.PrisonerInteractionRelease},
		{"frail prisoner not enslaved", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 5, 1, frail), slavers, 3, domain.PrisonerInteractionRelease},
		{"no Ideology releases when food short", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 5, 1, weak), core, 3, domain.PrisonerInteractionRelease},
		{"food at target holds a useless prisoner", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 5, 9, weak), core, 7, ""},
		{"held past the hold releases anyway", prisonerRow("p", false, domain.PrisonerInteractionMaintain, 5, 10, weak), core, 7, domain.PrisonerInteractionRelease},
		{"broken resistance keeps recruiting", prisonerRow("p", true, domain.PrisonerInteractionRecruit, 0, 30, weak), core, 1, ""},
		{"already released is settled", prisonerRow("p", true, domain.PrisonerInteractionRelease, 5, 12, weak), core, 3, ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			rows := domain.Known([]PrisonerFacts{c.row})
			deficit, known := PrisonerRecruitDeficit(rows, domain.Known(c.colony), domain.Known(c.food), p).Value()
			if !known || deficit != (c.want != "") {
				t.Fatalf("deficit %v known %v, want %q", deficit, known, c.want)
			}
			choice := SelectPrisonerInteractionMethod(rows, domain.Known(c.colony), domain.Known(c.food), p)
			if c.want == "" {
				if choice.Reason != PrisonerNoDeficit {
					t.Fatalf("unexpected choice %+v", choice)
				}
				return
			}
			if choice.Reason != "" || choice.Interaction != c.want {
				t.Fatalf("choice %+v, want %s", choice, c.want)
			}
		})
	}
}

// TestPrisonerWorthGatesRecruitAndHarvest: a skilled prisoner is recruited
// and not harvest-eligible; a worthless high-resistance one is released and
// harvest-eligible; resistance alone can sink an average prospect.
func TestPrisonerWorthGatesRecruitAndHarvest(t *testing.T) {
	p := PrisonerPolicy{ReleaseAfterDays: 10, FoodTargetDays: 7}
	average := PrisonerProspect{Age: 30, Health: 1, Skills: []PrisonerSkill{{"Cooking", 8, "Major", false}, {"Mining", 7, "None", false}}}
	cases := []struct {
		name    string
		row     PrisonerFacts
		want    domain.PrisonerInteractionMode
		harvest bool
	}{
		{"skilled prisoner recruited", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 20, 1, strong), domain.PrisonerInteractionRecruit, false},
		{"worthless high-resistance prisoner released", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 60, 1, weak), domain.PrisonerInteractionRelease, true},
		{"average prospect recruited at low resistance", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 2, 1, average), domain.PrisonerInteractionRecruit, false},
		{"average prospect not recruited at high resistance", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 40, 1, average), domain.PrisonerInteractionRelease, true},
		{"unrecruitable skilled prisoner harvest-eligible", prisonerRow("p", false, domain.PrisonerInteractionMaintain, 0, 1, strong), domain.PrisonerInteractionRelease, true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			choice := SelectPrisonerInteractionMethod(domain.Known([]PrisonerFacts{c.row}), domain.Known(core), domain.Known(3.0), p)
			if choice.Interaction != c.want {
				t.Fatalf("choice %+v, want %s", choice, c.want)
			}
			if h, ok := c.row.HarvestEligible(core).Value(); !ok || h != c.harvest {
				t.Fatalf("harvest %v known %v, want %v", h, ok, c.harvest)
			}
			if _, ok := c.row.Worth(core).Value(); !ok {
				t.Fatal("worth unknown")
			}
		})
	}
}

func TestPrisonerWorthWeighsColonyGaps(t *testing.T) {
	cook := PrisonerProspect{Age: 30, Health: 1, Skills: []PrisonerSkill{{"Cooking", 7, "None", false}}}
	if w := PrisonerWorth(cook, 0, PrisonerColony{BestSkill: map[string]int{"Cooking": 3}}); w != 2 {
		t.Fatalf("gap-filling cook worth %v, want 2", w)
	}
	if w := PrisonerWorth(cook, 0, PrisonerColony{BestSkill: map[string]int{"Cooking": 6}}); w != 0 {
		t.Fatalf("redundant cook worth %v, want 0", w)
	}
	if RecruitThreshold(1) >= RecruitThreshold(4) || RecruitThreshold(4) >= RecruitThreshold(10) {
		t.Fatal("threshold should rise with colony size")
	}
}

func TestPrisonerUnknownFactsNeverAuthorize(t *testing.T) {
	p := PrisonerPolicy{ReleaseAfterDays: 10, FoodTargetDays: 7}
	base := prisonerRow("p", true, domain.PrisonerInteractionMaintain, 5, 12, weak)
	noProspect := base
	noProspect.Prospect = domain.Unknown[PrisonerProspect]()
	for name, tc := range map[string]struct {
		row    PrisonerFacts
		colony domain.Fact[PrisonerColony]
		food   domain.Fact[float64]
	}{
		"prospect unknown": {noProspect, domain.Known(core), domain.Known(3.0)},
		"colony unknown":   {base, domain.Unknown[PrisonerColony](), domain.Known(3.0)},
		"food unknown":     {base, domain.Known(core), domain.Unknown[float64]()},
	} {
		rows := domain.Known([]PrisonerFacts{tc.row})
		if _, known := PrisonerRecruitDeficit(rows, tc.colony, tc.food, p).Value(); known {
			t.Fatalf("%s: deficit should be unknown", name)
		}
		if choice := SelectPrisonerInteractionMethod(rows, tc.colony, tc.food, p); choice.Interaction != "" {
			t.Fatalf("%s: unexpected choice %+v", name, choice)
		}
	}
	// No prisoner needs no colony facts.
	if deficit, known := PrisonerRecruitDeficit(domain.Known([]PrisonerFacts{}), domain.Unknown[PrisonerColony](), domain.Unknown[float64](), p).Value(); !known || deficit {
		t.Fatalf("empty census: %v %v", deficit, known)
	}
}

func TestPrisonerChoiceByPawnOrder(t *testing.T) {
	p := PrisonerPolicy{ReleaseAfterDays: 10, FoodTargetDays: 7}
	rows := domain.Known([]PrisonerFacts{
		prisonerRow("p2", true, domain.PrisonerInteractionMaintain, 5, 1, strong),
		prisonerRow("p1", false, domain.PrisonerInteractionMaintain, 5, 20, weak),
	})
	choice := SelectPrisonerInteractionMethod(rows, domain.Known(core), domain.Known(2.0), p)
	if choice.Pawn != "p1" || choice.Interaction != domain.PrisonerInteractionRelease {
		t.Fatalf("unexpected choice %+v", choice)
	}
}
