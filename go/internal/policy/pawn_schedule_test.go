package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func hours(slots []string, def string) []int {
	var out []int
	for h, s := range slots {
		if s == def {
			out = append(out, h)
		}
	}
	return out
}

func TestScheduleTemplates(t *testing.T) {
	build := func(sleep []int, joy int) []string {
		slots := make([]string, 24)
		for h := range slots {
			slots[h] = ScheduleAnything
		}
		for _, h := range sleep {
			slots[h] = ScheduleSleep
		}
		slots[joy] = ScheduleJoy
		return slots
	}
	for name, c := range map[string]struct {
		effects TraitEffects
		want    []string
	}{
		"day":      {TraitEffects{}, build([]int{22, 23, 0, 1, 2, 3, 4, 5}, 21)},
		"quick":    {TraitEffects{QuickSleeper: true}, build([]int{0, 1, 2, 3, 4, 5}, 23)},
		"owl":      {TraitEffects{NightShift: true}, build([]int{10, 11, 12, 13, 14, 15, 16, 17}, 9)},
		"quickOwl": {TraitEffects{NightShift: true, QuickSleeper: true}, build([]int{11, 12, 13, 14, 15, 16}, 10)},
	} {
		if got := scheduleTemplate(c.effects); !sameSchedule(got, c.want) {
			t.Fatalf("%s: %v", name, got)
		}
	}
	if !sameSchedule(nativeDefaultSchedule(), []string{"Sleep", "Sleep", "Sleep", "Sleep", "Sleep", "Sleep", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Anything", "Sleep", "Sleep"}) {
		t.Fatal(nativeDefaultSchedule())
	}
}

func TestPlanSchedulesStaggerJoy(t *testing.T) {
	var pawns []WorkPawn
	var people []PawnID
	for _, id := range []PawnID{"e", "c", "a", "d", "b"} {
		p := testWorkPawn(id, true, false, nil)
		p.Schedule = domain.Known(nativeDefaultSchedule())
		pawns = append(pawns, p)
		people = append(people, id)
	}
	joy := func(d ScheduleDecision) map[PawnID]int {
		out := map[PawnID]int{}
		for _, row := range d.Schedules {
			if h := hours(row.Slots, ScheduleJoy); len(h) == 1 && len(hours(row.Slots, ScheduleSleep)) == 8 && row.Slots[22] == ScheduleSleep && row.Slots[5] == ScheduleSleep {
				out[row.Pawn] = h[0]
			}
		}
		return out
	}
	census := func(places int) domain.Fact[ComfortObservation] {
		return domain.Known(ComfortObservation{People: people, Recreation: make([]ComfortFacility, places)})
	}
	for name, comfort := range map[string]domain.Fact[ComfortObservation]{"enough": census(5), "unknown": domain.Unknown[ComfortObservation](), "none": census(0)} {
		got := joy(PlanSchedules(pawns, comfort, false))
		for _, id := range people {
			if got[id] != 21 {
				t.Fatalf("%s: %v", name, got)
			}
		}
	}
	// Two places for five people: two per hour back from sleep, by ID.
	want := map[PawnID]int{"a": 21, "b": 21, "c": 20, "d": 20, "e": 19}
	for range 3 {
		got := joy(PlanSchedules(pawns, census(2), false))
		for _, id := range people {
			if got[id] != want[id] {
				t.Fatalf("staggered: %v", got)
			}
		}
	}
}

func TestPlanSchedules(t *testing.T) {
	owl := testWorkPawn("owl", true, false, nil, testTrait("NightOwl", 0))
	owl.Schedule = domain.Known(nativeDefaultSchedule())
	plain := testWorkPawn("plain", true, false, nil)
	plain.Schedule = domain.Known(scheduleTemplate(TraitEffects{}))
	edited := testWorkPawn("edited", true, false, nil)
	custom := nativeDefaultSchedule()
	custom[12] = ScheduleJoy
	edited.Schedule = domain.Known(custom)
	unknown := testWorkPawn("unknown", true, false, nil)
	away := testWorkPawn("away", true, false, nil)
	away.Available = domain.Known(false)
	away.Schedule = domain.Known(nativeDefaultSchedule())
	d := PlanSchedules([]WorkPawn{plain, owl, edited, unknown, away}, domain.Unknown[ComfortObservation](), false)
	if len(d.Schedules) != 3 || d.Schedules[1].Pawn != "owl" || d.Schedules[1].Matches || !sameSchedule(d.Schedules[1].Slots, scheduleTemplate(TraitEffects{NightShift: true})) {
		t.Fatal(d)
	}
	if d.Schedules[2].Pawn != "plain" || !d.Schedules[2].Matches {
		t.Fatal(d)
	}
	// A timetable edited by hand (under Manual) is replanned like any other
	// (#461): provenance is not authority over fresh planning.
	if d.Schedules[0].Pawn != "edited" || d.Schedules[0].Matches || !sameSchedule(d.Schedules[0].Slots, scheduleTemplate(TraitEffects{})) {
		t.Fatal(d.Schedules[0])
	}
	// A planner-written timetable is rewritten when the profile changes
	// (the owl read back its own night shift, then loses the trait).
	owl.Schedule = domain.Known(scheduleTemplate(TraitEffects{NightShift: true}))
	owl.Traits = domain.Known([]PawnTrait{})
	d = PlanSchedules([]WorkPawn{owl}, domain.Unknown[ComfortObservation](), false)
	if len(d.Schedules) != 1 || d.Schedules[0].Matches || !sameSchedule(d.Schedules[0].Slots, scheduleTemplate(TraitEffects{})) {
		t.Fatal(d)
	}
}

func TestPlanSchedulesNeedBands(t *testing.T) {
	day, owl := TraitEffects{}, TraitEffects{NightShift: true}
	with := func(effects TraitEffects, sleep []int, joy []int) []string {
		slots := scheduleTemplate(effects)
		for _, h := range sleep {
			slots[h] = ScheduleSleep
		}
		for _, h := range joy {
			slots[h] = ScheduleJoy
		}
		return slots
	}
	dayBase, dayLong, dayWide := with(day, nil, nil), with(day, []int{6, 7}, nil), with(day, nil, []int{20})
	owlBase, owlLong, owlWide := with(owl, nil, nil), with(owl, []int{18, 19}, nil), with(owl, nil, []int{8})
	unknown := domain.Unknown[float64]()
	for name, c := range map[string]struct {
		owl       bool
		rest, joy domain.Fact[float64]
		current   []string
		want      []string
	}{
		"drowsy extends":        {false, domain.Known(0.2), domain.Known(0.9), dayBase, dayLong},
		"band holds base":       {false, domain.Known(0.4), domain.Known(0.5), dayBase, dayBase},
		"band holds extended":   {false, domain.Known(0.4), domain.Known(0.9), dayLong, dayLong},
		"rested reverts":        {false, domain.Known(0.6), domain.Known(0.9), dayLong, dayBase},
		"bored widens":          {false, domain.Known(0.9), domain.Known(0.1), dayBase, dayWide},
		"band holds wide":       {false, domain.Known(0.9), domain.Known(0.5), dayWide, dayWide},
		"entertained reverts":   {false, domain.Known(0.9), domain.Known(0.7), dayWide, dayBase},
		"unknown is base":       {false, unknown, unknown, with(day, []int{6, 7}, []int{20}), dayBase},
		"both":                  {false, domain.Known(0.1), domain.Known(0.1), dayBase, with(day, []int{6, 7}, []int{20})},
		"owl drowsy extends":    {true, domain.Known(0.2), domain.Known(0.9), owlBase, owlLong},
		"owl band holds":        {true, domain.Known(0.5), domain.Known(0.9), owlLong, owlLong},
		"owl rested reverts":    {true, domain.Known(0.8), domain.Known(0.9), owlLong, owlBase},
		"owl bored widens":      {true, domain.Known(0.9), domain.Known(0.2), owlBase, owlWide},
		"owl from native table": {true, domain.Known(0.2), domain.Known(0.2), nativeDefaultSchedule(), with(owl, []int{18, 19}, []int{8})},
	} {
		var traits []PawnTrait
		if c.owl {
			traits = []PawnTrait{testTrait("NightOwl", 0)}
		}
		pawn := testWorkPawn("p", true, false, nil, traits...)
		pawn.Schedule, pawn.Rest, pawn.Joy = domain.Known(c.current), c.rest, c.joy
		d := PlanSchedules([]WorkPawn{pawn}, domain.Unknown[ComfortObservation](), false)
		if len(d.Schedules) != 1 || !sameSchedule(d.Schedules[0].Slots, c.want) || d.Schedules[0].Matches != sameSchedule(c.current, c.want) {
			t.Fatalf("%s: %v", name, d)
		}
		for _, s := range d.Schedules[0].Slots {
			if s == "Work" {
				t.Fatalf("%s wrote Work", name)
			}
		}
	}
}

func TestPlanSchedulesMeditate(t *testing.T) {
	with := func(block string, hrs ...int) []string {
		slots := scheduleTemplate(TraitEffects{})
		slots[21] = ScheduleAnything
		for _, h := range hrs {
			slots[h] = block
		}
		return slots
	}
	caster := func(focus float64, level int) WorkPawn {
		p := testWorkPawn("p", true, false, nil)
		p.Joy = domain.Known(0.9)
		p.Psyfocus, p.PsyfocusTarget, p.PsylinkLevel = domain.Known(focus), domain.Known(0.5), domain.Known(level)
		return p
	}
	plain := testWorkPawn("p", true, false, nil)
	plain.Joy = domain.Known(0.9)
	for name, c := range map[string]struct {
		pawn    WorkPawn
		med     bool
		current []string
		want    []string
	}{
		"caster meditates":     {caster(0.9, 2), true, nativeDefaultSchedule(), with(ScheduleMeditate, 21)},
		"no def keeps joy":     {caster(0.9, 2), false, nativeDefaultSchedule(), with(ScheduleJoy, 21)},
		"no psylink keeps joy": {plain, true, nativeDefaultSchedule(), with(ScheduleJoy, 21)},
		"low focus widens":     {caster(0.3, 1), true, nativeDefaultSchedule(), with(ScheduleMeditate, 20, 21)},
		"band holds wide":      {caster(0.55, 1), true, with(ScheduleMeditate, 20, 21), with(ScheduleMeditate, 20, 21)},
		"focused reverts":      {caster(0.6, 1), true, with(ScheduleMeditate, 20, 21), with(ScheduleMeditate, 21)},
		"wide joy stays wide":  {caster(0.55, 1), true, with(ScheduleJoy, 20, 21), with(ScheduleMeditate, 20, 21)},
	} {
		c.pawn.Schedule = domain.Known(c.current)
		d := PlanSchedules([]WorkPawn{c.pawn}, domain.Unknown[ComfortObservation](), c.med)
		if len(d.Schedules) != 1 || !sameSchedule(d.Schedules[0].Slots, c.want) {
			t.Fatalf("%s: %v", name, d)
		}
		if !c.med && len(hours(d.Schedules[0].Slots, ScheduleMeditate)) > 0 {
			t.Fatalf("%s wrote Meditate without the def", name)
		}
	}
	// Staggering moves the Meditate slot like Joy (#1317).
	a, b := caster(0.3, 1), caster(0.9, 1)
	a.ID, b.ID = "a", "b"
	a.Schedule, b.Schedule = domain.Known(nativeDefaultSchedule()), domain.Known(nativeDefaultSchedule())
	comfort := domain.Known(ComfortObservation{People: []PawnID{"a", "b"}, Recreation: make([]ComfortFacility, 1)})
	d := PlanSchedules([]WorkPawn{a, b}, comfort, true)
	if got := hours(d.Schedules[1].Slots, ScheduleMeditate); len(got) != 1 || got[0] != 20 {
		t.Fatal(d)
	}
	if got := hours(d.Schedules[0].Slots, ScheduleMeditate); len(got) != 2 || got[0] != 20 {
		t.Fatal(d)
	}
}
