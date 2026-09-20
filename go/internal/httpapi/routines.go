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
	// Progress is every active goal's progress record from the last review
	// (#629): method, expected observable, last progress tick, next review
	// tick and blocker.
	Progress []policy.GoalProgress
	// Stage is the colony stage the last review derived (#630) with the
	// first unmet condition of the next; nil until an enabled review
	// filed one.
	Stage *policy.ColonyStageRecord
	// Roster is the roster planner's last recorded report (#448), nil until
	// an enabled review planned work.
	Roster   *policy.WorkRosterReport
	Sections []facts.Status
	// LootHolds are the safe forbidden stacks the last review's reach stage
	// or demand kept forbidden, with reasons (#522).
	LootHolds []policy.LootHold
	// ColonyGrid is the persisted layout grid the held colony section
	// carries (#605), with the map bounds the overlay draws it over.
	ColonyGrid domain.Fact[policy.ColonyGrid]
	Bounds     policy.Bounds
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
	Progress          []goalProgressDTO            `json:"progress"`
	Stage             *colonyStageDTO              `json:"stage"`
	Roster            *routineRosterDTO            `json:"roster"`
	Sections          []routineSectionDTO          `json:"sections"`
	LootHolds         []lootHoldDTO                `json:"lootHolds"`
	ColonyGrid        *colonyGridDTO               `json:"colonyGrid"`
}

// colonyGridDTO is the persisted colony grid for the dashboard overlay:
// origin and axes in map cells, the pitch, the evidence the origin came
// from and the map bounds to draw the lines across.
type colonyGridDTO struct {
	Origin cellDTO    `json:"origin"`
	Pitch  int32      `json:"pitch"`
	Axes   [2]cellDTO `json:"axes"`
	Source string     `json:"source"`
	Bounds boundsDTO  `json:"bounds"`
}
type cellDTO struct {
	X int32 `json:"x"`
	Z int32 `json:"z"`
}
type boundsDTO struct {
	Width  int32 `json:"width"`
	Height int32 `json:"height"`
}

func colonyGrid(f domain.Fact[policy.ColonyGrid], bounds policy.Bounds) *colonyGridDTO {
	g, known := f.Value()
	if !known {
		return nil
	}
	return &colonyGridDTO{Origin: cellDTO{X: g.Origin.X, Z: g.Origin.Z}, Pitch: g.Pitch, Axes: [2]cellDTO{{X: g.Axes[0].X, Z: g.Axes[0].Z}, {X: g.Axes[1].X, Z: g.Axes[1].Z}}, Source: string(g.Source), Bounds: boundsDTO{Width: bounds.Width, Height: bounds.Height}}
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
	// Stale is what a narrowed invalidation marked since the value was
	// held (#359): entity ids, an inclusive cell rectangle [minX, minZ,
	// maxX, maxZ], or the whole section; absent while nothing is marked.
	Stale *routineStaleDTO `json:"stale,omitempty"`
}

type routineStaleDTO struct {
	IDs  []string `json:"ids,omitempty"`
	Rect []int32  `json:"rect,omitempty"`
	All  bool     `json:"all,omitempty"`
}

func routineStale(st facts.Staleness) *routineStaleDTO {
	if !st.Any() {
		return nil
	}
	out := &routineStaleDTO{IDs: st.IDs, All: st.All}
	if st.Rect != nil {
		out.Rect = []int32{st.Rect.MinX, st.Rect.MinZ, st.Rect.MaxX, st.Rect.MaxZ}
	}
	return out
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
// admission (capacity, labor) and every optional goal's ordering evidence and
// deferral reason. Unknown facts are null; labor rows are sorted by work type.
type routineDevelopmentDTO struct {
	Tick      domain.Tick                `json:"tick"`
	Workers   *int                       `json:"workers"`
	Labor     []routineLaborDTO          `json:"labor"`
	Capacity  int                        `json:"capacity"`
	Committed []domain.GoalID            `json:"committed"`
	Rows      []routineDevelopmentRowDTO `json:"rows"`
}
type routineLaborDTO struct {
	Work policy.WorkType `json:"work"`
	Free int             `json:"free"`
}
type routineDevelopmentRowDTO struct {
	Goal         domain.GoalID            `json:"goal"`
	Score        float64                  `json:"score"`
	Deficit      *float64                 `json:"deficit"`
	Risk         *float64                 `json:"risk"`
	WaitingSince domain.Tick              `json:"waitingSince"`
	Selected     bool                     `json:"selected"`
	Committed    bool                     `json:"committed"`
	Reason       policy.DevelopmentReason `json:"reason"`
	Bottleneck   policy.WorkType          `json:"bottleneck"`
}

// goalProgressDTO is one goal's progress record on the wire (#629): the
// five fields per goal plus the bounded cooldowns keying failed situations
// out. lastProgress and nextReview are ticks; blocked is empty when the
// goal is not blocked.
type goalProgressDTO struct {
	Goal         domain.GoalID         `json:"goal"`
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

func goalProgress(records []policy.GoalProgress) []goalProgressDTO {
	out := make([]goalProgressDTO, 0, len(records))
	for _, p := range records {
		dto := goalProgressDTO{Goal: p.Goal, Method: p.Method, Expected: p.Expected, LastProgress: p.LastProgress, NextReview: p.NextReview, Blocked: p.Blocked, Cooldowns: []progressCooldownDTO{}}
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
	result.ColonyGrid = colonyGrid(v.ColonyGrid, v.Bounds)
	result.Progress = goalProgress(v.Progress)
	for _, section := range v.Sections {
		result.Sections = append(result.Sections, routineSectionDTO{Section: string(section.Section), Family: string(section.Family), AsOf: section.AsOf, Complete: section.Complete, Source: section.Source, StoredAt: section.StoredAt.UTC().Format(time.RFC3339Nano), Stale: routineStale(section.Stale)})
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
	}{{"GreatMemory", e.GreatMemory}, {"QuickSleeper", e.QuickSleeper}, {"NightShift", e.NightShift}, {"MeleeOnly", e.MeleeOnly}, {"FrontLine", e.FrontLine}, {"RearRanged", e.RearRanged}, {"NoFirefighting", e.NoFirefighting}, {"Pyromaniac", e.Pyromaniac}, {"Execution", e.Execution}, {"SurgeonSafe", e.SurgeonSafe}, {"Nudist", e.Nudist}, {"Ascetic", e.Ascetic}, {"Cannibal", e.Cannibal}, {"Gourmand", e.Gourmand}, {"Undergrounder", e.Undergrounder}, {"Greedy", e.Greedy}, {"Jealous", e.Jealous}} {
		if flag.set {
			dto.Effects.Flags = append(dto.Effects.Flags, flag.name)
		}
	}
	return dto
}

func routineDevelopment(s policy.DevelopmentState) routineDevelopmentDTO {
	dto := routineDevelopmentDTO{Tick: s.Tick, Capacity: s.Capacity, Labor: []routineLaborDTO{}, Committed: []domain.GoalID{}, Rows: []routineDevelopmentRowDTO{}}
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
		v := routineDevelopmentRowDTO{Goal: row.Goal, Score: row.Score, WaitingSince: row.WaitingSince, Selected: row.Selected, Committed: row.Committed, Reason: row.Reason, Bottleneck: row.Bottleneck}
		if d, k := row.Deficit.Value(); k {
			v.Deficit = &d
		}
		if r, k := row.Risk.Value(); k {
			v.Risk = &r
		}
		dto.Rows = append(dto.Rows, v)
	}
	return dto
}
