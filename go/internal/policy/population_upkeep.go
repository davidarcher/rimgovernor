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
const MaintainPopulation GoalID = "MaintainPopulation"

// PrisonerPolicy is the slice of RoutinePolicy MaintainPopulation plans
// from. ReleaseAfterDays is how long a prisoner the colony has no use for
// is fed while the food runway holds FoodTargetDays; below that target it
// is released at once. Zero releases a useless prisoner at once regardless.
type PrisonerPolicy struct {
	ReleaseAfterDays float64
	FoodTargetDays   float64
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
	// Ideo is the player faction's primary ideoligion id; SlaveryPrecept
	// the defName of its Slavery-issue precept. Empty without Ideology.
	Ideo           string
	SlaveryPrecept string
}

// SlaveryAllowed reports whether the colony's ideoligion lets it enslave
// without a mood cost: Slavery_Acceptable or Slavery_Honorable. Abhorrent,
// Horrible and Disapproved all carry enslaved-prisoner and slaves-in-colony
// mood thoughts (Ideology Precepts_Slavery.xml).
func (c PrisonerColony) SlaveryAllowed() bool {
	return c.IdeologyActive && (c.SlaveryPrecept == "Slavery_Acceptable" || c.SlaveryPrecept == "Slavery_Honorable")
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

// RecruitWorth scores a prospect against the colony: +1 per usable skill at
// 8+, +1 more at 12+, +1 per major and +0.5 per minor passion, +2 per skill
// at 6+ that beats the colony's best by 3 or more (a gap the colony lacks),
// +1 per good and -2 per bad trait, -0.5 per incapable work type, -1 under
// 16 or over 60, -1 below half health.
func RecruitWorth(p PrisonerProspect, c PrisonerColony) float64 {
	score := 0.0
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

// canLabor reports whether a prospect would be a useful slave: at least 13,
// at least 40% health and incapable of at most four work types.
func canLabor(p PrisonerProspect) bool {
	return p.Age >= 13 && p.Health >= 0.4 && len(p.Incapable) <= 4
}

// prisonerUse is the interaction one living prisoner should have, or ""
// when the row is settled. unknown reports a fact the decision needed but
// the census did not carry; an unknown fact is never evidence of a settled
// prisoner. The rules, in order:
//
//   - A recruitable prisoner whose resistance is broken and who is already
//     being recruited keeps recruiting.
//   - Worth recruiting (RecruitWorth >= RecruitThreshold) and recruitable:
//     Convert first when Ideology is active outside classic mode and the
//     prisoner holds another ideoligion, so the recruit joins without a
//     foreign faith dragging mood and certainty; Recruit once it shares the
//     colony's ideoligion (or without Ideology).
//   - Otherwise, able to labor, not a wild man and the ideoligion allows
//     slavery without mood cost: Enslave.
//   - Otherwise Release: at once while the food runway is below target,
//     else after ReleaseAfterDays in custody.
func prisonerUse(row PrisonerFacts, colony PrisonerColony, food domain.Fact[float64], p PrisonerPolicy) (want domain.PrisonerInteractionMode, unknown bool) {
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
	settle := func(mode domain.PrisonerInteractionMode) (domain.PrisonerInteractionMode, bool) {
		if current == mode {
			return "", false
		}
		return mode, false
	}
	if recruitable && current == domain.PrisonerInteractionRecruit {
		resistance, known := row.Resistance.Value()
		if known && resistance <= 0 {
			return "", false
		}
	}
	if recruitable && RecruitWorth(prospect, colony) >= RecruitThreshold(colony.Colonists) {
		if colony.IdeologyActive && !colony.ClassicIdeo && colony.Ideo != "" && row.Ideo != "" && row.Ideo != colony.Ideo {
			return settle(domain.PrisonerInteractionConvert)
		}
		return settle(domain.PrisonerInteractionRecruit)
	}
	if colony.SlaveryAllowed() && !row.WildMan && canLabor(prospect) {
		return settle(domain.PrisonerInteractionEnslave)
	}
	if current == domain.PrisonerInteractionRelease {
		return "", false
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
	if float64(held) >= p.ReleaseAfterDays*60000 {
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
