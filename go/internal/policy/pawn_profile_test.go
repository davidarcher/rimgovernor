package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Every table row maps to exactly the effect the Core def documents; an
// unknown trait, or a known name at an unlisted degree, changes nothing.
func TestTraitTable(t *testing.T) {
	cases := []struct {
		trait PawnTrait
		want  TraitEffects
	}{
		{PawnTrait{"Industriousness", 2}, TraitEffects{WorkSpeed: 0.35}},
		{PawnTrait{"Industriousness", 1}, TraitEffects{WorkSpeed: 0.20}},
		{PawnTrait{"Industriousness", -1}, TraitEffects{WorkSpeed: -0.20}},
		{PawnTrait{"Industriousness", -2}, TraitEffects{WorkSpeed: -0.35}},
		{PawnTrait{"Neurotic", 1}, TraitEffects{WorkSpeed: 0.20}},
		{PawnTrait{"Neurotic", 2}, TraitEffects{WorkSpeed: 0.40}},
		{PawnTrait{"FastLearner", 0}, TraitEffects{LearnRate: 0.75}},
		{PawnTrait{"SlowLearner", 0}, TraitEffects{LearnRate: -0.75}},
		{PawnTrait{"TooSmart", 0}, TraitEffects{LearnRate: 0.75}},
		{PawnTrait{"GreatMemory", 0}, TraitEffects{GreatMemory: true}},
		{PawnTrait{"SpeedOffset", 2}, TraitEffects{MoveSpeed: 0.4}},
		{PawnTrait{"SpeedOffset", 1}, TraitEffects{MoveSpeed: 0.2}},
		{PawnTrait{"SpeedOffset", -1}, TraitEffects{MoveSpeed: -0.2}},
		{PawnTrait{"QuickSleeper", 0}, TraitEffects{QuickSleeper: true}},
		{PawnTrait{"NightOwl", 0}, TraitEffects{NightShift: true}},
		{PawnTrait{"Brawler", 0}, TraitEffects{MeleeOnly: true, FrontLine: true}},
		{PawnTrait{"Tough", 0}, TraitEffects{FrontLine: true}},
		{PawnTrait{"Nimble", 0}, TraitEffects{FrontLine: true}},
		{PawnTrait{"ShootingAccuracy", 1}, TraitEffects{RearRanged: true}},
		{PawnTrait{"ShootingAccuracy", -1}, TraitEffects{RearRanged: true}},
		{PawnTrait{"Pyromaniac", 0}, TraitEffects{NoFirefighting: true, Pyromaniac: true}},
		{PawnTrait{"Kind", 0}, TraitEffects{Sociable: 1}},
		{PawnTrait{"Abrasive", 0}, TraitEffects{Sociable: -1}},
		{PawnTrait{"Psychopath", 0}, TraitEffects{Execution: true, SurgeonSafe: true}},
		{PawnTrait{"Bloodlust", 0}, TraitEffects{Execution: true}},
		{PawnTrait{"Nudist", 0}, TraitEffects{Nudist: true}},
		{PawnTrait{"Ascetic", 0}, TraitEffects{Ascetic: true}},
		{PawnTrait{"Cannibal", 0}, TraitEffects{Cannibal: true}},
		{PawnTrait{"Gourmand", 0}, TraitEffects{Gourmand: true}},
		{PawnTrait{"DrugDesire", 2}, TraitEffects{ChemicalInterest: 2}},
		{PawnTrait{"DrugDesire", 1}, TraitEffects{ChemicalInterest: 1}},
		{PawnTrait{"DrugDesire", -1}, TraitEffects{ChemicalInterest: -1}},
		{PawnTrait{"Undergrounder", 0}, TraitEffects{Undergrounder: true}},
		{PawnTrait{"Greedy", 0}, TraitEffects{Greedy: true}},
		{PawnTrait{"Jealous", 0}, TraitEffects{Jealous: true}},
		{PawnTrait{"NaturalMood", 2}, TraitEffects{}},
		{PawnTrait{"Nerves", -2}, TraitEffects{}},
		{PawnTrait{"Industriousness", 0}, TraitEffects{}},
		{PawnTrait{"VTE_SomeModTrait", 1}, TraitEffects{}},
	}
	for _, c := range cases {
		if got := TraitEffect(c.trait); got != c.want {
			t.Fatal(c.trait, got, c.want)
		}
	}
	if len(KnownTraits()) != 35 {
		t.Fatal(len(KnownTraits()))
	}
}

func TestBuildProfile(t *testing.T) {
	pawn := WorkPawn{ID: "p", Ranged: domain.Known(true),
		Skills:    domain.Known([]WorkSkill{{Name: "Cooking", Level: 7, Stored: 6, Passion: "Major"}, {Name: "Mining", Level: 0, Disabled: true}, {Name: "Plants", Level: 3}}),
		Traits:    domain.Known([]PawnTrait{{"Industriousness", 2}, {"FastLearner", 0}, {"TooSmart", 0}, {"Abrasive", 0}, {"Mod_Trait", 0}}),
		Incapable: domain.Known([]WorkType{WorkMining, "Violent"}),
		Age:       domain.Known(11.0)}
	p := BuildProfile(pawn)
	if p.Effects != (TraitEffects{WorkSpeed: 0.35, LearnRate: 1.5, Sociable: -1}) || !p.Child || p.Age != 11 || !p.Ranged || len(p.Traits) != 5 {
		t.Fatal(p)
	}
	if s := p.Skill("Cooking"); s.Stored != 6 || s.LearnFactor(p.Effects) != 1.5*2.5 {
		t.Fatal(s)
	}
	if s := p.Skill("Plants"); s.Stored != 0 || s.LearnFactor(p.Effects) != 0.35*2.5 {
		t.Fatal(s)
	}
	if s := p.Skill("Medicine"); !s.Disabled || s.LearnFactor(p.Effects) != 0 {
		t.Fatal("missing skill is not disabled", s)
	}
	if p.Capable(WorkMining, 0) || !p.Capable(WorkHauling, 0) || !p.Capable(WorkCooking, 5) || p.Capable(WorkCooking, 8) || p.Capable(WorkWarden, 0) || p.Capable(WorkDoctor, 0) {
		t.Fatal("capable", p)
	}
	// Unknown traits, incapable rows and age degrade to nothing.
	bare := BuildProfile(WorkPawn{ID: "q", Skills: domain.Known([]WorkSkill{{Name: "Cooking", Level: 7}})})
	if bare.Effects != (TraitEffects{}) || bare.Child || len(bare.Incapable) != 0 || bare.Skill("Cooking").Level != 7 {
		t.Fatal(bare)
	}
	ordered := Profiles([]WorkPawn{pawn, {ID: "a"}})
	if len(ordered) != 2 || ordered[0].ID != "a" || ordered[1].ID != "p" {
		t.Fatal(ordered)
	}
}

func roleProfile(id PawnID, ranged bool, skills map[string]ProfileSkill, traits ...PawnTrait) PawnProfile {
	var rows []WorkSkill
	for name, s := range skills {
		rows = append(rows, WorkSkill{Name: name, Level: s.Level, Stored: s.Stored, Passion: s.Passion, Disabled: s.Disabled})
	}
	return BuildProfile(WorkPawn{ID: id, Ranged: domain.Known(ranged), Skills: domain.Known(rows), Traits: domain.Known(traits), Incapable: domain.Known([]WorkType{})})
}

func TestSituationalRoles(t *testing.T) {
	medic := roleProfile("medic", false, map[string]ProfileSkill{"Medicine": {Level: 12}, "Social": {Level: 4}, "Animals": {Level: 2}, "Shooting": {Level: 3}, "Melee": {Level: 9}})
	psycho := roleProfile("psycho", true, map[string]ProfileSkill{"Medicine": {Level: 9}, "Social": {Level: 9}, "Animals": {Level: 9}, "Shooting": {Level: 9}, "Melee": {Level: 1}}, PawnTrait{Name: "Psychopath"})
	kind := roleProfile("kind", true, map[string]ProfileSkill{"Medicine": {Level: 3}, "Social": {Level: 7}, "Animals": {Level: 11}, "Shooting": {Level: 12}, "Melee": {Level: 12}}, PawnTrait{Name: "Kind"}, PawnTrait{Name: "SpeedOffset", Degree: 2}, PawnTrait{Name: "Tough"})
	abrasive := roleProfile("abrasive", true, map[string]ProfileSkill{"Medicine": {Level: 1}, "Social": {Level: 15}, "Animals": {Level: 0}, "Shooting": {Level: 14}, "Melee": {Level: 2}}, PawnTrait{Name: "Abrasive"}, PawnTrait{Name: "Brawler"})
	all := []PawnProfile{medic, psycho, kind, abrasive}
	if id, ok := SurgeonFor(all, 10, false); !ok || id != "medic" {
		t.Fatal(id, ok)
	}
	if id, ok := SurgeonFor(all, 4, true); !ok || id != "psycho" {
		t.Fatal("harvest ignored the psychopath", id, ok)
	}
	if _, ok := SurgeonFor(all, 13, false); ok {
		t.Fatal("surgeon under minimum")
	}
	if id, ok := WardenFor(all, false); !ok || id != "kind" {
		t.Fatal(id, ok)
	}
	if id, ok := WardenFor(all, true); !ok || id != "psycho" {
		t.Fatal("execution ignored the psychopath", id, ok)
	}
	if id, ok := WardenFor([]PawnProfile{abrasive}, false); ok {
		t.Fatal("abrasive warden", id)
	}
	if id, ok := TraderFor(all); !ok || id != "kind" {
		t.Fatal(id, ok)
	}
	if id, ok := TraderFor([]PawnProfile{abrasive}); !ok || id != "abrasive" {
		t.Fatal("lone abrasive trader refused", id, ok)
	}
	if id, ok := TamerFor(all, 10); !ok || id != "kind" {
		t.Fatal(id, ok)
	}
	if _, ok := TamerFor(all, 12); ok {
		t.Fatal("tamer under minimum")
	}
	// The brawler shoots best but never hunts; the medic is unarmed.
	if id, ok := HunterFor(all); !ok || id != "kind" {
		t.Fatal(id, ok)
	}
	if _, ok := HunterFor([]PawnProfile{medic, abrasive}); ok {
		t.Fatal("unarmed or brawler hunter")
	}
	front, rear := FrontLine(all)
	if !reflect.DeepEqual(front, []PawnID{"medic", "kind", "abrasive"}) || !reflect.DeepEqual(rear, []PawnID{"psycho"}) {
		t.Fatal(front, rear)
	}
}
