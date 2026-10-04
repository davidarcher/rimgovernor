package policy

import (
	"errors"
	"fmt"
	"math"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ColonyStage is how far the colony has come by outcome (#630): the one
// ordered fact that sets the goal budgets (the research ladder's pace, the
// stall deadline) and which goals the review raises at all
// (StageGoalAllowed). It is derived from colony facts and the goal progress
// records, never from research: BuildTier (#604) is what the colony can
// build, the stage is what it has achieved.
//
// Each stage has explicit exit criteria, all read from the review's one
// live facts (StageColonyFacts):
//
//	Foothold     roofed sleeping for every colonist, a cooking bill, a food
//	             stockpile, food >= FootholdFoodDays, two armed fighters
//	Reserves     food >= FoodTargetDays, the growing field sown, the wood
//	             floor met, a research bench built
//	Stable       power online, the season's climate answered (refrigeration
//	             in spring and summer, sleeping warmth in fall and winter), a
//	             doctor-capable pawn, production unblocked for StableTicks
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

// StageBlocker names the first exit criterion of the current stage the
// colony has not met.
type StageBlocker string

const (
	StageBlockerShelter       StageBlocker = "shelter"
	StageBlockerCooking       StageBlocker = "cooking"
	StageBlockerFoodStorage   StageBlocker = "food_storage"
	StageBlockerStarvation    StageBlocker = "starvation"
	StageBlockerDefense       StageBlocker = "defense"
	StageBlockerFood          StageBlocker = "food_reserve"
	StageBlockerField         StageBlocker = "field"
	StageBlockerWood          StageBlocker = "wood"
	StageBlockerResearchBench StageBlocker = "research_bench"
	StageBlockerPower         StageBlocker = "power"
	StageBlockerClimate       StageBlocker = "climate"
	StageBlockerDoctor        StageBlocker = "doctor"
	StageBlockerProduction    StageBlocker = "production_blocked"
	// StageBlockerSettling: every condition holds and the stage waits out
	// its settling time (StableTicks, DevelopmentTicks).
	StageBlockerSettling StageBlocker = "settling"
	StageBlockerUnknown  StageBlocker = "unknown"
)

func (b StageBlocker) valid() bool {
	switch b {
	case "", StageBlockerShelter, StageBlockerCooking, StageBlockerFoodStorage, StageBlockerStarvation, StageBlockerDefense, StageBlockerFood, StageBlockerField, StageBlockerWood, StageBlockerResearchBench, StageBlockerPower, StageBlockerClimate, StageBlockerDoctor, StageBlockerProduction, StageBlockerSettling, StageBlockerUnknown:
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
	// Blocker is the first unmet exit criterion; empty at Development.
	// Reason is the same in words, with the measured values.
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
// the review's gates, colony facts or goal progress. Unknown facts never
// advance a stage.
type ColonyStageFacts struct {
	// Foothold's exit: Shelter is indoor and bed capacity for every
	// colonist; Cooking an active meal bill on a usable bench; FoodStorage
	// a food stockpile; Armed two armed fighters (every colonist when
	// fewer).
	Shelter, Cooking, FoodStorage, Armed domain.Fact[bool]
	// FoodDays is the food runway the review measured.
	FoodDays domain.Fact[float64]
	// Reserves' exit: FieldSown is growing cells for every colonist, all of
	// them sown; WoodShort the wood latch; ResearchBench a built research
	// bench.
	FieldSown, ResearchBench domain.Fact[bool]
	WoodShort                bool
	// Stable's exit: Power the power gate; Climate the season's
	// temperature answer; Doctor a pawn capable of doctoring.
	Power, Climate, Doctor domain.Fact[bool]
	// ProductionBlocked names the production goal whose record is blocked
	// (ProductionBlockedGoal), "" when none; Blocked is its reason.
	ProductionBlocked ConcernID
	Blocked           BlockedReason
}

// ColonyStagePolicy holds the stage thresholds. Each transition has an
// enter threshold and a laxer exit threshold so a colony oscillating around
// one does not flip stage each review. Zero fields take the defaults
// (RoundsPolicy.Stages).
type ColonyStagePolicy struct {
	// FootholdExitDays is the food runway Foothold needs to exit;
	// ReserveEnterDays the runway Reserves needs to exit; ReserveExitDays
	// the runway under which Reserves (and every stage above) drops to
	// Foothold.
	FootholdExitDays, ReserveEnterDays, ReserveExitDays float64
	// DevelopmentEnterDays is the food runway Development needs;
	// DevelopmentExitDays the runway under which it drops to Stable.
	DevelopmentEnterDays, DevelopmentExitDays float64
	// StableTicks is how long the production goals must stay unblocked to
	// exit Stable; StableExitTicks how long one must stay blocked to drop
	// it. DevelopmentTicks is how long Stable must hold to enter
	// Development.
	StableTicks, StableExitTicks, DevelopmentTicks domain.Tick
	// Floor is the lowest stage whose goals and budgets StageRoundsPolicy
	// applies, whatever the measured stage: a developed colony loaded
	// mid-game, or a test of a later stage's planner, skips the ladder.
	// Zero (Foothold) is no floor; the record keeps the measured stage.
	Floor ColonyStage `json:",omitempty"`
}

// Stages is the stage policy with the routine food thresholds as defaults:
// Foothold exits at FootholdFoodDays, Reserves at FoodTargetDays, and
// every stage above Foothold drops under two thirds of FootholdFoodDays; Development at twice the target, dropped under
// it; two days of unblocked production and three stable days for
// Development, one blocked day to drop Stable.
func (p RoundsPolicy) Stages() ColonyStagePolicy {
	s := p.Stage
	if s.FootholdExitDays <= 0 {
		s.FootholdExitDays = p.FootholdFoodDays
	}
	if s.ReserveEnterDays <= 0 {
		s.ReserveEnterDays = p.FoodTargetDays
	}
	if s.ReserveExitDays <= 0 {
		s.ReserveExitDays = s.FootholdExitDays * 2 / 3
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
	for _, n := range []float64{s.FootholdExitDays, s.ReserveEnterDays, s.ReserveExitDays, s.DevelopmentEnterDays, s.DevelopmentExitDays} {
		if math.IsNaN(n) || math.IsInf(n, 0) || n < 0 {
			return errors.New("invalid colony stage threshold")
		}
	}
	if !s.Floor.valid() {
		return errors.New("invalid colony stage floor")
	}
	if s.StableTicks < 0 || s.StableExitTicks < 0 || s.DevelopmentTicks < 0 {
		return errors.New("invalid colony stage settling time")
	}
	if s.FootholdExitDays > 0 && s.ReserveExitDays > 0 && s.ReserveExitDays > s.FootholdExitDays || s.ReserveEnterDays > 0 && s.ReserveExitDays > 0 && s.ReserveExitDays > s.ReserveEnterDays || s.DevelopmentEnterDays > 0 && s.DevelopmentExitDays > 0 && s.DevelopmentExitDays > s.DevelopmentEnterDays {
		return errors.New("unordered colony stage thresholds")
	}
	return nil
}

// stageCheck is one exit criterion: unknown and unmet facts both hold the
// stage, each named.
type stageCheck struct {
	fact    domain.Fact[bool]
	blocker StageBlocker
	name    string
}

func firstUnmet(checks ...stageCheck) (StageBlocker, string) {
	for _, c := range checks {
		v, known := c.fact.Value()
		switch {
		case !known:
			return StageBlockerUnknown, c.name + " unknown"
		case !v:
			return c.blocker, c.name + " unmet"
		}
	}
	return "", ""
}

// ReviewColonyStage is the stage transition: a pure function of the last
// record, this review's facts, the thresholds and the review tick. Drops
// are checked first and cascade (a starving colony is Foothold whatever it
// was), then the stage climbs at most one step per review, once every exit
// criterion of the current stage holds. Unknown facts never advance a
// stage and never drop one.
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
	foodAtLeast := func(days float64, blocker StageBlocker) stageCheck {
		c := stageCheck{fact: measured(f.FoodDays, func(v float64) bool { return v >= days }), blocker: blocker, name: "food"}
		if foodKnown && food < days {
			c.name = fmt.Sprintf("food %.1f of %.1f days", food, days)
		}
		return c
	}
	// exit reports the first unmet exit criterion of the given stage, ""
	// when the stage above is entered now.
	exit := func(stage ColonyStage, since domain.Tick) (StageBlocker, string) {
		switch stage {
		case StageFoothold:
			return firstUnmet(
				stageCheck{f.Shelter, StageBlockerShelter, "shelter"},
				foodAtLeast(p.FootholdExitDays, StageBlockerStarvation),
				stageCheck{f.Cooking, StageBlockerCooking, "cooking"},
				stageCheck{f.FoodStorage, StageBlockerFoodStorage, "food storage"},
				stageCheck{f.Armed, StageBlockerDefense, "basic defense"},
			)
		case StageReserves:
			wood := domain.Known(!f.WoodShort)
			if b, reason := firstUnmet(
				foodAtLeast(p.ReserveEnterDays, StageBlockerFood),
				stageCheck{f.FieldSown, StageBlockerField, "growing field"},
				stageCheck{wood, StageBlockerWood, "wood floor"},
				stageCheck{f.ResearchBench, StageBlockerResearchBench, "research bench"},
			); b != "" {
				return b, reason
			}
			if r.ProductionBlocked {
				return StageBlockerProduction, fmt.Sprintf("%s blocked %s", f.ProductionBlocked, f.Blocked)
			}
			return "", ""
		case StageStable:
			if b, reason := firstUnmet(
				stageCheck{f.Power, StageBlockerPower, "power"},
				stageCheck{f.Climate, StageBlockerClimate, "seasonal climate"},
				stageCheck{f.Doctor, StageBlockerDoctor, "doctor"},
			); b != "" {
				return b, reason
			}
			if r.ProductionBlocked {
				return StageBlockerProduction, fmt.Sprintf("%s blocked %s", f.ProductionBlocked, f.Blocked)
			}
			// A runway under the development exit names food before the
			// settling time: it is what dropped (or would drop) Development.
			if foodKnown && food < p.DevelopmentExitDays {
				return StageBlockerFood, fmt.Sprintf("food %.1f of %.1f days", food, p.DevelopmentEnterDays)
			}
			// The clear time counts from the stage's own entry: a colony
			// settles into Stable before it is judged ready to develop.
			if clear := now - max(r.ProductionSince, since); clear < p.StableTicks {
				return StageBlockerSettling, fmt.Sprintf("production clear %.1f of %.1f days", days(clear), days(p.StableTicks))
			}
			if held := now - since; held < p.DevelopmentTicks {
				return StageBlockerSettling, fmt.Sprintf("stable %.1f of %.1f days", days(held), days(p.DevelopmentTicks))
			}
			return firstUnmet(foodAtLeast(p.DevelopmentEnterDays, StageBlockerFood))
		}
		return "", ""
	}
	// Climb one step only from a stage that was kept, never in the review
	// that dropped into it.
	if stage == r.Stage && stage < StageDevelopment {
		if b, _ := exit(stage, since); b == "" {
			stage, since = stage+1, now
		}
	}
	r.Blocker, r.Reason = "", ""
	if stage < StageDevelopment {
		r.Blocker, r.Reason = exit(stage, since)
	}
	r.Stage, r.Since = stage, since
	return r
}

func days(t domain.Tick) float64 { return float64(t) / float64(DevelopmentStallTicks) }

// ValidateColonyStage checks a persisted record against the review tick.
func ValidateColonyStage(r ColonyStageRecord, tick domain.Tick) error {
	if !r.Stage.valid() || r.Since < 0 || r.Since > tick || r.ProductionSince < 0 || r.ProductionSince > tick || !r.Blocker.valid() {
		return errors.New("invalid colony stage")
	}
	return nil
}

// productionGoals are the goals whose blocked progress record holds the
// colony out of Development: the food ladder, cooking, food storage and
// the resource floors (wood among them).
var productionConcerns = []ConcernID{EnsureFoodSupply, EnsureCooking, MaintainFoodStorage, MaintainResource}

// ProductionBlockedGoal is the first production goal whose progress record
// is blocked on native evidence (no capable pawn, native refusal, every
// alternative cooled, a prerequisite goal): a goal merely between methods
// (no_method) or reconciling a write is not production stalled.
func ProductionBlockedConcern(progress []ConcernProgress) (ConcernID, BlockedReason) {
	for _, goal := range productionConcerns {
		for _, p := range progress {
			if p.Concern != goal {
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

// StageColonyFacts gathers the stage's facts from one review: the foothold
// facts read live, the food runway, the wood latch, the built
// research bench, the season, the doctors and the production goals'
// progress records.
func StageColonyFacts(needs RoundsFindings, f RoundsFacts, p RoundsPolicy, progress []ConcernProgress) ColonyStageFacts {
	facts := ColonyStageFacts{
		Shelter: allFacts(footholdShelter(f), footholdSleeping(f)), Cooking: f.Cooking, FoodStorage: f.FoodStorage, Armed: footholdArmed(f),
		FoodDays:  f.FoodDays,
		FieldSown: allFacts(footholdProduction(f), measured(f.FieldCoverage, func(v float64) bool { return v >= 1-1e-9 })),
		WoodShort: needs.Latches.Wood, ResearchBench: ResearchBenchBuilt(f.CurrentConstruction),
		Power: footholdPower(f), Climate: SeasonalClimate(f.Calendar, footholdTemperature(f, p), needs.Latches.Refrigeration), Doctor: DoctorCapable(f.WorkProfiles),
	}
	facts.ProductionBlocked, facts.Blocked = ProductionBlockedConcern(progress)
	return facts
}

// ResearchBenchBuilt reports a built research bench of the colony's own.
func ResearchBenchBuilt(observed domain.Fact[CurrentConstruction]) domain.Fact[bool] {
	census, known := observed.Value()
	if !known || !census.Colony {
		return domain.Unknown[bool]()
	}
	for _, row := range census.Buildings {
		if strings.Contains(row.Building.Definition(), "ResearchBench") {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// SeasonalClimate is the season's temperature answer: in spring and summer
// no perishable food is latched warm (refrigeration), in fall and winter
// the sleeping rooms hold their temperature (heat). An unknown season asks
// both.
func SeasonalClimate(calendar domain.Fact[Calendar], temperature domain.Fact[bool], refrigerating bool) domain.Fact[bool] {
	cooled := domain.Known(!refrigerating)
	c, known := calendar.Value()
	switch {
	case !known:
		return allFacts(cooled, temperature)
	case strings.Contains(c.Season, "Spring"), strings.Contains(c.Season, "Summer"):
		return cooled
	}
	return temperature
}

// DoctorCapable reports a colonist able to doctor.
func DoctorCapable(profiles domain.Fact[[]PawnProfile]) domain.Fact[bool] {
	rows, known := profiles.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	for _, p := range rows {
		if !p.Incapable[WorkDoctor] && !p.Child {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}

// stageGoals is the stage each staged goal is first raised at; a goal not
// listed (the foothold goals, emergencies, the cross-stage monitors: medical
// tending, mood, fire, raids) is raised at every stage. A goal before its
// stage is not raised at all, not merely held: it takes no slot and no
// planner runs for it.
var stageConcerns = map[ConcernID]ColonyStage{
	EnsureResearch:            StageReserves,
	MaintainResource:          StageReserves,
	EnsureDefensiveLayout:     StageStable,
	MaintainStoneShell:        StageStable,
	MaintainEquipment:         StageStable,
	MaintainRefrigeration:     StageStable,
	MaintainCleanFacilities:   StageStable,
	MaintainAnimalContainment: StageStable,
	MaintainAnimalFeed:        StageStable,
	MaintainHerd:              StageStable,
	MaintainFlooring:          StageDevelopment,
	MaintainLighting:          StageDevelopment,
	MaintainArt:               StageDevelopment,
}

// StageGoalAllowed reports whether the review raises the goal at the stage.
func StageConcernAllowed(goal ConcernID, stage ColonyStage) bool {
	first, staged := stageConcerns[goal]
	return !staged || stage >= first
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

// StageGoalStallScale is the factor ConcernStallTicks shrinks by at a stage:
// six in-game hours (1/4 of the configured deadline, which defaults to one
// day) at Foothold, so a stuck method rotates within the day while the
// colony has no shelter or starvation runway yet, without churning methods
// that are merely slow (one hour cooled them before a hauler arrived);
// unchanged from Reserves on.
func StageConcernStallScale(stage ColonyStage) float64 {
	if stage == StageFoothold {
		return 1.0 / 4
	}
	return 1
}

// StageRoundsPolicy is p with its budgets set by the stage: the research
// ladder (StageResearchLadder) and the goal-progress stall
// deadline (StageGoalStallScale over ConcernStallTicks). A stage that has not
// been reviewed yet (the zero record) is Foothold.
func StageRoundsPolicy(p RoundsPolicy, stage ColonyStage) RoundsPolicy {
	if !stage.valid() {
		stage = StageFoothold
	}
	if p.Stage.Floor.valid() && stage < p.Stage.Floor {
		stage = p.Stage.Floor
	}
	p.ColonyStage = stage
	p.ResearchLadder = StageResearchLadder(stage, p.ResearchLadder)
	if stallScale := StageConcernStallScale(stage); stallScale != 1 && p.ConcernStallTicks > 0 {
		p.ConcernStallTicks = int64(math.Ceil(float64(p.ConcernStallTicks) * stallScale))
	}
	return p
}
