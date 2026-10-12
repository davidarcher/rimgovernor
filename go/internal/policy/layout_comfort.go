package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Comfort and beauty pricing (#2975). Furniture buys mood through two needs the
// game keeps as one shape, a Need_Seeker: a level that climbs toward an
// "instant" level while something gives it and falls toward it otherwise. The
// planner prices the mood of a better chair or bed by simulating the need over
// a colonist's day with and without it, reading the thought stage the level is
// in, and pricing the difference of the time-averaged mood with the mood
// currency (PriceMood, #2954: a mood point is break risk) against the
// furniture's cost in sustained watts (BuildW).
//
// What the game says (ilspycmd, Verse.Need, RimWorld.Need_Seeker, Need_Comfort,
// Need_Beauty, PawnUtility.GainComfortFromCellIfPossible, Toils_Recipe,
// Toils_Ingest, Toils_LayDown, ThoughtWorker_NeedComfort / NeedBeauty; NeedDef
// rows from the catalog):
//   - Every 150 ticks Need_Seeker.NeedInterval moves the level toward the
//     instant level, up by seekerRisePerHour x 0.06 (the hour is 16.67
//     intervals, so a full hour moves it by seekerRisePerHour) or down by
//     seekerFallPerHour x 0.06, never past the instant level. A need frozen
//     (freezeWhileSleeping and asleep) does not move.
//   - Comfort: the instant level is the comfort stat of the building the pawn
//     stands on while it was used in the last 15 ticks, else 0. It is used when
//     lying in a bed (Toils_LayDown), eating (Toils_Ingest, any edifice with the
//     stat), and doing recipe work or research (chairsOnly: the edifice must be a
//     sittable building, so a bench worked standing up gives nothing). Rise 0.60
//     an hour, fall 0.04, not frozen by sleep. The thought NeedComfort has the
//     stage <0.1 Uncomfortable (-3), 0.1-0.6 none, then +4, +6, +8, +10 from 0.6,
//     0.7, 0.8, 0.9.
//   - Beauty: the instant level is 0.4 (baseLevel) plus 0.1 of the mean beauty
//     of the cells within 8.9 of the pawn that its room sees (BeautyUtility),
//     clamped; rise 0.32 an hour, fall 0.08, frozen asleep. The thought
//     NeedBeauty has the stages -15 up to 0.01, -10 to 0.15, -5 to 0.35, none to
//     0.65, then +5, +10, +15 above 0.65, 0.85 and 0.99.
//   - The comfort stat is base (stuff) times quality, plus the offsets of the
//     facilities linked to the building (an end table, a dresser). Quality is the
//     crafter's, not the planner's; it is priced at Normal (factor 1).
//
// The day is the schedule template's (pawn_schedule.go): the sleep block on the
// bed, the Anything hours spent at the spots in order, then idle at an instant
// level of 0 (nothing is used), and the Joy hour idle: the order matters little
// (comfort falls 4% an hour) and is fixed. The pawn repeats it until the level
// at its start stops changing; the day read is that steady one.
//
// Dwell is derived, never guessed: sleep from the schedule, a bench's hours from
// the station graph (the node's DailyWork over the colonists, who share the
// work evenly at work speed 1), and a spot with no dwell model (dining, which
// needs the ingest time, and recreation, which has no model yet) is left out:
// the day without it is a lower bound of the level. The advice is advisory beside
// the old planner, which stays authoritative until #2931.

// SeekerBands maps a need level to a thought stage (-1 for no stage). Level is
// in the band k when it is below Edges[k] (at most it, when UpperClosed), and in
// the last band above every edge. Comfort's category test is level < edge
// (Need_Comfort.CurCategory), beauty's level > edge (Need_Beauty.CurCategory),
// which is the band closed above.
type SeekerBands struct {
	Edges       []float64
	Stages      []int
	UpperClosed bool
}

// Stage is the thought stage of a need level, -1 when the level gives none.
func (b SeekerBands) Stage(level float64) int {
	for k, edge := range b.Edges {
		if level < edge || b.UpperClosed && level == edge {
			return b.Stages[k]
		}
	}
	return b.Stages[len(b.Stages)-1]
}

// maxStage is the highest stage index.
func (b SeekerBands) maxStage() int {
	top := -1
	for _, s := range b.Stages {
		top = max(top, s)
	}
	return top
}

// SeekerGameDef is one need's game numbers that its NeedDef row does not carry:
// the thought the level is read through and its category thresholds.
type SeekerGameDef struct {
	Need, Thought string
	Bands         SeekerBands
}

// SeekerTable is the environment needs' game table, registered in DefTables so
// a renamed def fails the dangling-reference gate. IntervalTicks is
// Pawn_NeedsTracker's need interval (IsHashIntervalTick(150)); BeautyImpact is
// Need_Beauty.BeautyImpactFactor, the level a point of mean beauty moves. The
// rise, fall, base level and sleep freeze are the NeedDef rows', read by the
// bridge.
type SeekerTable struct {
	IntervalTicks, BeautyImpact float64
	Comfort, Beauty             SeekerGameDef
}

var seekerGame = SeekerTable{
	IntervalTicks: 150,
	BeautyImpact:  0.1,
	Comfort: SeekerGameDef{Need: "Comfort", Thought: "NeedComfort", Bands: SeekerBands{
		Edges:  []float64{0.1, 0.6, 0.7, 0.8, 0.9},
		Stages: []int{0, -1, 1, 2, 3, 4},
	}},
	Beauty: SeekerGameDef{Need: "Beauty", Thought: "NeedBeauty", Bands: SeekerBands{
		Edges:       []float64{0.01, 0.15, 0.35, 0.65, 0.85, 0.99},
		Stages:      []int{0, 1, 2, -1, 3, 4, 5},
		UpperClosed: true,
	}},
}

// SeekerGame is the table the bridge reads the needs with.
func SeekerGame() SeekerTable { return seekerGame }

func seekerNeedDefs() []string {
	return []string{seekerGame.Beauty.Need, seekerGame.Comfort.Need}
}

func seekerThoughts() []string {
	return []string{seekerGame.Beauty.Thought, seekerGame.Comfort.Thought}
}

// NeedCurve is a seeker need's NeedDef numbers: the level it starts from, how
// fast it rises and falls an hour, and whether sleep freezes it. The zero value
// is unknown.
type NeedCurve struct {
	Base, RisePerHour, FallPerHour float64
	FreezeAsleep                   bool
}

// Valid reports a curve with both rates and a base level in 0 to 1.
func (c NeedCurve) Valid() bool {
	return finitePositive(c.RisePerHour) && finitePositive(c.FallPerHour) && c.Base >= 0 && c.Base <= 1
}

// SeekerNeed is one environment need: its curve and its game numbers. Both needs
// are the same model, so a third (impressiveness, cleanliness) plugs in here.
type SeekerNeed struct {
	Curve NeedCurve
	Game  SeekerGameDef
}

// EnvSegment is Hours of a pawn's day spent seeking Target (the instant level),
// asleep or awake.
type EnvSegment struct {
	Hours, Target float64
	Asleep        bool
}

// EnvRun is the steady day of a need: the hours spent in each thought stage,
// StageHours[s+1] for stage s and StageHours[0] for no stage, and the level the
// day starts from.
type EnvRun struct {
	StageHours []float64
	Start      float64
}

// envDaySteps is the need intervals in a day.
func envDaySteps() int {
	return int(math.Round(domain.TicksPerDay / seekerGame.IntervalTicks))
}

// envMaxDays bounds the steady-state search; a day's start level that has not
// settled by then leaves the run unknown. Rates are at least a few percent an
// hour, so a settled level takes days.
const envMaxDays = 200

// Run simulates the need over the day, repeated until it settles. False for an
// invalid curve or a day that is not 24 hours of segments.
func (n SeekerNeed) Run(day []EnvSegment) (EnvRun, bool) {
	steps := envDaySteps()
	if !n.Curve.Valid() {
		return EnvRun{}, false
	}
	total := 0.0
	for _, s := range day {
		if s.Hours < 0 || math.IsNaN(s.Hours) || math.IsNaN(s.Target) {
			return EnvRun{}, false
		}
		total += s.Hours
	}
	if math.Abs(total-24) > 1e-9 {
		return EnvRun{}, false
	}
	// The segment each interval falls in, by its midpoint.
	targets, asleep := make([]float64, steps), make([]bool, steps)
	hoursPerStep := float64(steps)
	at, k := 0.0, 0
	for i := range targets {
		mid := (float64(i) + 0.5) * 24 / hoursPerStep
		for k < len(day)-1 && at+day[k].Hours <= mid {
			at += day[k].Hours
			k++
		}
		targets[i], asleep[i] = min(max(day[k].Target, 0), 1), day[k].Asleep
	}
	level := n.Curve.Base
	for range envMaxDays {
		start := level
		hours := make([]float64, n.Game.Bands.maxStage()+2)
		for i := range targets {
			level = n.advance(level, targets[i], asleep[i])
			hours[n.Game.Bands.Stage(level)+1] += 24 / hoursPerStep
		}
		if math.Abs(level-start) < 1e-12 {
			return EnvRun{StageHours: hours, Start: start}, true
		}
	}
	return EnvRun{}, false
}

// advance is Need_Seeker.NeedInterval: one 150-tick interval toward the instant
// level, up at the rise rate and down at the fall rate, never past it, and not at
// all while the need is frozen asleep.
func (n SeekerNeed) advance(level, target float64, asleep bool) float64 {
	if n.Curve.FreezeAsleep && asleep {
		return level
	}
	perStep := seekerGame.IntervalTicks / domain.TicksPerHour
	if target > level {
		level = min(level+n.Curve.RisePerHour*perStep, target)
	}
	if target < level {
		level = max(level-n.Curve.FallPerHour*perStep, target)
	}
	return level
}

// MeanMood is the time-averaged mood the run's stages give, in points; false
// when the thought or a stage is unknown.
func (r EnvRun) MeanMood(m MoodModel, thought string) (float64, bool) {
	if len(m.Stages[thought]) == 0 {
		return 0, false
	}
	sum := 0.0
	for i, h := range r.StageHours {
		if h == 0 || i == 0 {
			continue
		}
		effect, _ := m.StageEffect(thought, i-1)
		sum += h * effect
	}
	return sum / 24, true
}

// EnvSpot is Hours a day awake at a spot whose instant level is Target.
type EnvSpot struct{ Hours, Target float64 }

// envDay lays a colonist's day out from the schedule template: the sleep block
// on the bed (sleepTarget), the spots in order over the Anything hours (scaled
// down together when they ask for more than the day has), the rest idle. It
// starts at waking, so the day is one cycle.
func envDay(sleepTarget float64, spots []EnvSpot) []EnvSegment {
	slots := scheduleTemplate(TraitEffects{})
	wake := 0
	for h := range slots {
		if slots[(h+23)%24] == ScheduleSleep && slots[h] != ScheduleSleep {
			wake = h
			break
		}
	}
	free := 0.0
	for _, s := range slots {
		if s == ScheduleAnything {
			free++
		}
	}
	asked := 0.0
	for _, s := range spots {
		asked += max(s.Hours, 0)
	}
	scale := 1.0
	if asked > free {
		scale = free / asked
	}
	var out []EnvSegment
	queue := slices.Clone(spots)
	for i := range queue {
		queue[i].Hours = max(queue[i].Hours, 0) * scale
	}
	for i := range 24 {
		switch slots[(wake+i)%24] {
		case ScheduleSleep:
			out = append(out, EnvSegment{Hours: 1, Target: sleepTarget, Asleep: true})
		case ScheduleAnything:
			room := 1.0
			for j := range queue {
				if room <= 0 {
					break
				}
				take := min(room, queue[j].Hours)
				if take <= 0 {
					continue
				}
				out = append(out, EnvSegment{Hours: take, Target: queue[j].Target})
				queue[j].Hours -= take
				room -= take
			}
			if room > 1e-12 {
				out = append(out, EnvSegment{Hours: room})
			}
		default:
			out = append(out, EnvSegment{Hours: 1})
		}
	}
	return out
}

// EnvPrice is what the price of a change in a need reads: the colony's mood
// model, a work-day in watts, the colonists the change reaches and their mean
// mood level without it (0 to 1).
type EnvPrice struct {
	Mood         MoodModel
	WorkDayW     float64
	Pawns        int
	ExpectedMood float64
}

// EnvChange is a change of the need's day priced as mood. Gain is the change of
// the time-averaged mood in points a colonist (negative when the change loses
// mood) and BenefitW what the mood is worth in sustained watts for all Pawns:
// the break risk it removes, a step function that is zero while the mood stays
// inside a break tier.
type EnvChange struct {
	Gain, BenefitW float64
}

// Price prices the alt day against the base day. False when any number is
// unknown: the curve, the thought's stages, the break model or the work-day.
func (n SeekerNeed) Price(base, alt []EnvSegment, in EnvPrice) (EnvChange, bool) {
	b, ok := n.Run(base)
	if !ok {
		return EnvChange{}, false
	}
	a, ok := n.Run(alt)
	if !ok {
		return EnvChange{}, false
	}
	mb, ok := b.MeanMood(in.Mood, n.Game.Thought)
	if !ok {
		return EnvChange{}, false
	}
	ma, ok := a.MeanMood(in.Mood, n.Game.Thought)
	if !ok {
		return EnvChange{}, false
	}
	price, ok := PriceMood(MoodPriceInput{Model: in.Mood.Break, WorkDayW: in.WorkDayW, Points: mb - ma, Pawns: in.Pawns, ExpectedMood: in.ExpectedMood})
	if !ok {
		return EnvChange{}, false
	}
	return EnvChange{Gain: ma - mb, BenefitW: -price.W}, true
}

// ComfortOption is one furniture piece a spot could hold: its def and stuff, the
// comfort stat the pawn using it gets at Normal quality and its cost, the market
// value of its adjusted costs. The zero option is no furniture.
type ComfortOption struct {
	Def, Stuff     string
	Comfort, Build float64
}

// ComfortModel is the catalog's furniture and needs for the comfort pricing; the
// zero value is unknown. Chairs are the buildable sittable defs at each stuff
// and Beds the single beds an adult sleeps in. BedOffset is the comfort the bed's
// linked facilities add (the end table and dresser every bedroom template
// places, so a sunk cost of every bed alike). Comfort and Beauty are the
// NeedDef curves.
type ComfortModel struct {
	Comfort, Beauty NeedCurve
	Chairs, Beds    []ComfortOption
	BedOffset       float64
}

// ComfortNeed is the comfort need over the model.
func (m ComfortModel) ComfortNeed() SeekerNeed {
	return SeekerNeed{Curve: m.Comfort, Game: seekerGame.Comfort}
}

// BeautyNeed is the beauty need over the model. Beauty is fed by the cells
// around a spot, not by furniture comfort; a caller with decor candidates prices
// them through SeekerNeed.Price with BeautyTarget, the same shape as a chair.
func (m ComfortModel) BeautyNeed() SeekerNeed {
	return SeekerNeed{Curve: m.Beauty, Game: seekerGame.Beauty}
}

// BeautyTarget is the instant level of beauty at a spot whose visible cells have
// the given mean beauty (Need_Beauty.LevelFromBeauty).
func (m ComfortModel) BeautyTarget(meanBeauty float64) float64 {
	return min(max(m.Beauty.Base+seekerGame.BeautyImpact*meanBeauty, 0), 1)
}

// undominated keeps the options no other option beats on both comfort and cost,
// cheapest first: a dearer piece with no more comfort never wins.
func undominated(options []ComfortOption) []ComfortOption {
	sorted := slices.Clone(options)
	slices.SortFunc(sorted, func(a, b ComfortOption) int {
		switch {
		case a.Build != b.Build:
			if a.Build < b.Build {
				return -1
			}
			return 1
		case a.Comfort != b.Comfort:
			if a.Comfort > b.Comfort {
				return -1
			}
			return 1
		case a.Def != b.Def:
			if a.Def < b.Def {
				return -1
			}
			return 1
		}
		switch {
		case a.Stuff < b.Stuff:
			return -1
		case a.Stuff > b.Stuff:
			return 1
		}
		return 0
	})
	var out []ComfortOption
	for _, o := range sorted {
		if len(out) == 0 || o.Comfort > out[len(out)-1].Comfort {
			out = append(out, o)
		}
	}
	return out
}

// ComfortChoice is the comfort tier advised for a station node. Option is the
// piece (zero for none); Priced is false when the choice could not be priced and
// is the current behaviour: the planner's own bed at a sleep node, no chair at a
// bench. Gain is the mood points a colonist gains over the current behaviour,
// BenefitW and CostW the mood and the pieces' cost in sustained watts for the
// node.
type ComfortChoice struct {
	Node     string
	Option   ComfortOption
	Priced   bool
	Gain     float64
	BenefitW float64
	CostW    float64
}

// ComfortInput is what the comfort choice reads. Baseline is the piece the
// planner would otherwise build for the bed; Pawns the colonists, who share the
// work; Steel is steel's market value for BuildW.
type ComfortInput struct {
	Model        ComfortModel
	Mood         MoodModel
	Graph        StationGraph
	Baseline     string
	Pawns        int
	WorkDayW     float64
	ExpectedMood float64
	Steel        float64
}
