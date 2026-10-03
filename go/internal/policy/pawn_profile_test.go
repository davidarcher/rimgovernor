package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestBuildProfile(t *testing.T) {
	pawn := WorkPawn{ID: "p", Ranged: domain.Known(true),
		Skills:    domain.Known([]WorkSkill{{Name: "Cooking", Level: 7, Stored: 6, Passion: "Major"}, {Name: "Mining", Level: 0, Disabled: true}, {Name: "Plants", Level: 3}}),
		Traits:    domain.Known([]PawnTrait{testTrait("Industriousness", 2), testTrait("FastLearner", 0), testTrait("TooSmart", 0), testTrait("Abrasive", 0), testTrait("Mod_Trait", 0)}),
		Incapable: domain.Known([]WorkType{WorkMining, "Violent"}),
		Age:       domain.Known(11.0)}
	p := BuildProfile(pawn)
	if !reflect.DeepEqual(p.Effects, TraitEffects{WorkSpeed: 0.35, LearnRate: 1.5, Sociable: -1}) || !p.Child || p.Age != 11 || !p.Ranged || len(p.Traits) != 5 {
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
	if !reflect.DeepEqual(bare.Effects, TraitEffects{}) || bare.Child || len(bare.Incapable) != 0 || bare.Skill("Cooking").Level != 7 {
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
	psycho := roleProfile("psycho", true, map[string]ProfileSkill{"Medicine": {Level: 9}, "Social": {Level: 9}, "Animals": {Level: 9}, "Shooting": {Level: 9}, "Melee": {Level: 1}}, testTrait("Psychopath", 0))
	kind := roleProfile("kind", true, map[string]ProfileSkill{"Medicine": {Level: 3}, "Social": {Level: 7}, "Animals": {Level: 11}, "Shooting": {Level: 12}, "Melee": {Level: 12}}, testTrait("Kind", 0), testTrait("SpeedOffset", 2), testTrait("Tough", 0))
	abrasive := roleProfile("abrasive", true, map[string]ProfileSkill{"Medicine": {Level: 1}, "Social": {Level: 15}, "Animals": {Level: 0}, "Shooting": {Level: 14}, "Melee": {Level: 2}}, testTrait("Abrasive", 0), testTrait("Brawler", 0))
	all := []PawnProfile{medic, psycho, kind, abrasive}
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
	if id, ok := TraderFor(Among(all, []PawnID{"psycho", "abrasive"})); !ok || id != "psycho" {
		t.Fatal("among ignored native eligibility", id, ok)
	}
	if got := Among(all, nil); got != nil {
		t.Fatal("among nobody", got)
	}
	front, rear := FrontLine(all)
	if !reflect.DeepEqual(front, []PawnID{"medic", "kind", "abrasive"}) || !reflect.DeepEqual(rear, []PawnID{"psycho"}) {
		t.Fatal(front, rear)
	}
}

// TestProfileChildFromDevelopmentalStage (#1678): a known developmental
// stage decides Child; without Biotech facts the age rule stays.
func TestProfileChildFromDevelopmentalStage(t *testing.T) {
	stage := func(name string) domain.Fact[PawnBiotech] {
		return domain.Known(PawnBiotech{DevelopmentalStage: domain.Known(name)})
	}
	for _, c := range []struct {
		name  string
		pawn  WorkPawn
		child bool
	}{
		{"baby", WorkPawn{Age: domain.Known(15.0), Biotech: stage("Baby")}, true},
		{"child", WorkPawn{Age: domain.Known(30.0), Biotech: stage("Child")}, true},
		{"adult below the age rule", WorkPawn{Age: domain.Known(11.0), Biotech: stage("Adult")}, false},
		{"unread stage keeps the age rule", WorkPawn{Age: domain.Known(11.0), Biotech: domain.Known(PawnBiotech{})}, true},
		{"core only", WorkPawn{Age: domain.Known(11.0)}, true},
	} {
		if got := BuildProfile(c.pawn).Child; got != c.child {
			t.Errorf("%s: child = %v", c.name, got)
		}
	}
}
