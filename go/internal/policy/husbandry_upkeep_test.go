package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func trainable(def string, available, learned bool) HusbandryTrainable {
	return HusbandryTrainable{Def: def, Available: domain.Known(available), Learned: domain.Known(learned)}
}

var noWild = domain.Known([]UpkeepAnimal{})

// feedFine is the tame gate open: the herd feed forecast reports no shortfall.
var feedFine = domain.Known(false)

// jobHerd gives every Muffalo the companion job: Obedience only.
var jobHerd = HerdPolicy{Roles: map[Resource]HerdRole{"Muffalo": {Job: HerdJobCompanion}}}

func herd(populationMax map[Resource]int64) HerdPolicy {
	return HerdPolicy{PopulationMax: populationMax}
}

// anyTamer is a roster with one capable, unskilled handler: every tame
// candidate without a minimum handling skill qualifies.
var anyTamer = domain.Known([]PawnProfile{BuildProfile(WorkPawn{ID: "handler", Work: testAllWork(), Skills: domain.Known([]WorkSkill{{Name: "Animals", Level: 0}})})})

func wildAnimal(id string, def Resource, tameable, designated bool) UpkeepAnimal {
	return UpkeepAnimal{ID: PawnID(id), Definition: def, Release: domain.Known(false), Slaughter: domain.Known(false), Tameable: domain.Known(tameable), Tame: domain.Known(designated)}
}

func TestAnimalHerdDeficitUnknownCensus(t *testing.T) {
	if v, known := AnimalHerdDeficit(domain.Unknown[[]UpkeepAnimal](), noWild, feedFine, herd(nil)).Value(); known || v {
		t.Fatal("unknown census must not be treated as recovered")
	}
}

func TestAnimalHerdDeficitEmptyHerdRecovered(t *testing.T) {
	deficit := AnimalHerdDeficit(domain.Known([]UpkeepAnimal{}), noWild, feedFine, herd(nil))
	if v, known := deficit.Value(); !known || v {
		t.Fatal("empty herd must be recovered", deficit)
	}
}

func TestAnimalHerdDeficitDetectsUntrainedAvailableTrainable(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	if v, known := AnimalHerdDeficit(animals, noWild, feedFine, jobHerd).Value(); !known || !v {
		t.Fatal("untrained available trainable must be a deficit")
	}
}

func TestAnimalHerdDeficitIgnoresLearnedUnavailableReleasedOrSlaughtered(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "learned", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, true)}},
		{ID: "unavailable", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", false, false)}},
		{ID: "released", Definition: "Muffalo", Release: domain.Known(true), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
		{ID: "slaughter-marked", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(true), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	if v, known := AnimalHerdDeficit(animals, noWild, feedFine, jobHerd).Value(); !known || v {
		t.Fatal("no animal should register a deficit", v, known)
	}
}

func TestAnimalHerdDeficitUnknownFactsStayUnknown(t *testing.T) {
	cases := []UpkeepAnimal{
		{ID: "a", Definition: "Muffalo", Release: domain.Unknown[bool](), Slaughter: domain.Known(false)},
		{ID: "a", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Unknown[bool]()},
		{ID: "a", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{{Def: "Obedience", Available: domain.Unknown[bool](), Learned: domain.Known(false)}}},
		{ID: "a", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{{Def: "Obedience", Available: domain.Known(true), Learned: domain.Unknown[bool]()}}},
	}
	for i, animal := range cases {
		if _, known := AnimalHerdDeficit(domain.Known([]UpkeepAnimal{animal}), noWild, feedFine, jobHerd).Value(); known {
			t.Fatalf("case %d: incomplete facts must stay unknown", i)
		}
	}
}

func TestSelectHusbandryMethodUnknownCensus(t *testing.T) {
	choice := SelectHusbandryMethod(domain.Unknown[[]UpkeepAnimal](), noWild, feedFine, herd(nil), anyTamer)
	if choice.Reason != HusbandryUnknown {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodNoDeficit(t *testing.T) {
	choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{
		{ID: "a", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, true)}},
	}), noWild, feedFine, herd(nil), anyTamer)
	if choice.Reason != HusbandryNoDeficit {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodPicksLowestAnimalThenTrainable(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Release", true, false), trainable("Obedience", true, false)}},
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	choice := SelectHusbandryMethod(animals, noWild, feedFine, jobHerd, anyTamer)
	if choice.Reason != "" || choice.Animal != "muffalo-1" || choice.Method != domain.HusbandryTrain || choice.TrainableDef != "Obedience" {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodSkipsReleasedAndSlaughteredAnimals(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{
		{ID: "muffalo-1", Definition: "Muffalo", Release: domain.Known(true), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
		{ID: "muffalo-2", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(true), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
		{ID: "muffalo-3", Definition: "Muffalo", Release: domain.Known(false), Slaughter: domain.Known(false), Training: []HusbandryTrainable{trainable("Obedience", true, false)}},
	})
	choice := SelectHusbandryMethod(animals, noWild, feedFine, jobHerd, anyTamer)
	if choice.Animal != "muffalo-3" {
		t.Fatal(choice)
	}
}

func TestSelectHusbandryMethodTamesTowardMinimum(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{playerAnimal("muffalo-1", "Muffalo", true)})
	wild := domain.Known([]UpkeepAnimal{
		wildAnimal("wild-3", "Muffalo", true, false),
		wildAnimal("wild-2", "Muffalo", true, false),
		wildAnimal("wild-1", "Muffalo", false, false),
		wildAnimal("thrumbo-1", "Thrumbo", true, false),
	})
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Muffalo": 2}}
	choice := SelectHusbandryMethod(animals, wild, feedFine, herd, anyTamer)
	if choice.Method != domain.HusbandryTame || choice.Animal != "wild-2" || choice.TrainableDef != "" {
		t.Fatal("the lowest-ID tameable wild animal of a tracked race below minimum must be proposed", choice)
	}
	if v, known := AnimalHerdDeficit(animals, wild, feedFine, herd).Value(); !known || !v {
		t.Fatal("a shortfall with a tame candidate is a deficit")
	}
}

func TestSelectHusbandryMethodTameCountsPendingDesignationsAndLeavingAnimals(t *testing.T) {
	leaving := playerAnimal("muffalo-1", "Muffalo", true)
	leaving.Release = domain.Known(true)
	animals := domain.Known([]UpkeepAnimal{leaving, playerAnimal("muffalo-2", "Muffalo", true)})
	wild := domain.Known([]UpkeepAnimal{wildAnimal("wild-1", "Muffalo", false, true), wildAnimal("wild-2", "Muffalo", true, false)})
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Muffalo": 2}}
	if choice := SelectHusbandryMethod(animals, wild, feedFine, herd, anyTamer); choice.Reason != HusbandryNoDeficit {
		t.Fatal("a pending tame designation counts toward the minimum", choice)
	}
	herd.PopulationMin["Muffalo"] = 3
	if choice := SelectHusbandryMethod(animals, wild, feedFine, herd, anyTamer); choice.Method != domain.HusbandryTame || choice.Animal != "wild-2" {
		t.Fatal("a release-designated animal does not count toward the minimum", choice)
	}
}

func TestSelectHusbandryMethodTameUnknownWildCensus(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{})
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Muffalo": 1}}
	if choice := SelectHusbandryMethod(animals, domain.Unknown[[]UpkeepAnimal](), feedFine, herd, anyTamer); choice.Reason != HusbandryUnknown {
		t.Fatal("an unknown wild census must not be treated as no candidate", choice)
	}
	if _, known := AnimalHerdDeficit(animals, domain.Unknown[[]UpkeepAnimal](), feedFine, herd).Value(); known {
		t.Fatal("an unknown wild census leaves the deficit unknown")
	}
	if choice := SelectHusbandryMethod(animals, domain.Unknown[[]UpkeepAnimal](), feedFine, HerdPolicy{}, anyTamer); choice.Reason != HusbandryNoDeficit {
		t.Fatal("without a minimum the wild census is never consulted", choice)
	}
}

func TestSelectHusbandryMethodTameWaitsForFeed(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{playerAnimal("muffalo-1", "Muffalo", true)})
	wild := domain.Known([]UpkeepAnimal{wildAnimal("wild-1", "Muffalo", true, false)})
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Muffalo": 2}, Roles: jobHerd.Roles}
	short := domain.Known(true)
	if choice := SelectHusbandryMethod(animals, wild, short, herd, anyTamer); choice.Reason != HusbandryNoDeficit {
		t.Fatal("a herd short of feed never takes on another mouth", choice)
	}
	if v, known := AnimalHerdDeficit(animals, wild, short, herd).Value(); !known || v {
		t.Fatal("a feed-gated shortfall is not a herd deficit", v, known)
	}
	unknown := domain.Unknown[bool]()
	if choice := SelectHusbandryMethod(animals, wild, unknown, herd, anyTamer); choice.Reason != HusbandryUnknown {
		t.Fatal("an unknown feed forecast never authorizes a tame", choice)
	}
	if _, known := AnimalHerdDeficit(animals, wild, unknown, herd).Value(); known {
		t.Fatal("an unknown feed forecast leaves the deficit unknown")
	}
	// A training candidate is unaffected by the feed gate.
	untrained := playerAnimal("muffalo-1", "Muffalo", true)
	untrained.Training = []HusbandryTrainable{trainable("Obedience", true, false)}
	if choice := SelectHusbandryMethod(domain.Known([]UpkeepAnimal{untrained}), wild, unknown, herd, anyTamer); choice.Method != domain.HusbandryTrain {
		t.Fatal("training precedes the feed-gated tame fallback", choice)
	}
	// The gate reads MaintainAnimalFeed's own review.
	if v, known := HerdFeedShort(feedReviewWith(nil)).Value(); !known || v {
		t.Fatal("an empty feed target list opens the gate")
	}
	if v, known := HerdFeedShort(feedReviewWith([]AnimalFeedTarget{{ID: "muffalo-1"}})).Value(); !known || !v {
		t.Fatal("a feed target closes the gate")
	}
	if _, known := HerdFeedShort(AnimalUpkeepReview{}).Value(); known {
		t.Fatal("an unknown feed review leaves the gate unknown")
	}
}

func feedReviewWith(targets []AnimalFeedTarget) AnimalUpkeepReview {
	if targets == nil {
		targets = []AnimalFeedTarget{}
	}
	return AnimalUpkeepReview{Feed: domain.Known(targets)}
}

func TestSelectHusbandryMethodTameNeedsAHandlerAtTheMinimum(t *testing.T) {
	animals := domain.Known([]UpkeepAnimal{})
	hard := wildAnimal("wild-1", "Muffalo", true, false)
	hard.MinimumHandlingSkill = domain.Known(8)
	easy := wildAnimal("wild-2", "Muffalo", true, false)
	easy.MinimumHandlingSkill = domain.Known(3)
	wild := domain.Known([]UpkeepAnimal{hard, easy})
	herd := HerdPolicy{PopulationMin: map[Resource]int64{"Muffalo": 2}}
	novice := domain.Known([]PawnProfile{BuildProfile(WorkPawn{ID: "novice", Work: testAllWork(), Skills: domain.Known([]WorkSkill{{Name: "Animals", Level: 5}})})})
	if choice := SelectHusbandryMethod(animals, wild, feedFine, herd, novice); choice.Method != domain.HusbandryTame || choice.Animal != "wild-2" {
		t.Fatal("the first candidate the roster can handle is proposed", choice)
	}
	if choice := SelectHusbandryMethod(animals, domain.Known([]UpkeepAnimal{hard}), feedFine, herd, novice); choice.Reason != HusbandryNoDeficit {
		t.Fatal("no handler at the minimum proposes no tame", choice)
	}
	if choice := SelectHusbandryMethod(animals, wild, feedFine, herd, domain.Unknown[[]PawnProfile]()); choice.Reason != HusbandryUnknown {
		t.Fatal("an unknown roster leaves the tame unknown", choice)
	}
}
