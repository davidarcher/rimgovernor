package policy

import (
	"math"
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The recorded NeedDef rows: comfort rises 0.60 an hour and falls 0.04, beauty
// starts at 0.4, rises 0.32, falls 0.08 and freezes asleep.
func comfortTestModel() ComfortModel {
	return ComfortModel{
		Comfort: NeedCurve{RisePerHour: 0.6, FallPerHour: 0.04},
		Beauty:  NeedCurve{Base: 0.4, RisePerHour: 0.32, FallPerHour: 0.08, FreezeAsleep: true},
		Beds: []ComfortOption{
			{Def: "Bed", Stuff: "Wood", Comfort: 0.5, Build: 40},
			{Def: "Bed", Stuff: "Steel", Comfort: 0.7, Build: 120},
		},
		Chairs: []ComfortOption{
			{Def: "Stool", Stuff: "Wood", Comfort: 0.5, Build: 10},
			{Def: "Chair", Stuff: "Wood", Comfort: 0.7, Build: 30},
			{Def: "Armchair", Stuff: "Cloth", Comfort: 0.9, Build: 80},
			{Def: "Pew", Stuff: "Wood", Comfort: 0.6, Build: 400}, // dearer than the armchair and less comfortable: never wins
		},
	}
}

// The recorded thought stages: NeedComfort -3 / +4 / +6 / +8 / +10 and
// NeedBeauty -15 / -10 / -5 / +5 / +10 / +15.
func comfortTestMood() MoodModel {
	stages := func(effects ...float64) []MoodStage {
		out := make([]MoodStage, len(effects))
		for i, e := range effects {
			out[i] = MoodStage{Effect: e, Present: true}
		}
		return out
	}
	return MoodModel{Break: testMoodBreaks(), Stages: map[string][]MoodStage{
		"NeedComfort": stages(-3, 4, 6, 8, 10),
		"NeedBeauty":  stages(-15, -10, -5, 5, 10, 15),
	}}
}

// hoursUntil is the hours the need takes from start, seeking target, to change
// stage, counted in the game's 150-tick intervals.
func hoursUntil(n SeekerNeed, start, target float64) float64 {
	from := n.Game.Bands.Stage(start)
	level, steps := start, 0
	for n.Game.Bands.Stage(level) == from && steps < 100000 {
		level = n.advance(level, target, false)
		steps++
	}
	return float64(steps) * seekerGame.IntervalTicks / domain.TicksPerHour
}

// TestComfortNeedMatchesTheWikiTables: the need rises 60% an hour while used
// (3.6% per 150 ticks) and falls 4% an hour otherwise, so each stage of the
// thought is reached and left on the wiki's schedule, to one interval.
func TestComfortNeedMatchesTheWikiTables(t *testing.T) {
	n := comfortTestModel().ComfortNeed()
	if got := n.Curve.RisePerHour * seekerGame.IntervalTicks / domain.TicksPerHour; math.Abs(got-0.036) > 1e-12 {
		t.Fatalf("rise per interval = %v, want 0.036", got)
	}
	interval := seekerGame.IntervalTicks / domain.TicksPerHour
	// Reaching: from level 0 sitting on a stat-1.0 chair, the stage changes at
	// each threshold over 0.6 an hour.
	level, hours := 0.0, 0.0
	for _, want := range []struct {
		stage int
		at    float64
	}{{-1, 0.1}, {1, 0.6}, {2, 0.7}, {3, 0.8}, {4, 0.9}} {
		for n.Game.Bands.Stage(level) != want.stage {
			level = n.advance(level, 1, false)
			hours += interval
		}
		if wantH := want.at / 0.6; hours < wantH-1e-9 || hours > wantH+interval+1e-9 {
			t.Errorf("stage %d reached after %.3f h, want %.3f", want.stage, hours, wantH)
		}
	}
	// Leaving: from 1.0 unused, each stage ends after (level - threshold) / 0.04 hours.
	level, hours = 1, 0
	for _, want := range []struct {
		stage int
		at    float64
	}{{3, 0.9}, {2, 0.8}, {1, 0.7}, {-1, 0.6}, {0, 0.1}} {
		for n.Game.Bands.Stage(level) != want.stage {
			level = n.advance(level, 0, false)
			hours += interval
		}
		if wantH := (1 - want.at) / 0.04; hours < wantH-1e-9 || hours > wantH+interval+1e-9 {
			t.Errorf("stage %d reached after %.3f h, want %.3f", want.stage, hours, wantH)
		}
	}
	if got := hoursUntil(n, 0.65, 0.9); got < 0.05/0.6-1e-9 {
		t.Errorf("0.65 to 0.7 took %v h", got)
	}
}

func TestSeekerBandsAreTheGamesCategories(t *testing.T) {
	comfort, beauty := seekerGame.Comfort.Bands, seekerGame.Beauty.Bands
	for level, want := range map[float64]int{0: 0, 0.0999: 0, 0.1: -1, 0.5999: -1, 0.6: 1, 0.6999: 1, 0.7: 2, 0.8: 3, 0.8999: 3, 0.9: 4, 1: 4} {
		if got := comfort.Stage(level); got != want {
			t.Errorf("comfort %v = stage %d, want %d", level, got, want)
		}
	}
	// Beauty's categories are strict greater-than: the edge belongs below.
	for level, want := range map[float64]int{0: 0, 0.01: 0, 0.0101: 1, 0.15: 1, 0.1501: 2, 0.35: 2, 0.3501: -1, 0.65: -1, 0.6501: 3, 0.85: 3, 0.8501: 4, 0.99: 4, 0.9901: 5, 1: 5} {
		if got := beauty.Stage(level); got != want {
			t.Errorf("beauty %v = stage %d, want %d", level, got, want)
		}
	}
}

// TestBeautyIsTheSameShape: the second term of the environment need. Its level
// starts at the base 0.4 (neutral), rises 0.32 an hour toward 0.4 + 0.1 of the
// mean beauty, and freezes while asleep.
func TestBeautyIsTheSameShape(t *testing.T) {
	m := comfortTestModel()
	n := m.BeautyNeed()
	if got := m.BeautyTarget(5); math.Abs(got-0.9) > 1e-12 {
		t.Errorf("beauty target of mean 5 = %v, want 0.9", got)
	}
	if got := m.BeautyTarget(-9); got != 0 {
		t.Errorf("clamped target = %v, want 0", got)
	}
	if got := n.advance(0.4, 1, true); got != 0.4 {
		t.Errorf("asleep beauty moved to %v", got)
	}
	if got := m.ComfortNeed().advance(0.4, 1, true); got <= 0.4 {
		t.Errorf("comfort froze asleep: %v", got)
	}
	// A pretty room (mean 3, target 0.7) all day is "pretty" the whole day: the
	// level climbs from 0.4 to 0.65 in 0.78 hours and past it after.
	room := []EnvSegment{{Hours: 24, Target: m.BeautyTarget(3)}}
	run, ok := n.Run(room)
	if !ok {
		t.Fatal("beauty run unknown")
	}
	if got := run.StageHours[3+1]; math.Abs(got-24) > 1e-9 {
		t.Errorf("hours at the pretty stage = %v, want 24 (steady state)", got)
	}
	if mood, ok := run.MeanMood(comfortTestMood(), "NeedBeauty"); !ok || math.Abs(mood-5) > 1e-9 {
		t.Errorf("mean mood = %v %v, want +5", mood, ok)
	}
	// An ugly room (mean -2, target 0.2) costs: level 0.2 is unsightly (-5).
	run, _ = n.Run([]EnvSegment{{Hours: 24, Target: m.BeautyTarget(-2)}})
	if mood, _ := run.MeanMood(comfortTestMood(), "NeedBeauty"); math.Abs(mood+5) > 1e-9 {
		t.Errorf("ugly room mood = %v, want -5", mood)
	}
}

func TestEnvRunNeedsAFullDayAndAKnownCurve(t *testing.T) {
	n := comfortTestModel().ComfortNeed()
	if _, ok := n.Run([]EnvSegment{{Hours: 23}}); ok {
		t.Error("a 23 hour day ran")
	}
	if _, ok := (SeekerNeed{Game: seekerGame.Comfort}).Run([]EnvSegment{{Hours: 24}}); ok {
		t.Error("an unknown curve ran")
	}
	if _, ok := n.Run([]EnvSegment{{Hours: 24, Target: math.NaN()}}); ok {
		t.Error("a NaN target ran")
	}
	if _, ok := (EnvRun{StageHours: []float64{0, 24}}).MeanMood(MoodModel{}, "NeedComfort"); ok {
		t.Error("an unknown thought priced")
	}
}

// TestEnvDayFollowsTheSchedule: the sleep block (22h to 5h, 8 hours) is on the
// bed, the Anything hours are spent at the spots in order, and a day asking for
// more than it has is scaled down together.
func TestEnvDayFollowsTheSchedule(t *testing.T) {
	hours := func(day []EnvSegment, match func(EnvSegment) bool) float64 {
		sum := 0.0
		for _, s := range day {
			if match(s) {
				sum += s.Hours
			}
		}
		return sum
	}
	day := envDay(0.7, []EnvSpot{{Hours: 3, Target: 0.9}, {Hours: 1.5, Target: 0.5}})
	if got := hours(day, func(EnvSegment) bool { return true }); math.Abs(got-24) > 1e-9 {
		t.Fatalf("day is %v hours", got)
	}
	if got := hours(day, func(s EnvSegment) bool { return s.Asleep }); got != 8 {
		t.Errorf("sleeps %v hours, want the template's 8", got)
	}
	if got := hours(day, func(s EnvSegment) bool { return s.Target == 0.9 }); math.Abs(got-3) > 1e-9 {
		t.Errorf("3 h spot = %v", got)
	}
	if got := hours(day, func(s EnvSegment) bool { return s.Target == 0.5 }); math.Abs(got-1.5) > 1e-9 {
		t.Errorf("1.5 h spot = %v", got)
	}
	if day[0].Asleep {
		t.Error("the day starts at waking")
	}
	// 40 hours asked of 15: scaled to fit, the spots keep their ratio.
	crowded := envDay(0.7, []EnvSpot{{Hours: 20, Target: 0.9}, {Hours: 20, Target: 0.5}})
	a := hours(crowded, func(s EnvSegment) bool { return s.Target == 0.9 })
	b := hours(crowded, func(s EnvSegment) bool { return s.Target == 0.5 })
	if math.Abs(a-b) > 1e-9 || math.Abs(a+b-15) > 1e-9 {
		t.Errorf("scaled spots %v and %v, want 7.5 each", a, b)
	}
}

// comfortGraph is a sleep node of beds and one worked bench: the bench is worked
// `hours` a colonist-day by `pawns` colonists.
func comfortGraph(pawns int, hours float64) StationGraph {
	return StationGraph{Nodes: []StationNode{
		{ID: "Bed/sleep", Def: "Bed", Purpose: PurposeSleep, Count: pawns + 1},
		{ID: "Bench/work", Def: "Bench", Purpose: PurposeWork, Count: 1, DailyWork: hours * domain.TicksPerHour * float64(pawns)},
	}}
}

func comfortInput(pawns int, hours, mood float64) ComfortInput {
	return ComfortInput{
		Model: comfortTestModel(), Mood: comfortTestMood(), Graph: comfortGraph(pawns, hours),
		Baseline: "Bed", Pawns: pawns, WorkDayW: 1000, ExpectedMood: mood, Steel: 1.9,
	}
}

func choiceOf(t *testing.T, choices []ComfortChoice, node string) ComfortChoice {
	t.Helper()
	for _, c := range choices {
		if c.Node == node {
			return c
		}
	}
	t.Fatalf("no choice for %s in %+v", node, choices)
	return ComfortChoice{}
}

// TestLongDwellBenchGetsTheBetterChair: a bench worked most of the day lifts the
// colonists a mood stage with an armchair, which at a mood just under the break
// threshold takes them out of the minor tier; the same bench worked an hour a
// day never crosses a stage and gets none. Nothing is vetoed: a chair too dear
// for its mood stays the choice's cost in watts, not a refusal.
func TestLongDwellBenchGetsTheBetterChair(t *testing.T) {
	long := ChooseComfort(comfortInput(4, 9, 0.34))
	bench := choiceOf(t, long, "Bench/work")
	if !bench.Priced || bench.Option.Def == "" || !(bench.Gain > 0) || !(bench.BenefitW > bench.CostW) {
		t.Fatalf("long dwell bench = %+v, want a chair", bench)
	}
	short := choiceOf(t, ChooseComfort(comfortInput(4, 0.2, 0.34)), "Bench/work")
	if !short.Priced || short.Option.Def != "" || short.BenefitW != 0 {
		t.Fatalf("short dwell bench = %+v, want no chair", short)
	}
}

// TestBedHoldingTheLevelMakesAWorkChairWorthLittle: a steel bed holds the level
// at 0.7 all night; a chair of 0.7 adds nothing the bed does not keep, while
// over a wooden bed (0.5, below every stage) the same chair is worth the stage
// it buys.
func TestBedHoldingTheLevelMakesAWorkChairWorthLittle(t *testing.T) {
	over := func(bed string) ComfortChoice {
		in := comfortInput(4, 9, 0.34)
		in.Baseline = bed
		in.Model.Beds = slices.DeleteFunc(slices.Clone(in.Model.Beds), func(o ComfortOption) bool { return o.Stuff != bed })
		in.Baseline = "Bed"
		return choiceOf(t, ChooseComfort(in), "Bench/work")
	}
	wood, steel := over("Wood"), over("Steel")
	if wood.Option.Def == "" || !(wood.BenefitW > 0) {
		t.Fatalf("over a wooden bed the chair should win: %+v", wood)
	}
	if !(steel.Gain < wood.Gain) {
		t.Errorf("a bed that holds the level should make the chair worth less: steel-bed gain %v, wood-bed %v", steel.Gain, wood.Gain)
	}
}

// TestComfortBelowTheFirstThresholdIsNearlyWorthless: a chair of 0.5 never lifts
// the level out of the no-stage band, so it adds no mood however long it is used.
func TestComfortBelowTheFirstThresholdIsNearlyWorthless(t *testing.T) {
	in := comfortInput(4, 12, 0.34)
	in.Model.Chairs = []ComfortOption{{Def: "Stool", Stuff: "Wood", Comfort: 0.5, Build: 10}}
	bench := choiceOf(t, ChooseComfort(in), "Bench/work")
	if bench.Option.Def != "" {
		t.Errorf("a 0.5 chair was chosen: %+v", bench)
	}
}

// TestUnknownLeavesTheChoiceUnpriced: an unknown comfort curve, mood model, work
// day, steel value or colony mood leaves every node at the current behaviour,
// the planner's bed and no chair, not at a guess.
func TestUnknownLeavesTheChoiceUnpriced(t *testing.T) {
	cases := map[string]func(*ComfortInput){
		"curve":    func(in *ComfortInput) { in.Model.Comfort = NeedCurve{} },
		"stages":   func(in *ComfortInput) { in.Mood.Stages = nil },
		"workday":  func(in *ComfortInput) { in.WorkDayW = 0 },
		"steel":    func(in *ComfortInput) { in.Steel = 0 },
		"mood":     func(in *ComfortInput) { in.ExpectedMood = 1.5 },
		"colonist": func(in *ComfortInput) { in.Pawns = 0 },
		"nobed":    func(in *ComfortInput) { in.Baseline = "Cot" },
	}
	for name, mutate := range cases {
		in := comfortInput(4, 9, 0.34)
		mutate(&in)
		for _, c := range ChooseComfort(in) {
			if c.Priced || c.Gain != 0 || c.BenefitW != 0 {
				t.Errorf("%s: %s priced anyway: %+v", name, c.Node, c)
			}
			if c.Node == "Bench/work" && c.Option.Def != "" {
				t.Errorf("%s: unpriced bench has a chair %+v", name, c.Option)
			}
		}
	}
}

// TestBedChoiceIsPricedBothWays: a dearer bed wins only where its mood pays and
// a cheaper one stands in only where the mood it loses costs less than it saves.
func TestBedChoiceIsPricedBothWays(t *testing.T) {
	// At 0.34 mood a bed that lifts the level a stage crosses the break threshold.
	dearIn := comfortInput(4, 0, 0.34)
	dearIn.WorkDayW = 5000 // a work-day worth enough that the break risk outweighs five beds
	dear := choiceOf(t, ChooseComfort(dearIn), "Bed/sleep")
	if !dear.Priced || dear.Option.Stuff != "Steel" {
		t.Errorf("sleeping at 0.34 mood: %+v, want the steel bed", dear)
	}
	// At a mood far above every threshold no stage pays: the planner's own bed.
	calm := choiceOf(t, ChooseComfort(comfortInput(4, 0, 0.9)), "Bed/sleep")
	if !calm.Priced || calm.Option.Stuff != "Steel" && calm.Option.Stuff != "Wood" {
		t.Errorf("calm colony bed: %+v", calm)
	}
	if calm.BenefitW != 0 && calm.Option.Stuff == "Steel" {
		t.Errorf("a calm colony pays for comfort: %+v", calm)
	}
}

func TestComfortChoiceIsDeterministic(t *testing.T) {
	a := ChooseComfort(comfortInput(5, 6, 0.34))
	for range 3 {
		if b := ChooseComfort(comfortInput(5, 6, 0.34)); !reflect.DeepEqual(a, b) {
			t.Fatalf("choices differ:\n%+v\n%+v", a, b)
		}
	}
}

func TestUndominatedKeepsOnlyWhatBuysComfort(t *testing.T) {
	got := undominated([]ComfortOption{
		{Def: "A", Comfort: 0.5, Build: 10}, {Def: "B", Comfort: 0.4, Build: 20}, {Def: "C", Comfort: 0.8, Build: 30}, {Def: "D", Comfort: 0.8, Build: 40},
	})
	if len(got) != 2 || got[0].Def != "A" || got[1].Def != "C" {
		t.Errorf("undominated = %+v", got)
	}
}

// TestAdviseStationsCarriesTheComfortTiers: the chain adds the comfort tiers
// only while the colony's mood and the mood model are known, and leaves a node
// unpriced while its other numbers (here a work-day) are unknown.
func TestAdviseStationsCarriesTheComfortTiers(t *testing.T) {
	f := stationFactsFixture()
	advice, err := AdviseStations(f)
	if err != nil {
		t.Fatal(err)
	}
	if advice.Comfort != nil {
		t.Errorf("comfort advised without a mood: %+v", advice.Comfort)
	}
	f.ExpectedMood = domain.Known(0.4)
	f.Catalog.Mood = comfortTestMood()
	f.Catalog.Comfort = comfortTestModel()
	advice, err = AdviseStations(f)
	if err != nil {
		t.Fatal(err)
	}
	if len(advice.Comfort) == 0 {
		t.Fatal("no comfort rows")
	}
	for _, c := range advice.Comfort {
		if c.Priced {
			t.Errorf("%s priced with no crop to price a work-day by: %+v", c.Node, c)
		}
	}
}
