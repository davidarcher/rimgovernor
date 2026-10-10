package httpapi

import (
	"context"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoundsProvider reports which composed routine planner families this
// process wired up at startup and the durable review cursor's progress, for
// read-only runtime diagnostics. Implementations must not trigger native
// calls or mutate state.
type RoundsProvider interface {
	RoundsStatus(context.Context) (RoundsStatus, error)
}

// RoundsStatus is a runtime snapshot of the composed routine runtime.
// ActiveFamilies names every routine planner family this process enabled
// (composed default or explicit), regardless of whether it currently has
// pending work; LastReviewTick is the durable review cursor's most recent
// reviewed tick, known once at least one review has run. Development is the
// ranking the last review recorded, nil until a review has run. Sections
// are the state store's held census sections with the tick each describes
// (facts.Store), so a live serve shows staleness per section.
type RoundsStatus struct {
	Emergency         []policy.ConcernID
	ExtentEligibility policy.ExtentEligibilityRequest
	// ResourceReach holds same-observation inputs; absent facts stay unknown.
	ResourceReach   policy.ResourceReachRequest
	ResourceRunways []policy.ResourceRunway
	ReviewsEnabled  bool
	MethodsEnabled  bool
	ActiveFamilies  []string
	LastReviewTick  domain.Tick
	LastReviewKnown bool
	// Progress is every active concern's progress record from the last review:
	// method, expected observable, last progress tick, next review
	// tick and blocker.
	Progress []policy.ConcernProgress
	// Stage is the colony stage the last review derived with the
	// first unmet condition of the next; nil until an enabled review
	// filed one.
	Stage *policy.ColonyStageRecord
	// Roster is the roster planner's last recorded report, nil until
	// an enabled review planned work.
	Roster   *policy.WorkRosterReport
	Sections []facts.Status
	// NoOps are the inspections that raised nothing in the last enabled
	// review, with the typed reason.
	NoOps []policy.NoOpRecord
	// LootHolds are the safe forbidden stacks the last review's reach stage
	// or demand kept forbidden, with reasons.
	LootHolds []policy.LootHold
}

type roundsStatusDTO struct {
	Emergency         []policy.ConcernID           `json:"emergency"`
	ExtentEligibility policy.ExtentEligibilityView `json:"extentEligibility"`
	ResourceReach     policy.ResourceReachDecision `json:"resourceReach"`
	Extent            roundsExtentDTO              `json:"extent"`
	ResourceRunways   []resourceRunwayDTO          `json:"resourceRunways"`
	ReviewsEnabled    bool                         `json:"reviewsEnabled"`
	MethodsEnabled    bool                         `json:"methodsEnabled"`
	ActiveFamilies    []string                     `json:"activeFamilies"`
	LastReviewTick    *domain.Tick                 `json:"lastReviewTick"`
	Progress          []concernProgressDTO         `json:"progress"`
	Stage             *colonyStageDTO              `json:"stage"`
	Roster            *roundsRosterDTO             `json:"roster"`
	Sections          []roundsSectionDTO           `json:"sections"`
	NoOps             []noOpDTO                    `json:"noOps"`
	LootHolds         []lootHoldDTO                `json:"lootHolds"`
}

// noOpDTO is one detector that raised nothing and why.
type noOpDTO struct {
	Concern domain.ConcernID  `json:"concern"`
	Reason  policy.NoOpReason `json:"reason"`
}

// roundsRosterDTO is the roster planner's recorded report: the per-work-type
// census (owners wanted and found, pawns capable), the skills no assignment
// exercises and each pawn's typed profile, so the dossier can say why a pawn
// holds or lacks a role. Rows are sorted by work type, pawn then skill.
type roundsRosterDTO struct {
	Tick     domain.Tick           `json:"tick"`
	Coverage []roundsCoverageDTO   `json:"coverage"`
	Decaying []roundsDecayingDTO   `json:"decaying"`
	Pawns    []roundsRosterPawnDTO `json:"pawns"`
}
type roundsCoverageDTO struct {
	Work    policy.WorkType `json:"work"`
	Demand  int             `json:"demand"`
	Owners  int             `json:"owners"`
	Capable int             `json:"capable"`
}
type roundsDecayingDTO struct {
	Pawn  policy.PawnID `json:"pawn"`
	Skill string        `json:"skill"`
	Level int           `json:"level"`
}
type roundsRosterPawnDTO struct {
	Pawn      policy.PawnID           `json:"pawn"`
	Age       float64                 `json:"age"`
	Child     bool                    `json:"child"`
	Ranged    bool                    `json:"ranged"`
	Traits    []roundsTraitDTO        `json:"traits"`
	Effects   roundsTraitEffectsDTO   `json:"effects"`
	Skills    []roundsProfileSkillDTO `json:"skills"`
	Incapable []policy.WorkType       `json:"incapable"`
	Forbidden []policy.WorkType       `json:"forbidden"`
}
type roundsTraitDTO struct {
	Name   string `json:"name"`
	Degree int    `json:"degree"`
}

// roundsTraitEffectsDTO is TraitEffects with the boolean preferences
// flattened to their field names, so the launcher lists them without
// knowing the table.
type roundsTraitEffectsDTO struct {
	WorkSpeed        float64  `json:"workSpeed"`
	LearnRate        float64  `json:"learnRate"`
	MoveSpeed        float64  `json:"moveSpeed"`
	Sociable         int      `json:"sociable"`
	ChemicalInterest int      `json:"chemicalInterest"`
	Flags            []string `json:"flags"`
}
type roundsProfileSkillDTO struct {
	Name        string  `json:"name"`
	Level       int     `json:"level"`
	Stored      int     `json:"stored"`
	Passion     string  `json:"passion"`
	Disabled    bool    `json:"disabled"`
	LearnFactor float64 `json:"learnFactor"`
}

// roundsSectionDTO is one held state section: the tick its value
// describes, whether it covers the whole section, the native method that
// produced it and when the store took it.
type roundsSectionDTO struct {
	Section  string `json:"section"`
	Family   string `json:"family"`
	AsOf     int64  `json:"asOf"`
	Complete bool   `json:"complete"`
	Source   string `json:"source"`
	StoredAt string `json:"storedAt"`
}

// colonyStageDTO is the colony stage: its name, the review tick it
// was entered, the first unmet condition of the next stage (blocker,
// empty at Development) with the measured values in words, and whether
// the Foothold hold refuses the comfort-class development.
type colonyStageDTO struct {
	Stage   string      `json:"stage"`
	Since   domain.Tick `json:"since"`
	Blocker string      `json:"blocker"`
	Reason  string      `json:"reason"`
	Held    bool        `json:"held"`
}

// concernProgressDTO is one concern's progress record on the wire: the
// five fields per concern plus the bounded cooldowns keying failed situations
// out. lastProgress and nextReview are ticks; blocked is empty when the
// concern is not blocked.
type concernProgressDTO struct {
	Concern      domain.ConcernID      `json:"concern"`
	Method       string                `json:"method"`
	Expected     string                `json:"expected"`
	LastProgress domain.Tick           `json:"lastProgress"`
	NextReview   domain.Tick           `json:"nextReview"`
	Blocked      policy.BlockedReason  `json:"blocked"`
	Subject      string                `json:"subject,omitempty"`
	Cooldowns    []progressCooldownDTO `json:"cooldowns"`
}

type progressCooldownDTO struct {
	Key   string      `json:"key"`
	Until domain.Tick `json:"until"`
}

func concernProgress(records []policy.ConcernProgress) []concernProgressDTO {
	out := make([]concernProgressDTO, 0, len(records))
	for _, p := range records {
		dto := concernProgressDTO{Concern: p.Concern, Method: p.Method, Expected: p.Expected, LastProgress: p.LastProgress, NextReview: p.NextReview, Blocked: p.Blocked, Subject: p.BlockedSubject(), Cooldowns: []progressCooldownDTO{}}
		for _, c := range p.Cooldowns {
			dto.Cooldowns = append(dto.Cooldowns, progressCooldownDTO{Key: c.Key, Until: c.Until})
		}
		out = append(out, dto)
	}
	return out
}

func roundsStatus(v RoundsStatus) roundsStatusDTO {
	families := v.ActiveFamilies
	if families == nil {
		families = []string{}
	}
	result := roundsStatusDTO{Emergency: append([]policy.ConcernID{}, v.Emergency...), ReviewsEnabled: v.ReviewsEnabled, MethodsEnabled: v.MethodsEnabled, ActiveFamilies: families, Sections: []roundsSectionDTO{}}
	result.ResourceRunways = resourceRunwaysDTO(v.ResourceRunways)
	result.ResourceReach = policy.ResourceReach(v.ResourceReach)
	result.Extent = roundsExtent(v.ResourceReach.Extent)
	result.ExtentEligibility = policy.ExtentEligibility(v.ExtentEligibility)
	result.LootHolds = lootHolds(v.LootHolds)
	result.Progress = concernProgress(v.Progress)
	result.NoOps = []noOpDTO{}
	for _, n := range v.NoOps {
		result.NoOps = append(result.NoOps, noOpDTO{Concern: n.Concern, Reason: n.Reason})
	}
	for _, section := range v.Sections {
		result.Sections = append(result.Sections, roundsSectionDTO{Section: string(section.Section), Family: string(section.Family), AsOf: section.AsOf, Complete: section.Complete, Source: section.Source, StoredAt: section.StoredAt.UTC().Format(time.RFC3339Nano)})
	}
	if v.LastReviewKnown {
		tick := v.LastReviewTick
		result.LastReviewTick = &tick
	}
	if v.Stage != nil {
		result.Stage = &colonyStageDTO{Stage: v.Stage.Stage.String(), Since: v.Stage.Since, Blocker: string(v.Stage.Blocker), Reason: v.Stage.Reason, Held: v.Stage.Held}
	}
	if v.Roster != nil {
		dto := roundsRoster(*v.Roster)
		result.Roster = &dto
	}
	return result
}

func roundsRoster(r policy.WorkRosterReport) roundsRosterDTO {
	dto := roundsRosterDTO{Tick: r.Tick, Coverage: []roundsCoverageDTO{}, Decaying: []roundsDecayingDTO{}, Pawns: []roundsRosterPawnDTO{}}
	for _, c := range r.Coverage {
		dto.Coverage = append(dto.Coverage, roundsCoverageDTO{Work: c.Work, Demand: c.Demand, Owners: c.Owners, Capable: c.Capable})
	}
	sort.SliceStable(dto.Coverage, func(i, j int) bool { return dto.Coverage[i].Work < dto.Coverage[j].Work })
	for _, d := range r.Decaying {
		dto.Decaying = append(dto.Decaying, roundsDecayingDTO{Pawn: d.Pawn, Skill: d.Skill, Level: d.Level})
	}
	sort.SliceStable(dto.Decaying, func(i, j int) bool {
		if dto.Decaying[i].Pawn != dto.Decaying[j].Pawn {
			return dto.Decaying[i].Pawn < dto.Decaying[j].Pawn
		}
		return dto.Decaying[i].Skill < dto.Decaying[j].Skill
	})
	for _, p := range r.Profiles {
		dto.Pawns = append(dto.Pawns, roundsRosterPawn(p))
	}
	sort.SliceStable(dto.Pawns, func(i, j int) bool { return dto.Pawns[i].Pawn < dto.Pawns[j].Pawn })
	return dto
}

func roundsRosterPawn(p policy.PawnProfile) roundsRosterPawnDTO {
	dto := roundsRosterPawnDTO{Pawn: p.ID, Age: p.Age, Child: p.Child, Ranged: p.Ranged, Traits: []roundsTraitDTO{}, Skills: []roundsProfileSkillDTO{}, Incapable: []policy.WorkType{}, Forbidden: []policy.WorkType{}}
	for _, t := range p.Traits {
		dto.Traits = append(dto.Traits, roundsTraitDTO{Name: t.Name, Degree: t.Degree})
	}
	for _, s := range p.Skills {
		dto.Skills = append(dto.Skills, roundsProfileSkillDTO{Name: s.Name, Level: s.Level, Stored: s.Stored, Passion: s.Passion, Disabled: s.Disabled, LearnFactor: s.LearnFactor(p.Effects)})
	}
	sort.SliceStable(dto.Skills, func(i, j int) bool { return dto.Skills[i].Name < dto.Skills[j].Name })
	for w, incapable := range p.Incapable {
		if incapable {
			dto.Incapable = append(dto.Incapable, w)
		}
	}
	sort.Slice(dto.Incapable, func(i, j int) bool { return dto.Incapable[i] < dto.Incapable[j] })
	dto.Forbidden = append(dto.Forbidden, p.ForbiddenWork()...)
	e := p.Effects
	dto.Effects = roundsTraitEffectsDTO{WorkSpeed: e.WorkSpeed, LearnRate: e.LearnRate, MoveSpeed: e.MoveSpeed, Sociable: e.Sociable, ChemicalInterest: e.ChemicalInterest, Flags: e.Flags()}
	return dto
}
