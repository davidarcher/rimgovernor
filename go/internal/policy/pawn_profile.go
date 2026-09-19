package policy

import (
	"sort"
)

// PawnTrait is one native trait row: the TraitDef name and, for spectrum
// traits (Industriousness, NaturalMood, SpeedOffset, ...), its degree.
type PawnTrait struct {
	Name   string
	Degree int
}

// TraitEffects is what the planner and the other pawn-facing goals read from a
// pawn's traits, typed so no policy matches trait names itself. Values are the
// Core TraitDef stat offsets (WorkSpeedGlobal, GlobalLearningFactor,
// MoveSpeed) or the hard preferences the wiki documents; every trait the
// table does not know (mod traits, DLC traits) contributes nothing.
type TraitEffects struct {
	// WorkSpeed is the summed WorkSpeedGlobal offset (Industrious +0.35,
	// Slothful -0.35, Very neurotic +0.40).
	WorkSpeed float64
	// LearnRate is the summed GlobalLearningFactor offset (FastLearner and
	// TooSmart +0.75 each, SlowLearner -0.75).
	LearnRate float64
	// MoveSpeed is the summed MoveSpeed offset (Jogger +0.4, Slowpoke -0.2).
	MoveSpeed float64
	// GreatMemory halves skill decay above level 10.
	GreatMemory bool
	// QuickSleeper rests twice as fast: a shorter Sleep block suffices.
	QuickSleeper bool
	// NightShift (NightOwl) wants the pawn awake 23h-6h and asleep by day.
	NightShift bool
	// MeleeOnly (Brawler) forbids a ranged weapon and so the Hunting owner
	// role; FrontLine (Tough, Nimble, Brawler) prefers the melee line and
	// RearRanged (careful shooter, trigger-happy) the ranged line.
	MeleeOnly, FrontLine, RearRanged bool
	// NoFirefighting (Pyromaniac) forbids Firefighter; Pyromaniac also orders
	// break containment away from flammable stores.
	NoFirefighting, Pyromaniac bool
	// Sociable orders warden, recruiter and trader candidates: Kind +1,
	// Abrasive -1 (never warden or trader while another candidate exists).
	Sociable int
	// Execution (Psychopath, Bloodlust) marks a warden who executes and a
	// surgeon who harvests without a mood loss; Psychopath alone is
	// SurgeonSafe.
	Execution, SurgeonSafe bool
	// Apparel and diet flags the gear and food planners consume.
	Nudist, Ascetic, Cannibal, Gourmand bool
	// ChemicalInterest is the DrugDesire degree: 1 interest, 2 fascination,
	// -1 teetotaler.
	ChemicalInterest int
	// Room provisioning flags (#286): Undergrounder wants no windows or
	// outdoors, Greedy an impressive room, Jealous no better room than his.
	Undergrounder, Greedy, Jealous bool
}

func (e TraitEffects) add(o TraitEffects) TraitEffects {
	e.WorkSpeed += o.WorkSpeed
	e.LearnRate += o.LearnRate
	e.MoveSpeed += o.MoveSpeed
	e.GreatMemory = e.GreatMemory || o.GreatMemory
	e.QuickSleeper = e.QuickSleeper || o.QuickSleeper
	e.NightShift = e.NightShift || o.NightShift
	e.MeleeOnly = e.MeleeOnly || o.MeleeOnly
	e.FrontLine = e.FrontLine || o.FrontLine
	e.RearRanged = e.RearRanged || o.RearRanged
	e.NoFirefighting = e.NoFirefighting || o.NoFirefighting
	e.Pyromaniac = e.Pyromaniac || o.Pyromaniac
	e.Sociable += o.Sociable
	e.Execution = e.Execution || o.Execution
	e.SurgeonSafe = e.SurgeonSafe || o.SurgeonSafe
	e.Nudist = e.Nudist || o.Nudist
	e.Ascetic = e.Ascetic || o.Ascetic
	e.Cannibal = e.Cannibal || o.Cannibal
	e.Gourmand = e.Gourmand || o.Gourmand
	e.ChemicalInterest += o.ChemicalInterest
	e.Undergrounder = e.Undergrounder || o.Undergrounder
	e.Greedy = e.Greedy || o.Greedy
	e.Jealous = e.Jealous || o.Jealous
	return e
}

// traitTable maps Core TraitDef name and degree to typed effects. Singular
// traits carry degree 0. Numbers come from Core/Defs/TraitDefs; verify
// against the def when a game update changes them.
var traitTable = map[PawnTrait]TraitEffects{
	{"Industriousness", 2}:   {WorkSpeed: 0.35},
	{"Industriousness", 1}:   {WorkSpeed: 0.20},
	{"Industriousness", -1}:  {WorkSpeed: -0.20},
	{"Industriousness", -2}:  {WorkSpeed: -0.35},
	{"Neurotic", 1}:          {WorkSpeed: 0.20},
	{"Neurotic", 2}:          {WorkSpeed: 0.40},
	{"FastLearner", 0}:       {LearnRate: 0.75},
	{"SlowLearner", 0}:       {LearnRate: -0.75},
	{"TooSmart", 0}:          {LearnRate: 0.75},
	{"GreatMemory", 0}:       {GreatMemory: true},
	{"SpeedOffset", 2}:       {MoveSpeed: 0.4},
	{"SpeedOffset", 1}:       {MoveSpeed: 0.2},
	{"SpeedOffset", -1}:      {MoveSpeed: -0.2},
	{"QuickSleeper", 0}:      {QuickSleeper: true},
	{"NightOwl", 0}:          {NightShift: true},
	{"Brawler", 0}:           {MeleeOnly: true, FrontLine: true},
	{"Tough", 0}:             {FrontLine: true},
	{"Nimble", 0}:            {FrontLine: true},
	{"ShootingAccuracy", 1}:  {RearRanged: true},
	{"ShootingAccuracy", -1}: {RearRanged: true},
	{"Pyromaniac", 0}:        {NoFirefighting: true, Pyromaniac: true},
	{"Kind", 0}:              {Sociable: 1},
	{"Abrasive", 0}:          {Sociable: -1},
	{"Psychopath", 0}:        {Execution: true, SurgeonSafe: true},
	{"Bloodlust", 0}:         {Execution: true},
	{"Nudist", 0}:            {Nudist: true},
	{"Ascetic", 0}:           {Ascetic: true},
	{"Cannibal", 0}:          {Cannibal: true},
	{"Gourmand", 0}:          {Gourmand: true},
	{"DrugDesire", 2}:        {ChemicalInterest: 2},
	{"DrugDesire", 1}:        {ChemicalInterest: 1},
	{"DrugDesire", -1}:       {ChemicalInterest: -1},
	{"Undergrounder", 0}:     {Undergrounder: true},
	{"Greedy", 0}:            {Greedy: true},
	{"Jealous", 0}:           {Jealous: true},
}

// TraitEffect is the table row for one trait; an unknown trait is the zero
// value.
func TraitEffect(trait PawnTrait) TraitEffects {
	return traitTable[trait]
}

// KnownTraits lists the trait rows the table knows, for documentation and
// the dossier.
func KnownTraits() []PawnTrait {
	out := make([]PawnTrait, 0, len(traitTable))
	for t := range traitTable {
		out = append(out, t)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Name != out[j].Name {
			return out[i].Name < out[j].Name
		}
		return out[i].Degree < out[j].Degree
	})
	return out
}

// ProfileSkill is one skill as the planner scores it: the effective level
// (with aptitude offsets, what the game applies), the stored level native
// keeps beneath it (what learning raises) and the passion.
type ProfileSkill struct {
	Name     string
	Level    int
	Stored   int
	Passion  string
	Disabled bool
}

// LearnFactor is the passion learning multiplier (no passion 0.35, minor
// 1.0, major 1.5) scaled by the pawn's global learning offset.
func (s ProfileSkill) LearnFactor(effects TraitEffects) float64 {
	if s.Disabled {
		return 0
	}
	rate := 0.35
	switch s.Passion {
	case "Minor":
		rate = 1
	case "Major":
		rate = 1.5
	}
	return rate * (1 + effects.LearnRate)
}

// PawnProfile is who a pawn is for assignment purposes: skills, traits and
// backstory incapabilities as one typed view over the routine read.
type PawnProfile struct {
	ID        PawnID
	Skills    map[string]ProfileSkill
	Traits    []PawnTrait
	Effects   TraitEffects
	Incapable map[WorkType]bool
	// Age is the biological age in years; Child is age under the adult
	// stage (13, Biotech developmental stages) and age-gates work.
	Age   float64
	Child bool
	// Ranged is whether the pawn's primary weapon is ranged.
	Ranged bool
}

// Skill returns the pawn's skill row; a skill absent from the read is level
// 0 and never learns (a disabled skill).
func (p PawnProfile) Skill(name string) ProfileSkill {
	if s, ok := p.Skills[name]; ok {
		return s
	}
	return ProfileSkill{Name: name, Disabled: true}
}

// Capable reports whether the pawn can do the work type at all: not
// backstory-disabled, not trait-forbidden and, for skilled work, the skill
// not disabled and at or above the floor.
func (p PawnProfile) Capable(work WorkType, floor int) bool {
	if p.Incapable[work] || p.Forbidden(work) {
		return false
	}
	skill := WorkSkillName(work)
	if skill == "" {
		return true
	}
	s := p.Skill(skill)
	return !s.Disabled && s.Level >= floor
}

// Forbidden is the hard trait rule: a Pyromaniac never fights fire, a
// Brawler never hunts (ranged), an Abrasive pawn never wardens.
func (p PawnProfile) Forbidden(work WorkType) bool {
	switch work {
	case WorkFirefighter:
		return p.Effects.NoFirefighting
	case WorkHunting:
		return p.Effects.MeleeOnly
	case WorkWarden:
		return p.Effects.Sociable < 0
	}
	return false
}

// BuildProfile reads the profile from a WorkPawn; unknown traits, incapable
// rows or age leave those parts empty rather than making the profile unknown,
// since the planner degrades to skill-only ordering without them.
func BuildProfile(pawn WorkPawn) PawnProfile {
	profile := PawnProfile{ID: pawn.ID, Skills: map[string]ProfileSkill{}, Incapable: map[WorkType]bool{}}
	if skills, ok := pawn.Skills.Value(); ok {
		for _, s := range skills {
			profile.Skills[s.Name] = ProfileSkill{Name: s.Name, Level: s.Level, Stored: s.Stored, Passion: s.Passion, Disabled: s.Disabled}
		}
	}
	if traits, ok := pawn.Traits.Value(); ok {
		profile.Traits = append([]PawnTrait(nil), traits...)
		for _, t := range traits {
			profile.Effects = profile.Effects.add(TraitEffect(t))
		}
	}
	if incapable, ok := pawn.Incapable.Value(); ok {
		for _, w := range incapable {
			profile.Incapable[w] = true
		}
	}
	if age, ok := pawn.Age.Value(); ok {
		profile.Age = age
		profile.Child = age < 13
	}
	profile.Ranged, _ = pawn.Ranged.Value()
	return profile
}

// Profiles builds every pawn's profile in ID order.
func Profiles(pawns []WorkPawn) []PawnProfile {
	out := make([]PawnProfile, 0, len(pawns))
	for _, p := range pawns {
		out = append(out, BuildProfile(p))
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}
