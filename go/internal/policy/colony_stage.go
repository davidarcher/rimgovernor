package policy

import (
	"errors"
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ColonyStage is how far the colony has come by outcome (#630): the one
// ordered fact that sets the goal budgets (the development-project limit,
// the research ladder's pace, the reserve targets) and which optional
// planners the wave runs. It is derived from colony facts and the goal
// progress records, never from research: BuildTier (#604) is what the
// colony can build, the stage is what it has achieved.
//
//	Foothold     the base stage: shelter for all and no starvation runway
//	             are its own conditions, and while they are unmet the
//	             stage holds every ranked development project
//	Reserves     food runway at the target, wood and resource floors met
//	Stable       production goals unblocked for StableTicks
//	Development  Stable held for DevelopmentTicks with the reserve doubled
type ColonyStage int

const (
	StageFoothold ColonyStage = iota
	StageReserves
	StageStable
	StageDevelopment
)

var colonyStageNames = [...]string{"Foothold", "Reserves", "Stable", "Development"}

func (s ColonyStage) String() string {
	if !s.valid() {
		return "Foothold"
	}
	return colonyStageNames[s]
}

func (s ColonyStage) valid() bool { return s >= StageFoothold && s <= StageDevelopment }

// StageBlocker names the first condition of the next stage the colony has
// not met, or what holds the current one.
type StageBlocker string

const (
	StageBlockerShelter    StageBlocker = "shelter"
	StageBlockerStarvation StageBlocker = "starvation"
	StageBlockerFood       StageBlocker = "food_reserve"
	StageBlockerWood       StageBlocker = "wood"
	StageBlockerResources  StageBlocker = "resources"
	StageBlockerProduction StageBlocker = "production_blocked"
	// StageBlockerSettling: every condition holds and the stage waits out
	// its settling time (StableTicks, DevelopmentTicks).
	StageBlockerSettling StageBlocker = "settling"
	StageBlockerUnknown  StageBlocker = "unknown"
)

func (b StageBlocker) valid() bool {
	switch b {
	case "", StageBlockerShelter, StageBlockerStarvation, StageBlockerFood, StageBlockerWood, StageBlockerResources, StageBlockerProduction, StageBlockerSettling, StageBlockerUnknown:
		return true
	}
	return false
}

// ColonyStageRecord is the stage as the last review left it, persisted with
// the review so the transitions carry their history (hysteresis).
type ColonyStageRecord struct {
	Stage ColonyStage
	// Since is the review tick the stage was entered.
	Since domain.Tick
	// Blocker is the first unmet condition of the next stage; empty at
	// Development. Reason is the same in words, with the measured values.
	Blocker StageBlocker `json:",omitempty"`
	Reason  string       `json:",omitempty"`
	// Held: Foothold's own shelter condition is unmet (the last known
	// shelter gate), so the stage holds every ranked development project
	// and their planners (HoldsDevelopment).
	Held bool `json:",omitempty"`
	// ProductionBlocked and ProductionSince are the production clock: whether
	// a production goal's progress record was blocked at the last review
	// and the tick that state was entered.
	ProductionBlocked bool        `json:",omitempty"`
	ProductionSince   domain.Tick `json:",omitempty"`
}

// HoldsDevelopment reports the Foothold hold: the colony has no shelter for
// everyone yet, so the ranked development projects (StageDevelopmentGoal)
// and the comfort-class planners wait for the builder.
func (r ColonyStageRecord) HoldsDevelopment() bool {
	return r.Stage == StageFoothold && r.Held
}

// ColonyStageFacts is what one review measured for the stage, all of it
// colony facts or goal progress.
type ColonyStageFacts struct {
	// Shelter is the foothold shelter gate (indoor and bed capacity for
	// every colonist).
	Shelter domain.Fact[bool]
	// FoodDays is the food runway the review measured.
	FoodDays domain.Fact[float64]
	// WoodShort is the wood latch (stock under WoodMin until it recovers
	// to WoodTarget).
	WoodShort bool
	// ResourcesShort is MaintainResource's assessed deficit; unknown while
	// the census is.
	ResourcesShort domain.Fact[bool]
	// ProductionBlocked names the production goal whose record is blocked
	// (ProductionBlockedGoal), "" when none; Blocked is its reason.
	ProductionBlocked GoalID
	Blocked           BlockedReason
}

// ColonyStagePolicy holds the stage thresholds. Each transition has an
// enter threshold and a laxer exit threshold so a colony oscillating around
// one does not flip stage each review. Zero fields take the defaults
// (RoutinePolicy.Stages).
type ColonyStagePolicy struct {
	// ReserveEnterDays is the food runway Reserves needs; ReserveExitDays
	// the runway under which Reserves (and every stage above) drops to
	// Foothold.
	ReserveEnterDays, ReserveExitDays float64
	// DevelopmentEnterDays is the food runway Development needs;
	// DevelopmentExitDays the runway under which it drops to Stable.
	DevelopmentEnterDays, DevelopmentExitDays float64
	// StableTicks is how long the production goals must stay unblocked to
	// enter Stable; StableExitTicks how long one must stay blocked to drop
	// it. DevelopmentTicks is how long Stable must hold to enter
	// Development.
	StableTicks, StableExitTicks, DevelopmentTicks domain.Tick
}

// Stages is the stage policy with the routine food thresholds as defaults:
// Reserves at FoodTargetDays, dropped under FootholdFoodDays; Development at
// twice the target, dropped under it; two days of unblocked production for
// Stable, one blocked day to drop it, three stable days for Development.
func (p RoutinePolicy) Stages() ColonyStagePolicy {
	s := p.Stage
	if s.ReserveEnterDays <= 0 {
		s.ReserveEnterDays = p.FoodTargetDays
	}
	if s.ReserveExitDays <= 0 {
		s.ReserveExitDays = p.FootholdFoodDays
	}
	if s.DevelopmentEnterDays <= 0 {
		s.DevelopmentEnterDays = 2 * p.FoodTargetDays
	}
	if s.DevelopmentExitDays <= 0 {
		s.DevelopmentExitDays = p.FoodTargetDays
	}
	if s.StableTicks <= 0 {
		s.StableTicks = 2 * DevelopmentStallTicks
	}
	if s.StableExitTicks <= 0 {
		s.StableExitTicks = DevelopmentStallTicks
	}
	if s.DevelopmentTicks <= 0 {
		s.DevelopmentTicks = 3 * DevelopmentStallTicks
	}
	return s
}

func (s ColonyStagePolicy) validate() error {
	for _, n := range []float64{s.ReserveEnterDays, s.ReserveExitDays, s.DevelopmentEnterDays, s.DevelopmentExitDays} {
		if math.IsNaN(n) || math.IsInf(n, 0) || n < 0 {
			return errors.New("invalid colony stage threshold")
		}
	}
	if s.StableTicks < 0 || s.StableExitTicks < 0 || s.DevelopmentTicks < 0 {
		return errors.New("invalid colony stage settling time")
	}
	if s.ReserveEnterDays > 0 && s.ReserveExitDays > 0 && s.ReserveExitDays > s.ReserveEnterDays || s.DevelopmentEnterDays > 0 && s.DevelopmentExitDays > 0 && s.DevelopmentExitDays > s.DevelopmentEnterDays {
		return errors.New("unordered colony stage thresholds")
	}
	return nil
}

// ReviewColonyStage is the stage transition: a pure function of the last
// record, this review's facts, the thresholds and the review tick. Drops
// are checked first and cascade (a starving colony is Foothold whatever it
// was), then the stage climbs at most one step per review, so Stable's
// settling time starts when Stable is entered. Unknown facts never advance
// a stage and never drop one.
func ReviewColonyStage(previous ColonyStageRecord, f ColonyStageFacts, p ColonyStagePolicy, now domain.Tick) ColonyStageRecord {
	r := previous
	if !r.Stage.valid() || r.Since > now || r.ProductionSince > now {
		r = ColonyStageRecord{Since: now}
	}
	shelter, shelterKnown := f.Shelter.Value()
	if shelterKnown {
		r.Held = !shelter
	}
	blocked := f.ProductionBlocked != ""
	if blocked != r.ProductionBlocked || r.ProductionSince == 0 {
		r.ProductionBlocked, r.ProductionSince = blocked, now
	}
	food, foodKnown := f.FoodDays.Value()
	keepReserves := !(shelterKnown && !shelter) && !(foodKnown && food < p.ReserveExitDays)
	keepStable := keepReserves && !(r.ProductionBlocked && now-r.ProductionSince >= p.StableExitTicks)
	keepDevelopment := keepStable && !(foodKnown && food < p.DevelopmentExitDays)
	stage := r.Stage
	if stage == StageDevelopment && !keepDevelopment {
		stage = StageStable
	}
	if stage == StageStable && !keepStable {
		stage = StageReserves
	}
	if stage == StageReserves && !keepReserves {
		stage = StageFoothold
	}
	since := r.Since
	if stage != r.Stage {
		since = now
	}
	// next reports whether the stage above the given one is entered now,
	// else the first condition holding it.
	next := func(stage ColonyStage, since domain.Tick) (bool, StageBlocker, string) {
		switch stage {
		case StageFoothold:
			switch {
			case !shelterKnown:
				return false, StageBlockerUnknown, "shelter unknown"
			case !shelter:
				return false, StageBlockerShelter, "shelter unmet"
			case !foodKnown:
				return false, StageBlockerUnknown, "food unknown"
			case food < p.ReserveExitDays:
				return false, StageBlockerStarvation, fmt.Sprintf("food %.1f days below %.1f", food, p.ReserveExitDays)
			case food < p.ReserveEnterDays:
				return false, StageBlockerFood, fmt.Sprintf("food %.1f days below %.1f", food, p.ReserveEnterDays)
			case f.WoodShort:
				return false, StageBlockerWood, "wood below floor"
			}
			short, known := f.ResourcesShort.Value()
			switch {
			case !known:
				return false, StageBlockerUnknown, "resource stock unknown"
			case short:
				return false, StageBlockerResources, "resource floor unmet"
			}
			return true, "", ""
		case StageReserves:
			if r.ProductionBlocked {
				return false, StageBlockerProduction, fmt.Sprintf("%s blocked %s", f.ProductionBlocked, f.Blocked)
			}
			// The clear time counts from the stage's own entry: a colony
			// settles into Reserves before it is judged Stable.
			if clear := now - max(r.ProductionSince, since); clear < p.StableTicks {
				return false, StageBlockerSettling, fmt.Sprintf("production clear %.1f of %.1f days", days(clear), days(p.StableTicks))
			}
			return true, "", ""
		case StageStable:
			// A runway under the development exit names food before the
			// settling time: it is what dropped (or would drop) Development.
			if foodKnown && food < p.DevelopmentExitDays {
				return false, StageBlockerFood, fmt.Sprintf("food %.1f days below %.1f", food, p.DevelopmentEnterDays)
			}
			if held := now - since; held < p.DevelopmentTicks {
				return false, StageBlockerSettling, fmt.Sprintf("stable %.1f of %.1f days", days(held), days(p.DevelopmentTicks))
			}
			switch {
			case !foodKnown:
				return false, StageBlockerUnknown, "food unknown"
			case food < p.DevelopmentEnterDays:
				return false, StageBlockerFood, fmt.Sprintf("food %.1f days below %.1f", food, p.DevelopmentEnterDays)
			}
			return true, "", ""
		}
		return false, "", ""
	}
	// Climb one step only from a stage that was kept, never in the review
	// that dropped into it.
	if stage == r.Stage {
		if ok, _, _ := next(stage, since); ok {
			stage, since = stage+1, now
		}
	}
	_, r.Blocker, r.Reason = next(stage, since)
	r.Stage, r.Since = stage, since
	return r
}

func days(t domain.Tick) float64 { return float64(t) / float64(DevelopmentStallTicks) }

// ValidateColonyStage checks a persisted record against the review tick.
func ValidateColonyStage(r ColonyStageRecord, tick domain.Tick) error {
	if !r.Stage.valid() || r.Since < 0 || r.Since > tick || r.ProductionSince < 0 || r.ProductionSince > tick || !r.Blocker.valid() || len(r.Reason) > 256 {
		return errors.New("invalid colony stage")
	}
	return nil
}

// productionGoals are the goals whose blocked progress record holds the
// colony out of Stable: the food ladder, cooking, food storage and the
// wood and resource floors.
var productionGoals = []GoalID{EnsureFoodSupply, EnsureCooking, EnsureFoodStorage, MaintainFoodStorage, MaintainWood, MaintainResource}

// ProductionBlockedGoal is the first production goal whose progress record
// is blocked on native evidence (no capable pawn, native refusal, every
// alternative cooled, a prerequisite goal): a goal merely between methods
// (no_method) or reconciling a write is not production stalled.
func ProductionBlockedGoal(progress []GoalProgress) (GoalID, BlockedReason) {
	for _, goal := range productionGoals {
		for _, p := range progress {
			if p.Goal != goal {
				continue
			}
			switch {
			case p.Blocked == BlockedNoWorker, p.Blocked == BlockedNativeIneligible, p.Blocked == BlockedCooldown, p.Blocked.Prerequisite() != "":
				return goal, p.Blocked
			}
		}
	}
	return "", ""
}

// StageColonyFacts gathers the stage's facts from one review: the shelter
// gates, the food runway, the wood latch, the MaintainResource assessment
// and the production goals' progress records.
func StageColonyFacts(needs RoutineNeeds, f RoutineFacts, progress []GoalProgress) ColonyStageFacts {
	facts := ColonyStageFacts{Shelter: allFacts(needs.Gates.Shelter, needs.Gates.Sleeping), FoodDays: fallback(f.PopulationFoodDays, f.FoodDays), WoodShort: needs.Latches.Wood, ResourcesShort: domain.Unknown[bool]()}
	for _, a := range needs.Assessments {
		if a.ID == MaintainResource && a.Need != domain.NeedUnknown {
			facts.ResourcesShort = domain.Known(a.Need == domain.NeedDeficit)
		}
	}
	facts.ProductionBlocked, facts.Blocked = ProductionBlockedGoal(progress)
	return facts
}

// StageDevelopmentGoal reports the ranked projects the Foothold hold
// withholds: the comfort-class development (a dining room, a stone shell,
// home coverage) the same builder would raise before the shelter stands.
// EnsureExpansion is not held: it raises the indoor capacity the shelter
// gate measures.
func StageDevelopmentGoal(id GoalID) bool {
	switch id {
	case EnsureComfort, MaintainStoneShell, MaintainHomeCoverage:
		return true
	}
	return false
}

// StageDevelopmentLimit is the development-project limit at a stage: the
// configured limit below Development (Foothold's own constraint is the
// hold on the ranked development projects), one more at Development
// (never over the ranking's bound of eight).
func StageDevelopmentLimit(stage ColonyStage, limit int) int {
	if stage == StageDevelopment {
		return min(8, limit+1)
	}
	return limit
}

// stageLadderRungs is how many rungs of the research ladder each stage
// walks: the masonry and power rungs at Foothold, through solar at
// Reserves, through the medieval crafts at Stable, the whole ladder at
// Development.
var stageLadderRungs = [...]int{2, 5, 8, math.MaxInt}

// StageResearchLadder is the research ladder paced to the stage: its first
// rungs only, so a colony without reserves researches what its shell and
// power need and no further.
func StageResearchLadder(stage ColonyStage, ladder []string) []string {
	if !stage.valid() {
		stage = StageFoothold
	}
	if n := stageLadderRungs[stage]; len(ladder) > n {
		return ladder[:n]
	}
	return ladder
}

// StageReserveScale is the factor the reserve targets (the food reserve's
// days, the wood target) grow by at a stage: unchanged until Stable, then
// half again, doubled at Development.
func StageReserveScale(stage ColonyStage) float64 {
	switch stage {
	case StageStable:
		return 1.5
	case StageDevelopment:
		return 2
	}
	return 1
}

// StageRoutinePolicy is p with its budgets set by the stage: the project
// limit (StageDevelopmentLimit), the research ladder (StageResearchLadder)
// and the reserve targets (StageReserveScale over FoodReserveDays, WoodTarget
// and WoodMax, within the policy's own bounds). A stage that has not been
// reviewed yet (the zero record) is Foothold.
func StageRoutinePolicy(p RoutinePolicy, stage ColonyStage) RoutinePolicy {
	p.MaxDevelopmentProjects = StageDevelopmentLimit(stage, p.MaxDevelopmentProjects)
	p.ResearchLadder = StageResearchLadder(stage, p.ResearchLadder)
	scale := StageReserveScale(stage)
	if scale != 1 {
		p.FoodReserveDays = math.Min(60, p.FoodReserveDays*scale)
		p.WoodTarget = int64(math.Ceil(float64(p.WoodTarget) * scale))
		p.WoodMax = max(p.WoodMax, int64(math.Ceil(float64(p.WoodMax)*scale)))
	}
	return p
}
