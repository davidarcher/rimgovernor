package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainPopulation names Population-*'s prisoner interaction deficit: any
// observed, living colony prisoner whose exclusive interaction is not the
// use the colony has for it (prisonerUse). The autopilot chooses each
// prisoner's use itself -- recruit, convert, enslave or release -- with no
// player-only exemption; execution and the non-exclusive toggles are never
// proposed. Native eligibility (recruitable, alive, prisoner, not a wild man
// barred from the mode, Ideology for Enslave/Convert) is re-validated by
// native when it applies the intent; this only decides which
// already-observed prisoner gets which write.
const MaintainPopulation ConcernID = "MaintainPopulation"

// PrisonerPolicy is the slice of RoutinePolicy MaintainPopulation plans
// from. ReleaseAfterDays is how long a prisoner the colony has no use for
// is fed while the food runway holds FoodTargetDays; below that target it
// is released at once. Zero releases a useless prisoner at once regardless.
type PrisonerPolicy struct {
	ReleaseAfterDays float64
	FoodTargetDays   float64
}

// PopulationOutlook is the storyteller's colony-level population state read
// with the population census (#1031): StorytellerUtilityPopulation's intent
// and adjusted population, the chance a non-colony humanlike downed by
// violence dies now, and the chance a new prisoner is unrecruitable.
type PopulationOutlook struct {
	Intent              domain.Fact[float64]
	AdjustedPopulation  domain.Fact[float64]
	DeathOnDownedChance domain.Fact[float64]
	UnrecruitableChance domain.Fact[float64]
}

// PrisonerSkill and PrisonerTrait are one pawn's native skill and trait rows.
type PrisonerSkill struct {
	Name     string
	Level    int
	Passion  string // "None", "Minor" or "Major"
	Disabled bool
}
type PrisonerTrait struct {
	Def    string
	Degree int
}

// PrisonerProspect is the biography a prisoner's use is judged by.
type PrisonerProspect struct {
	Age    float64
	Health float64 // SummaryHealthPercent, 0..1
	Skills []PrisonerSkill
	Traits []PrisonerTrait
	// Incapable lists the work types the pawn can never do.
	Incapable []string
}

// PrisonerColony is the colony side of the decision: the free colonists'
// best level per skill and the Ideology facts Enslave and Convert need.
type PrisonerColony struct {
	Colonists      int
	BestSkill      map[string]int
	IdeologyActive bool
	ClassicIdeo    bool
	// Ideo is the player faction's primary ideoligion id; empty without
	// Ideology.
	Ideo string
	// Ideology is the frame's ideoligion fact (#1654): slavery and organ
	// use read it through the shared precept rule (#1656). The routine
	// reading fills it; unknown without Ideology.
	Ideology domain.Fact[Ideoligion]
	// Medicine is each free colonist's Medicine level by pawn, doctors only
	// (the skill not disabled): the doctor training floor counts it (#1236)
	// and the training step names its surgeon from it (#1253).
	Medicine map[domain.PawnID]int
}

// SlaveryAllowed reports whether the colony's ideoligion lets it enslave
// without a mood cost or refusal: the precept rule's stance for the
// EnslavedPrisoner event is allowed or approved. Classic mode, an unread
// ideoligion and a game without Ideology hold.
func (c PrisonerColony) SlaveryAllowed() bool {
	if !c.IdeologyActive || c.ClassicIdeo {
		return false
	}
	stance := ActionStance(c.Ideology, PreceptAction{HistoryEvent: "EnslavedPrisoner"}, PreceptSubject{}).Stance
	return stance == PreceptAllowed || stance == PreceptApproved
}

// PrisonerFacts is one observed prisoner's population census row.
type PrisonerFacts struct {
	Pawn               domain.PawnID
	Dead               domain.Fact[bool]
	Prisoner           domain.Fact[bool]
	Recruitable        domain.Fact[bool]
	CurrentInteraction domain.Fact[domain.PrisonerInteractionMode]
	// Resistance and HeldTicks: native's remaining recruit resistance and
	// the TimeAsPrisoner record, in ticks.
	Resistance domain.Fact[float64]
	HeldTicks  domain.Fact[int64]
	// Will is Pawn_GuestTracker.will (Ideology only); Ideo the prisoner's
	// ideoligion id, empty for none; WildMan bars Enslave.
	Will     domain.Fact[float64]
	Ideo     string
	WildMan  bool
	Prospect domain.Fact[PrisonerProspect]
	// Organ harvest facts (#1169): the prisoner's operations and queued
	// medical bills, its home faction's id (empty for none) and the goodwill
	// change vanilla's harvest violation would make with it (<= 0).
	// MissingParts: the prisoner's missing parts (#1232 keeps a second leg).
	MissingParts    domain.Fact[[]MissingPart]
	Operations      domain.Fact[[]SurgeryOperation]
	QueuedSurgeries domain.Fact[int]
	Faction         string
	HarvestGoodwill domain.Fact[int]
	// MedicalCare is the prisoner's MedicalCareCategory name (#1239).
	MedicalCare domain.Fact[string]
	// Withdrawal: the prisoner carries a drug addiction it cannot feed, so
	// it is in or near withdrawal (#1236 peg-leg control).
	Withdrawal domain.Fact[bool]
	// Care cap inputs (#1301): conditions and life threat, the queued
	// bills' recipes and an Execution interaction.
	Conditions      domain.Fact[[]CareCondition]
	LifeThreatening domain.Fact[bool]
	QueuedRecipes   []string
	Executing       bool
	// PolicyInputs is the prisoner's current drug policy and chemicals
	// (#1554).
	PolicyInputs domain.Fact[PawnPolicyInputs]
	// CreepJoiner: the prisoner has a creepjoiner tracker (unknown while that
	// read failed); Kind is its PawnKindDef name (#1740 disarming).
	CreepJoiner domain.Fact[bool]
	Kind        string
}

// PrisonerPlanReason names why RoutinePrisonerInteractionPlanner did or did
// not propose an interaction write.
type PrisonerPlanReason string

const (
	PrisonerNoDeficit PrisonerPlanReason = "no_recruitable_prisoner"
	PrisonerUnknown   PrisonerPlanReason = "population_census_unknown"
)

// PrisonerChoice is the one prisoner/mode pair chosen for an interaction
// write, mirroring HusbandryChoice's single-write-per-cycle rule. Interaction
// is set whenever Reason is empty.
type PrisonerChoice struct {
	Reason      PrisonerPlanReason
	Pawn        domain.PawnID
	Interaction domain.PrisonerInteractionMode
}

// Trait effects on a prospect's worth: work-rate and mood-stability traits
// by defName and degree. Anything unlisted is neutral.
var (
	prospectGoodTraits = map[PrisonerTrait]bool{
		{"Industriousness", 1}: true, {"Industriousness", 2}: true, {"Tough", 0}: true,
		{"TooSmart", 0}: true, {"Nerves", 1}: true, {"Nerves", 2}: true,
		{"NaturalMood", 1}: true, {"NaturalMood", 2}: true, {"FastLearner", 0}: true,
		{"Kind", 0}: true,
	}
	prospectBadTraits = map[PrisonerTrait]bool{
		{"Pyromaniac", 0}: true, {"Industriousness", -1}: true, {"Industriousness", -2}: true,
		{"Nerves", -1}: true, {"Nerves", -2}: true, {"NaturalMood", -1}: true, {"NaturalMood", -2}: true,
		{"Wimp", 0}: true, {"Abrasive", 0}: true, {"DrugDesire", 2}: true, {"Gourmand", 0}: true,
	}
)

// PrisonerWorth scores a prospect against the colony: +1 per usable skill at
// 8+, +1 more at 12+, +1 per major and +0.5 per minor passion, +2 per skill
// at 6+ that beats the colony's best by 3 or more (a gap the colony lacks),
// +1 per good and -2 per bad trait, -0.5 per incapable work type, -1 under
// 16 or over 60, -1 below half health, and -1 per 10 points of native recruit
// resistance still to break (0 when unknown). Recruiting needs
// RecruitThreshold; organ harvest (#1169) considers only prisoners below it.
func PrisonerWorth(p PrisonerProspect, resistance float64, c PrisonerColony) float64 {
	score := -resistance / 10
	for _, s := range p.Skills {
		if s.Disabled {
			continue
		}
		if s.Level >= 8 {
			score++
		}
		if s.Level >= 12 {
			score++
		}
		switch s.Passion {
		case "Major":
			score++
		case "Minor":
			score += 0.5
		}
		if best, ok := c.BestSkill[s.Name]; s.Level >= 6 && (!ok || s.Level >= best+3) {
			score += 2
		}
	}
	for _, t := range p.Traits {
		if prospectGoodTraits[t] {
			score++
		}
		if prospectBadTraits[t] {
			score -= 2
		}
	}
	score -= 0.5 * float64(len(p.Incapable))
	if p.Age < 16 || p.Age > 60 {
		score--
	}
	if p.Health < 0.5 {
		score--
	}
	return score
}

// RecruitThreshold is the worth a prospect needs: a colony of fewer than 3
// takes almost anyone (2), fewer than 6 wants a solid hand (4), a larger one
// only a strong one (6).
func RecruitThreshold(colonists int) float64 {
	switch {
	case colonists < 3:
		return 2
	case colonists < 6:
		return 4
	}
	return 6
}

// Worth is the row's PrisonerWorth against the colony; Unknown without a
// prospect.
func (row PrisonerFacts) Worth(c PrisonerColony) domain.Fact[float64] {
	p, ok := row.Prospect.Value()
	if !ok {
		return domain.Unknown[float64]()
	}
	resistance, _ := row.Resistance.Value()
	return domain.Known(PrisonerWorth(p, resistance, c))
}

func (row PrisonerFacts) worthRecruiting(p PrisonerProspect, c PrisonerColony) bool {
	resistance, _ := row.Resistance.Value()
	return PrisonerWorth(p, resistance, c) >= RecruitThreshold(c.Colonists)
}

// HarvestEligible reports whether a living prisoner is one the colony would
// not recruit: unrecruitable, or worth below RecruitThreshold. It only
// gates organ harvest (#1169); Unknown when a needed fact is missing.
func (row PrisonerFacts) HarvestEligible(c PrisonerColony) domain.Fact[bool] {
	dead, dk := row.Dead.Value()
	recruitable, rk := row.Recruitable.Value()
	p, pk := row.Prospect.Value()
	if !dk || !rk || !pk {
		return domain.Unknown[bool]()
	}
	return domain.Known(!dead && (!recruitable || !row.worthRecruiting(p, c)))
}

// canLabor reports whether a prospect would be a useful slave: at least 13,
// at least 40% health and incapable of at most four work types.
func canLabor(p PrisonerProspect) bool {
	return p.Age >= 13 && p.Health >= 0.4 && len(p.Incapable) <= 4
}

// prisonerUse is the interaction one living prisoner should have, or ""
// when the row is settled: prisonerIntent's use when the prisoner does not
// already have it. A legless prisoner is not released: vanilla cannot
// release a downed pawn, and MaintainSurgery puts a peg leg back first
// (#1236). unknown reports a fact the decision needed but the census did
// not carry; an unknown fact is never evidence of a settled prisoner.
func prisonerUse(row PrisonerFacts, colony PrisonerColony, food domain.Fact[float64], p PrisonerPolicy) (want domain.PrisonerInteractionMode, unknown bool) {
	intent, unknown := prisonerIntent(row, colony, food, p)
	current, _ := row.CurrentInteraction.Value()
	if unknown || intent == "" || intent == current {
		return "", unknown
	}
	if intent == domain.PrisonerInteractionRelease && row.legless() {
		return "", false
	}
	return intent, false
}

// prisonerIntent is the use the colony has for one living prisoner, or ""
// while it is simply held. The rules, in order:
//
//   - A recruitable prisoner whose resistance is broken and who is already
//     being recruited keeps recruiting.
//   - Worth recruiting (PrisonerWorth >= RecruitThreshold) and recruitable:
//     Convert first when Ideology is active outside classic mode and the
//     prisoner holds another ideoligion, so the recruit joins without a
//     foreign faith dragging mood and certainty; Recruit once it shares the
//     colony's ideoligion (or without Ideology).
//   - Otherwise, able to labor, not a wild man and the ideoligion allows
//     slavery without mood cost: Enslave.
//   - Otherwise Release: once chosen it stays, else at once while the food
//     runway is below target, else after ReleaseAfterDays in custody.
func prisonerIntent(row PrisonerFacts, colony PrisonerColony, food domain.Fact[float64], p PrisonerPolicy) (intent domain.PrisonerInteractionMode, unknown bool) {
	dead, dk := row.Dead.Value()
	if !dk {
		return "", true
	}
	if dead {
		return "", false
	}
	recruitable, rk := row.Recruitable.Value()
	current, ck := row.CurrentInteraction.Value()
	prospect, pk := row.Prospect.Value()
	if !rk || !ck || !pk {
		return "", true
	}
	if recruitable && current == domain.PrisonerInteractionRecruit {
		resistance, known := row.Resistance.Value()
		if known && resistance <= 0 {
			return domain.PrisonerInteractionRecruit, false
		}
	}
	if recruitable && row.worthRecruiting(prospect, colony) {
		if colony.IdeologyActive && !colony.ClassicIdeo && colony.Ideo != "" && row.Ideo != "" && row.Ideo != colony.Ideo {
			return domain.PrisonerInteractionConvert, false
		}
		return domain.PrisonerInteractionRecruit, false
	}
	if colony.SlaveryAllowed() && !row.WildMan && canLabor(prospect) {
		return domain.PrisonerInteractionEnslave, false
	}
	if current == domain.PrisonerInteractionRelease {
		return domain.PrisonerInteractionRelease, false
	}
	days, fk := food.Value()
	if !fk {
		return "", true
	}
	if days < p.FoodTargetDays {
		return domain.PrisonerInteractionRelease, false
	}
	held, hk := row.HeldTicks.Value()
	if !hk {
		return "", true
	}
	if float64(held) >= p.ReleaseAfterDays*domain.TicksPerDay {
		return domain.PrisonerInteractionRelease, false
	}
	return "", false
}

// PrisonerRecruitDeficit reports whether any observed, living prisoner
// still needs an interaction write. An unknown census, or any prisoner whose
// facts are incomplete for the decision, leaves the whole need unknown
// rather than silently treating it as recovered. The colony facts matter
// only once a prisoner is held.
func PrisonerRecruitDeficit(prisoners domain.Fact[[]PrisonerFacts], colony domain.Fact[PrisonerColony], food domain.Fact[float64], p PrisonerPolicy) domain.Fact[bool] {
	rows, known := prisoners.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	if len(rows) == 0 {
		return domain.Known(false)
	}
	c, ck := colony.Value()
	if !ck {
		return domain.Unknown[bool]()
	}
	deficit := false
	for _, row := range rows {
		want, unknown := prisonerUse(row, c, food, p)
		if unknown {
			return domain.Unknown[bool]()
		}
		if want != "" {
			deficit = true
		}
	}
	return domain.Known(deficit)
}

// SelectPrisonerInteractionMethod picks the lowest-pawn-ID living prisoner
// still needing a write, and the write it needs, to dispatch next. A row
// whose facts are incomplete is skipped rather than blocking the rest.
func SelectPrisonerInteractionMethod(prisoners domain.Fact[[]PrisonerFacts], colony domain.Fact[PrisonerColony], food domain.Fact[float64], p PrisonerPolicy) PrisonerChoice {
	rows, known := prisoners.Value()
	c, ck := colony.Value()
	if !known || len(rows) > 0 && !ck {
		return PrisonerChoice{Reason: PrisonerUnknown}
	}
	sorted := append([]PrisonerFacts{}, rows...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Pawn < sorted[j].Pawn })
	for _, row := range sorted {
		if want, unknown := prisonerUse(row, c, food, p); !unknown && want != "" {
			return PrisonerChoice{Pawn: row.Pawn, Interaction: want}
		}
	}
	return PrisonerChoice{Reason: PrisonerNoDeficit}
}
