package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PawnTrait is one native trait row: the TraitDef name and, for spectrum
// traits (Industriousness, NaturalMood, SpeedOffset, ...), its degree.
type PawnTrait struct {
	Name   string
	Degree int
	// Effects are the trait's typed effects, resolved from the definition
	// catalog when the pawn is read (DefinitionCatalog.TraitEffects).
	Effects TraitEffects
}

// TraitEffects is what the planner and the other pawn-facing goals read from a
// pawn's traits, typed so no policy matches trait names itself. A trait's
// effects are derived from its catalog row when the pawn is read
// (DefinitionCatalog.TraitEffects: stat offsets, disabled work, needs and
// ingestion thoughts), plus the few flags the game applies in code (TraitFlags).
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
	// DisabledWork is the work types the trait's disabled work tags and
	// disabled work types take away (Pyromaniac: Firefighter); Pyromaniac
	// also orders break containment away from flammable stores.
	DisabledWork []WorkType
	Pyromaniac   bool
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

// Add combines two effect sets: offsets and counts sum, flags join.
func (e TraitEffects) Add(o TraitEffects) TraitEffects {
	e.WorkSpeed += o.WorkSpeed
	e.LearnRate += o.LearnRate
	e.MoveSpeed += o.MoveSpeed
	e.GreatMemory = e.GreatMemory || o.GreatMemory
	e.QuickSleeper = e.QuickSleeper || o.QuickSleeper
	e.NightShift = e.NightShift || o.NightShift
	e.MeleeOnly = e.MeleeOnly || o.MeleeOnly
	e.FrontLine = e.FrontLine || o.FrontLine
	e.RearRanged = e.RearRanged || o.RearRanged
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
	e.DisabledWork = slices.Compact(slices.Sorted(slices.Values(slices.Concat(e.DisabledWork, o.DisabledWork))))
	return e
}

// traitKey names one trait degree in traitFlags.
type traitKey struct {
	Name   string
	Degree int
}

// traitFlags are the typed preferences the game applies to a trait in code,
// where no row of the TraitDef or a ThoughtDef states them; every other
// effect is derived from the catalog rows (DefinitionCatalog.TraitEffects).
// A trait absent here contributes no flag.
var traitFlags = map[traitKey]TraitEffects{
	{"GreatMemory", 0}:       {GreatMemory: true},
	{"NightOwl", 0}:          {NightShift: true},
	{"Brawler", 0}:           {MeleeOnly: true, FrontLine: true},
	{"Tough", 0}:             {FrontLine: true},
	{"Nimble", 0}:            {FrontLine: true},
	{"ShootingAccuracy", 1}:  {RearRanged: true},
	{"ShootingAccuracy", -1}: {RearRanged: true},
	{"Pyromaniac", 0}:        {Pyromaniac: true},
	{"Kind", 0}:              {Sociable: 1},
	{"Abrasive", 0}:          {Sociable: -1},
	{"Psychopath", 0}:        {Execution: true, SurgeonSafe: true},
	{"Bloodlust", 0}:         {Execution: true},
	{"Nudist", 0}:            {Nudist: true},
	{"Ascetic", 0}:           {Ascetic: true},
	{"Gourmand", 0}:          {Gourmand: true},
	{"DrugDesire", 2}:        {ChemicalInterest: 2},
	{"DrugDesire", 1}:        {ChemicalInterest: 1},
	{"DrugDesire", -1}:       {ChemicalInterest: -1},
	{"Greedy", 0}:            {Greedy: true},
	{"Jealous", 0}:           {Jealous: true},
}

// TraitFlags is the code-applied part of a trait's effects; a trait it does
// not list is the zero value.
func TraitFlags(name string, degree int) TraitEffects {
	return traitFlags[traitKey{name, degree}]
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
	// Age is the biological age in years; Child is a pre-adult developmental
	// stage (Newborn, Baby, Child) when Biotech facts carry one, else age
	// under 13 (the adult stage's start) and age-gates work.
	Age   float64
	Child bool
	// Biotech is the pawn's Biotech facts (#1678): life stage, genes and
	// the rest; unknown without Biotech.
	Biotech domain.Fact[PawnBiotech]
	// Genes are the active genes' typed effects (#1689); zero when unknown.
	// Their stat modifiers are already folded into Effects.
	Genes GeneEffects
	// WorkMinAge is the race's minimum age per work type for a child; a
	// work type absent has no minimum (#1682).
	WorkMinAge map[WorkType]int
	// Ranged is whether the pawn's primary weapon is ranged.
	Ranged bool
	// Inspiration is the current InspirationDef defName; known "" is none
	// and unknown stays distinct from none (#1187).
	Inspiration domain.Fact[string]
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
	if min, ok := p.WorkMinAge[work]; ok && p.Age < float64(min) {
		return true
	}
	if slices.Contains(p.Effects.DisabledWork, work) {
		return true
	}
	switch work {
	case WorkHunting:
		return p.Effects.MeleeOnly
	case WorkWarden:
		return p.Effects.Sociable < 0
	}
	return false
}

// ForbiddenWork lists the work types Forbidden refuses for this pawn, in
// natural-priority order, for the dossier.
func (p PawnProfile) ForbiddenWork() []WorkType {
	var out []WorkType
	for _, work := range []WorkType{WorkFirefighter, WorkWarden, WorkHunting} {
		if p.Forbidden(work) {
			out = append(out, work)
		}
	}
	young := make([]WorkType, 0, len(p.WorkMinAge))
	for work := range p.WorkMinAge {
		if p.Forbidden(work) && !slices.Contains(out, work) {
			young = append(young, work)
		}
	}
	for _, work := range p.Effects.DisabledWork {
		if !slices.Contains(out, work) && !slices.Contains(young, work) {
			young = append(young, work)
		}
	}
	slices.Sort(young)
	return append(out, young...)
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
			profile.Effects = profile.Effects.Add(t.Effects)
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
	profile.Biotech = pawn.Biotech
	if bt, ok := pawn.Biotech.Value(); ok {
		if genes, ok := bt.Effects.Value(); ok {
			profile.Genes = genes
			profile.Effects.WorkSpeed = genes.apply("WorkSpeedGlobal", profile.Effects.WorkSpeed)
			profile.Effects.LearnRate = genes.apply("GlobalLearningFactor", profile.Effects.LearnRate)
		}
		if child, known := bt.IsChild(); known {
			profile.Child = child
		}
		if ages, ok := bt.WorkMinAges.Value(); ok {
			profile.WorkMinAge = ages
		}
	}
	profile.Ranged, _ = pawn.Ranged.Value()
	profile.Inspiration = pawn.Inspiration
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
