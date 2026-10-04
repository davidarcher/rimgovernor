package httpapi

import (
	"context"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// RoutineProvider reports which composed routine planner families this
// process wired up at startup and the durable review cursor's progress, for
// read-only runtime diagnostics. Implementations must not trigger native
// calls or mutate state.
type RoutineProvider interface {
	RoutineStatus(context.Context) (RoutineStatus, error)
}

// RoutineStatus is a runtime snapshot of the composed routine runtime.
// ActiveFamilies names every routine planner family this process enabled
// (composed default or explicit), regardless of whether it currently has
// pending work; LastReviewTick is the durable review cursor's most recent
// reviewed tick, known once at least one review has run. Development is the
// ranking the last review recorded, nil until a review has run. Sections
// are the state store's held census sections with the tick each describes
// (facts.Store, #354), so a live serve shows staleness per section.
type RoutineStatus struct {
	ExtentEligibility policy.ExtentEligibilityRequest
	// ResourceReach holds same-observation inputs; absent facts stay unknown.
	ResourceReach   policy.ResourceReachRequest
	ResourceRunways []policy.ResourceRunway
	ReviewsEnabled  bool
	MethodsEnabled  bool
	ActiveFamilies  []string
	LastReviewTick  domain.Tick
	LastReviewKnown bool
	Development     *policy.DevelopmentState
	// Progress is every active concern's progress record from the last review
	// (#629): method, expected observable, last progress tick, next review
	// tick and blocker.
	Progress []policy.GoalProgress
	// Stage is the colony stage the last review derived (#630) with the
	// first unmet condition of the next; nil until an enabled review
	// filed one.
	Stage *policy.ColonyStageRecord
	// Roster is the roster planner's last recorded report (#448), nil until
	// an enabled review planned work.
	Roster *policy.WorkRosterReport
	// LayoutTidy is the last review's TidyLayout outcome (#611): the pending
	// re-site with its explanation, or why none stands.
	LayoutTidy *policy.TidyReview
	Sections   []facts.Status
	// NoOps are the inspections that raised nothing in the last enabled
	// review, with the typed reason (#1909).
	NoOps []policy.NoOpRecord
	// LootHolds are the safe forbidden stacks the last review's reach stage
	// or demand kept forbidden, with reasons (#522).
	LootHolds []policy.LootHold
}

type routineStatusDTO struct {
	ExtentEligibility policy.ExtentEligibilityView `json:"extentEligibility"`
	ResourceReach     policy.ResourceReachDecision `json:"resourceReach"`
	Extent            routineExtentDTO             `json:"extent"`
	ResourceRunways   []resourceRunwayDTO          `json:"resourceRunways"`
	ReviewsEnabled    bool                         `json:"reviewsEnabled"`
	MethodsEnabled    bool                         `json:"methodsEnabled"`
	ActiveFamilies    []string                     `json:"activeFamilies"`
	LastReviewTick    *domain.Tick                 `json:"lastReviewTick"`
	Development       *routineDevelopmentDTO       `json:"development"`
	Progress          []concernProgressDTO         `json:"progress"`
	Stage             *colonyStageDTO              `json:"stage"`
	Roster            *routineRosterDTO            `json:"roster"`
	LayoutTidy        *layoutTidyDTO               `json:"layoutTidy"`
	Sections          []routineSectionDTO          `json:"sections"`
	NoOps             []noOpDTO                    `json:"noOps"`
	LootHolds         []lootHoldDTO                `json:"lootHolds"`
}

// noOpDTO is one detector that raised nothing and why.
type noOpDTO struct {
	Concern domain.ConcernID  `json:"concern"`
	Reason  policy.NoOpReason `json:"reason"`
}

// layoutTidyDTO is the TidyLayout review for the development panel: the
// gate outcome, the candidate count and the pending proposal, if any.
type layoutTidyDTO struct {
	Active     bool             `json:"active"`
	Reason     string           `json:"reason"`
	Candidates int              `json:"candidates"`
	Proposal   *tidyProposalDTO `json:"proposal"`
}
type tidyProposalDTO struct {
	Kind        string       `json:"kind"`
	Item        string       `json:"item"`
	From        rectangleDTO `json:"from"`
	To          rectangleDTO `json:"to"`
	Crop        string       `json:"crop"`
	Gain        int          `json:"gain"`
	Distance    int32        `json:"distance"`
	Explanation string       `json:"explanation"`
}
type rectangleDTO struct {
	X      int32 `json:"x"`
	Z      int32 `json:"z"`
	Width  int32 `json:"width"`
	Height int32 `json:"height"`
}

func rectangle(r policy.Rectangle) rectangleDTO {
	return rectangleDTO{X: r.X, Z: r.Z, Width: r.Width, Height: r.Height}
}
func layoutTidy(v *policy.TidyReview) *layoutTidyDTO {
	if v == nil || !v.Known {
		return nil
	}
	dto := &layoutTidyDTO{Active: v.Active, Reason: v.Reason, Candidates: v.Candidates}
	if p := v.Proposal; p != nil {
		dto.Proposal = &tidyProposalDTO{Kind: string(p.Item.Kind), Item: p.Item.ID, From: rectangle(p.Item.Footprint), To: rectangle(p.Target), Crop: p.Item.Crop, Gain: p.Gain, Distance: p.Distance, Explanation: p.Explanation}
	}
	return dto
}

// routineRosterDTO is the roster planner's recorded report: the per-work-type
// census (owners wanted and found, pawns capable), the skills no assignment
// exercises and each pawn's typed profile, so the dossier can say why a pawn
// holds or lacks a role. Rows are sorted by work type, pawn then skill.
type routineRosterDTO struct {
	Tick     domain.Tick            `json:"tick"`
	Coverage []routineCoverageDTO   `json:"coverage"`
	Decaying []routineDecayingDTO   `json:"decaying"`
	Pawns    []routineRosterPawnDTO `json:"pawns"`
}
type routineCoverageDTO struct {
	Work    policy.WorkType `json:"work"`
	Demand  int             `json:"demand"`
	Owners  int             `json:"owners"`
	Capable int             `json:"capable"`
}
type routineDecayingDTO struct {
	Pawn  policy.PawnID `json:"pawn"`
	Skill string        `json:"skill"`
	Level int           `json:"level"`
}
type routineRosterPawnDTO struct {
	Pawn      policy.PawnID            `json:"pawn"`
	Age       float64                  `json:"age"`
	Child     bool                     `json:"child"`
	Ranged    bool                     `json:"ranged"`
	Traits    []routineTraitDTO        `json:"traits"`
	Effects   routineTraitEffectsDTO   `json:"effects"`
	Skills    []routineProfileSkillDTO `json:"skills"`
	Incapable []policy.WorkType        `json:"incapable"`
	Forbidden []policy.WorkType        `json:"forbidden"`
}
type routineTraitDTO struct {
	Name   string `json:"name"`
	Degree int    `json:"degree"`
}

// routineTraitEffectsDTO is TraitEffects with the boolean preferences
// flattened to their field names, so the dashboard lists them without
// knowing the table.
type routineTraitEffectsDTO struct {
	WorkSpeed        float64  `json:"workSpeed"`
	LearnRate        float64  `json:"learnRate"`
	MoveSpeed        float64  `json:"moveSpeed"`
	Sociable         int      `json:"sociable"`
	ChemicalInterest int      `json:"chemicalInterest"`
	Flags            []string `json:"flags"`
}
type routineProfileSkillDTO struct {
	Name        string  `json:"name"`
	Level       int     `json:"level"`
	Stored      int     `json:"stored"`
	Passion     string  `json:"passion"`
	Disabled    bool    `json:"disabled"`
	LearnFactor float64 `json:"learnFactor"`
}

// routineSectionDTO is one held state section: the tick its value
// describes, whether it covers the whole section, the native method that
// produced it and when the store took it.
type routineSectionDTO struct {
	Section  string `json:"section"`
	Family   string `json:"family"`
	AsOf     int64  `json:"asOf"`
	Complete bool   `json:"complete"`
	Source   string `json:"source"`
	StoredAt string `json:"storedAt"`
}

// colonyStageDTO is the colony stage (#630): its name, the review tick it
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

// routineDevelopmentDTO is the recorded development ranking: bounded
// admission (capacity, labor) and every optional concern's ordering evidence and
// deferral reason. Unknown facts are null; labor rows are sorted by work type.
type routineDevelopmentDTO struct {
	Tick      domain.Tick                `json:"tick"`
	Workers   *int                       `json:"workers"`
	Labor     []routineLaborDTO          `json:"labor"`
	Capacity  int                        `json:"capacity"`
	Committed []domain.ConcernID         `json:"committed"`
	Rows      []routineDevelopmentRowDTO `json:"rows"`
	// Capacity bounds planner cost; distinct observed workers decide
	// admission. HeldWorkers is labor open startup work and withheld prerequisites
	// hold without a slot; Limiting the
	// first reason an eligible concern was left unselected.
	HeldWorkers int                      `json:"heldWorkers"`
	Limiting    policy.DevelopmentReason `json:"limiting"`
}
type routineLaborDTO struct {
	Work policy.WorkType `json:"work"`
	Free int             `json:"free"`
}
type routineDevelopmentRowDTO struct {
	Concern      domain.ConcernID         `json:"concern"`
	Score        float64                  `json:"score"`
	Deficit      *float64                 `json:"deficit"`
	Risk         *float64                 `json:"risk"`
	WaitingSince domain.Tick              `json:"waitingSince"`
	Selected     bool                     `json:"selected"`
	Committed    bool                     `json:"committed"`
	Reason       policy.DevelopmentReason `json:"reason"`
	Bottleneck   policy.WorkType          `json:"bottleneck"`
	// Donation is the ordering the row inherited from a concern waiting on
	// its shortfall (#651); absent when it serves none.
	Donation *routineDonationDTO `json:"donation,omitempty"`
}

// routineDonationDTO: priority is the effective ordering (the concern's own
// priority is unchanged), chain runs from the originating concern to this one,
// shortfall is the bounded demand, conflict names an operator ceiling that
// kept the row from a slot.
type routineDonationDTO struct {
	Priority  int                `json:"priority"`
	Chain     []domain.ConcernID `json:"chain"`
	Resource  policy.Resource    `json:"resource,omitempty"`
	Shortfall int64              `json:"shortfall,omitempty"`
	Conflict  string             `json:"conflict,omitempty"`
}

// concernProgressDTO is one concern's progress record on the wire (#629): the
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
	Cooldowns    []progressCooldownDTO `json:"cooldowns"`
}

type progressCooldownDTO struct {
	Key   string      `json:"key"`
	Until domain.Tick `json:"until"`
}

func concernProgress(records []policy.GoalProgress) []concernProgressDTO {
	out := make([]concernProgressDTO, 0, len(records))
	for _, p := range records {
		dto := concernProgressDTO{Concern: p.Goal, Method: p.Method, Expected: p.Expected, LastProgress: p.LastProgress, NextReview: p.NextReview, Blocked: p.Blocked, Cooldowns: []progressCooldownDTO{}}
		for _, c := range p.Cooldowns {
			dto.Cooldowns = append(dto.Cooldowns, progressCooldownDTO{Key: c.Key, Until: c.Until})
		}
		out = append(out, dto)
	}
	return out
}

func routineStatus(v RoutineStatus) routineStatusDTO {
	families := v.ActiveFamilies
	if families == nil {
		families = []string{}
	}
	result := routineStatusDTO{ReviewsEnabled: v.ReviewsEnabled, MethodsEnabled: v.MethodsEnabled, ActiveFamilies: families, Sections: []routineSectionDTO{}}
	result.ResourceRunways = resourceRunwaysDTO(v.ResourceRunways)
	result.ResourceReach = policy.ResourceReach(v.ResourceReach)
	result.Extent = routineExtent(v.ResourceReach.Extent)
	result.ExtentEligibility = policy.ExtentEligibility(v.ExtentEligibility)
	result.LootHolds = lootHolds(v.LootHolds)
	result.Progress = concernProgress(v.Progress)
	result.NoOps = []noOpDTO{}
	for _, n := range v.NoOps {
		result.NoOps = append(result.NoOps, noOpDTO{Concern: n.Goal, Reason: n.Reason})
	}
	for _, section := range v.Sections {
		result.Sections = append(result.Sections, routineSectionDTO{Section: string(section.Section), Family: string(section.Family), AsOf: section.AsOf, Complete: section.Complete, Source: section.Source, StoredAt: section.StoredAt.UTC().Format(time.RFC3339Nano)})
	}
	if v.LastReviewKnown {
		tick := v.LastReviewTick
		result.LastReviewTick = &tick
	}
	if v.Development != nil {
		dto := routineDevelopment(*v.Development)
		result.Development = &dto
	}
	if v.Stage != nil {
		result.Stage = &colonyStageDTO{Stage: v.Stage.Stage.String(), Since: v.Stage.Since, Blocker: string(v.Stage.Blocker), Reason: v.Stage.Reason, Held: v.Stage.Held}
	}
	if v.Roster != nil {
		dto := routineRoster(*v.Roster)
		result.Roster = &dto
	}
	result.LayoutTidy = layoutTidy(v.LayoutTidy)
	return result
}

func routineRoster(r policy.WorkRosterReport) routineRosterDTO {
	dto := routineRosterDTO{Tick: r.Tick, Coverage: []routineCoverageDTO{}, Decaying: []routineDecayingDTO{}, Pawns: []routineRosterPawnDTO{}}
	for _, c := range r.Coverage {
		dto.Coverage = append(dto.Coverage, routineCoverageDTO{Work: c.Work, Demand: c.Demand, Owners: c.Owners, Capable: c.Capable})
	}
	sort.SliceStable(dto.Coverage, func(i, j int) bool { return dto.Coverage[i].Work < dto.Coverage[j].Work })
	for _, d := range r.Decaying {
		dto.Decaying = append(dto.Decaying, routineDecayingDTO{Pawn: d.Pawn, Skill: d.Skill, Level: d.Level})
	}
	sort.SliceStable(dto.Decaying, func(i, j int) bool {
		if dto.Decaying[i].Pawn != dto.Decaying[j].Pawn {
			return dto.Decaying[i].Pawn < dto.Decaying[j].Pawn
		}
		return dto.Decaying[i].Skill < dto.Decaying[j].Skill
	})
	for _, p := range r.Profiles {
		dto.Pawns = append(dto.Pawns, routineRosterPawn(p))
	}
	sort.SliceStable(dto.Pawns, func(i, j int) bool { return dto.Pawns[i].Pawn < dto.Pawns[j].Pawn })
	return dto
}

func routineRosterPawn(p policy.PawnProfile) routineRosterPawnDTO {
	dto := routineRosterPawnDTO{Pawn: p.ID, Age: p.Age, Child: p.Child, Ranged: p.Ranged, Traits: []routineTraitDTO{}, Skills: []routineProfileSkillDTO{}, Incapable: []policy.WorkType{}, Forbidden: []policy.WorkType{}}
	for _, t := range p.Traits {
		dto.Traits = append(dto.Traits, routineTraitDTO{Name: t.Name, Degree: t.Degree})
	}
	for _, s := range p.Skills {
		dto.Skills = append(dto.Skills, routineProfileSkillDTO{Name: s.Name, Level: s.Level, Stored: s.Stored, Passion: s.Passion, Disabled: s.Disabled, LearnFactor: s.LearnFactor(p.Effects)})
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
	dto.Effects = routineTraitEffectsDTO{WorkSpeed: e.WorkSpeed, LearnRate: e.LearnRate, MoveSpeed: e.MoveSpeed, Sociable: e.Sociable, ChemicalInterest: e.ChemicalInterest, Flags: []string{}}
	for _, flag := range []struct {
		name string
		set  bool
	}{{"GreatMemory", e.GreatMemory}, {"QuickSleeper", e.QuickSleeper}, {"NightShift", e.NightShift}, {"MeleeOnly", e.MeleeOnly}, {"FrontLine", e.FrontLine}, {"RearRanged", e.RearRanged}, {"Pyromaniac", e.Pyromaniac}, {"Execution", e.Execution}, {"SurgeonSafe", e.SurgeonSafe}, {"Nudist", e.Nudist}, {"Ascetic", e.Ascetic}, {"Cannibal", e.Cannibal}, {"Gourmand", e.Gourmand}, {"Undergrounder", e.Undergrounder}, {"Greedy", e.Greedy}, {"Jealous", e.Jealous}} {
		if flag.set {
			dto.Effects.Flags = append(dto.Effects.Flags, flag.name)
		}
	}
	for _, work := range e.DisabledWork {
		dto.Effects.Flags = append(dto.Effects.Flags, "No"+string(work))
	}
	return dto
}

func routineDevelopment(s policy.DevelopmentState) routineDevelopmentDTO {
	dto := routineDevelopmentDTO{Tick: s.Tick, Capacity: s.Capacity, Labor: []routineLaborDTO{}, Committed: []domain.ConcernID{}, Rows: []routineDevelopmentRowDTO{}, Limiting: s.Limiting}
	for _, h := range s.Holds {
		if !h.Slot && len(h.Labor) > 0 {
			dto.HeldWorkers++
		}
	}
	if v, k := s.Workers.Value(); k {
		dto.Workers = &v
	}
	if labor, k := s.Labor.Value(); k {
		for w, n := range labor {
			dto.Labor = append(dto.Labor, routineLaborDTO{Work: w, Free: n})
		}
		sort.Slice(dto.Labor, func(i, j int) bool { return dto.Labor[i].Work < dto.Labor[j].Work })
	}
	dto.Committed = append(dto.Committed, s.Committed...)
	for _, row := range s.Rows {
		v := routineDevelopmentRowDTO{Concern: row.Goal, Score: row.Score, WaitingSince: row.WaitingSince, Selected: row.Selected, Committed: row.Committed, Reason: row.Reason, Bottleneck: row.Bottleneck}
		if d, k := row.Deficit.Value(); k {
			v.Deficit = &d
		}
		if r, k := row.Risk.Value(); k {
			v.Risk = &r
		}
		if d := row.Donation; d != nil {
			v.Donation = &routineDonationDTO{Priority: d.Priority, Chain: append([]domain.ConcernID{}, d.Chain...), Resource: d.Resource, Shortfall: d.Shortfall, Conflict: d.Conflict}
		}
		dto.Rows = append(dto.Rows, v)
	}
	return dto
}
