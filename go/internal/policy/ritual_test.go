package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const ritualDay = 60000

// ritualFixture is an ideoligion with a Sermon (cooldown 5 days, free start,
// led by the moral guide, a required unbound reader slot) held as Precept_3.
func ritualFixture(edit func(*Ideoligion)) domain.Fact[Ideoligion] {
	ideo := Ideoligion{
		Defs: IdeologyDefs{Rituals: map[string]RitualDef{
			"Sermon": {Name: "Sermon", IntervalDaysMin: 5, CanStartAnytime: true, RequiredBuildings: []string{"Lectern"},
				Roles: []RitualRoleSlot{
					{ID: "preacher", Precept: "IdeoRole_Moral", MaxCount: 1, Required: true},
					{ID: "reader", MaxCount: 1, Required: true},
					{ID: "extra", MaxCount: 2},
				}},
		}},
		Facts: IdeoligionFacts{IdeoID: "Ideo_1",
			Roles:   []HeldRole{{ID: "Precept_2", Def: "IdeoRole_Moral", Active: true, Pawns: []domain.PawnID{"guide"}}},
			Rituals: []HeldRitual{{ID: "Precept_3", Def: "Ritual_Sermon", Pattern: "Sermon", LastFinishedTick: 0}}},
	}
	if edit != nil {
		edit(&ideo)
	}
	return domain.Known(ideo)
}

func ritualPawns() domain.Fact[[]WorkPawn] {
	return domain.Known([]WorkPawn{
		rolePawn("guide", "Ideo_1", "IdeoRole_Moral", 0.9),
		rolePawn("alice", "Ideo_1", "", 0.5),
		rolePawn("bob", "Ideo_1", "", 0.8),
		rolePawn("carol", "Ideo_1", "", 0.8),
		rolePawn("outsider", "Ideo_2", "", 1),
	})
}

func lecternSites() domain.Fact[[]RitualSite] {
	return domain.Known([]RitualSite{{ID: "Thing_9", Def: "Lectern", Cell: domain.Cell{X: 20, Z: 21}}, {ID: "Thing_2", Def: "Lectern", Cell: domain.Cell{X: 10, Z: 11}}, {ID: "Thing_5", Def: "Altar", Cell: domain.Cell{X: 1, Z: 1}}})
}

func plansAt(t *testing.T, ideo domain.Fact[Ideoligion], sites domain.Fact[[]RitualSite], tick domain.Tick, calm bool) []RitualPlan {
	t.Helper()
	got, ok := PlanRituals(ideo, ritualPawns(), sites, tick, domain.Known(calm)).Value()
	if !ok {
		t.Fatal("plans unknown")
	}
	return got
}

// A free-start ritual is due once the pattern's own cooldown has passed since
// it last finished; the interval is the catalog's, not a constant.
func TestRitualsScheduleOnTheCatalogCadence(t *testing.T) {
	ideo := ritualFixture(func(i *Ideoligion) { i.Facts.Rituals[0].LastFinishedTick = 100 })
	if got := plansAt(t, ideo, lecternSites(), 100+5*ritualDay-1, true); len(got) != 0 {
		t.Fatalf("inside the cooldown: %+v", got)
	}
	got := plansAt(t, ideo, lecternSites(), 100+5*ritualDay, true)
	want := RitualPlan{Ritual: "Precept_3", Def: "Sermon", Sites: []domain.Cell{{X: 10, Z: 11}, {X: 20, Z: 21}}, Organizer: "guide",
		Slots:      []RitualSlotFill{{Slot: "preacher", Pawns: []PawnID{"guide"}}, {Slot: "reader", Pawns: []PawnID{"bob"}}},
		Spectators: []PawnID{"carol", "alice"}}
	if len(got) != 1 || !reflect.DeepEqual(got[0], want) {
		t.Fatalf("got %+v\nwant %+v", got, want)
	}
	slower := ritualFixture(func(i *Ideoligion) {
		i.Facts.Rituals[0].LastFinishedTick = 100
		d := i.Defs.Rituals["Sermon"]
		d.IntervalDaysMin = 8
		i.Defs.Rituals["Sermon"] = d
	})
	if got := plansAt(t, slower, lecternSites(), 100+5*ritualDay, true); len(got) != 0 {
		t.Fatalf("a longer cooldown in the def holds the ritual: %+v", got)
	}
}

func TestRitualsObligationIsDueRegardlessOfCooldownAndRepeatPenaltyHoldsFreeStart(t *testing.T) {
	obligation := ritualFixture(func(i *Ideoligion) {
		i.Facts.Rituals[0].LastFinishedTick = 50
		i.Facts.Rituals[0].ActiveObligations = 1
		i.Facts.Rituals[0].RepeatPenaltyActive = true
	})
	if got := plansAt(t, obligation, lecternSites(), 60, true); len(got) != 1 {
		t.Fatalf("an obligation is due: %+v", got)
	}
	penalty := ritualFixture(func(i *Ideoligion) { i.Facts.Rituals[0].RepeatPenaltyActive = true })
	if got := plansAt(t, penalty, lecternSites(), 100*ritualDay, true); len(got) != 0 {
		t.Fatalf("the repeat penalty holds a free start: %+v", got)
	}
	noFreeStart := ritualFixture(func(i *Ideoligion) {
		d := i.Defs.Rituals["Sermon"]
		d.CanStartAnytime = false
		i.Defs.Rituals["Sermon"] = d
	})
	if got := plansAt(t, noFreeStart, lecternSites(), 100*ritualDay, true); len(got) != 0 {
		t.Fatalf("a pattern with no free start waits for its obligation: %+v", got)
	}
	running := ritualFixture(func(i *Ideoligion) {
		i.Facts.Rituals[0].ActiveObligations = 1
		i.Facts.Rituals[0].Running = true
	})
	if got := plansAt(t, running, lecternSites(), 100*ritualDay, true); len(got) != 0 {
		t.Fatalf("a running ritual is not begun again: %+v", got)
	}
}

// An incident holds the ritual: nothing is planned, so nobody is held off
// Sleep either; an unknown reading plans nothing and stays unknown.
func TestRitualsHoldDuringIncidents(t *testing.T) {
	ideo := ritualFixture(nil)
	if got := plansAt(t, ideo, lecternSites(), 100*ritualDay, false); len(got) != 0 {
		t.Fatalf("a fight holds the ritual: %+v", got)
	}
	if owed, known := RitualsOwed(PlanRituals(ideo, ritualPawns(), lecternSites(), 100*ritualDay, domain.Known(false))).Value(); !known || owed {
		t.Fatal("a held ritual owes nothing")
	}
	if _, known := PlanRituals(ideo, ritualPawns(), lecternSites(), 100*ritualDay, domain.Unknown[bool]()).Value(); known {
		t.Fatal("an unread emergency census is unknown")
	}
	if got := RitualCalm(domain.Known(int64(0)), domain.Known(int64(1))); got != domain.Known(false) {
		t.Fatalf("a critical patient is not calm: %v", got)
	}
	if got := RitualCalm(domain.Known(int64(2)), domain.Known(int64(0))); got != domain.Known(false) {
		t.Fatalf("a threat is not calm: %v", got)
	}
	if got := RitualCalm(domain.Known(int64(0)), domain.Known(int64(0))); got != domain.Known(true) {
		t.Fatalf("calm: %v", got)
	}
	if _, known := RitualCalm(domain.Unknown[int64](), domain.Known(int64(0))).Value(); known {
		t.Fatal("unknown hostiles are not calm")
	}
}

func TestRitualsNeedASiteAndTheRequiredSlots(t *testing.T) {
	ideo := ritualFixture(nil)
	for name, sites := range map[string]domain.Fact[[]RitualSite]{
		"no building": domain.Known([]RitualSite{{ID: "Thing_5", Def: "Altar", Cell: domain.Cell{X: 1, Z: 1}}}),
	} {
		if got := plansAt(t, ideo, sites, 100*ritualDay, true); len(got) != 0 {
			t.Fatalf("%s: %+v", name, got)
		}
	}
	if _, known := PlanRituals(ideo, ritualPawns(), domain.Unknown[[]RitualSite](), 100*ritualDay, domain.Known(true)).Value(); known {
		t.Fatal("unread sites are unknown")
	}
	noGuide := ritualFixture(func(i *Ideoligion) { i.Facts.Roles[0].Pawns = nil })
	if got := plansAt(t, noGuide, lecternSites(), 100*ritualDay, true); len(got) != 0 {
		t.Fatalf("a required role precept with no holder cannot be led: %+v", got)
	}
	inactive := ritualFixture(func(i *Ideoligion) { i.Facts.Roles[0].Active = false })
	if got := plansAt(t, inactive, lecternSites(), 100*ritualDay, true); len(got) != 0 {
		t.Fatalf("an inactive role leads nothing: %+v", got)
	}
	noPattern := ritualFixture(func(i *Ideoligion) { i.Facts.Rituals[0].Pattern = "" })
	if got := plansAt(t, noPattern, lecternSites(), 100*ritualDay, true); len(got) != 0 {
		t.Fatalf("a ritual with no pattern has no cadence or spot: %+v", got)
	}
}

// An unavailable believer (downed, drafted, in a mental state) is not
// gathered; a ritual that leaves a pawn for another keeps its attendees apart.
func TestRitualsGatherOnlyAvailableBelievers(t *testing.T) {
	ideo := ritualFixture(nil)
	pawns := ritualPawns()
	rows, _ := pawns.Value()
	rows[2].Available = domain.Known(false)
	got, _ := PlanRituals(ideo, domain.Known(rows), lecternSites(), 100*ritualDay, domain.Known(true)).Value()
	if len(got) != 1 || got[0].Slots[1].Pawns[0] != "carol" || !reflect.DeepEqual(got[0].Spectators, []PawnID{"alice"}) {
		t.Fatalf("%+v", got)
	}
	rows[0].Available = domain.Known(false)
	if got, _ := PlanRituals(ideo, domain.Known(rows), lecternSites(), 100*ritualDay, domain.Known(true)).Value(); len(got) != 0 {
		t.Fatalf("the moral guide cannot attend: %+v", got)
	}
}

func TestRitualAttendeesAreHeldOffSleep(t *testing.T) {
	ideo := ritualFixture(nil)
	plans := PlanRituals(ideo, ritualPawns(), lecternSites(), 100*ritualDay, domain.Known(true))
	hold := HeldOffSleep(RoutineFacts{RitualPlans: plans})
	if len(hold) != 4 || !hold["guide"] || !hold["alice"] || !hold["bob"] || !hold["carol"] || hold["outsider"] {
		t.Fatalf("%v", hold)
	}
	if HeldOffSleep(RoutineFacts{RitualPlans: PlanRituals(ideo, ritualPawns(), lecternSites(), 100*ritualDay, domain.Known(false))}) != nil {
		t.Fatal("a held ritual holds nobody")
	}
	if HeldOffSleep(RoutineFacts{}) != nil {
		t.Fatal("unknown plans hold nobody")
	}
	both := HeldOffSleep(RoutineFacts{RitualPlans: plans, Royalty: domain.Known(RoyaltyFacts{Ceremonies: []BestowingCeremony{{Pawn: "Envoy", Accepted: domain.Known(true)}}})})
	if !both["Envoy"] || !both["guide"] {
		t.Fatalf("the ceremony hold and the ritual hold merge: %v", both)
	}
	schedule := func(id PawnID) WorkPawn {
		p := testWorkPawn(id, true, false, nil)
		p.Schedule = domain.Known(nativeDefaultSchedule())
		return p
	}
	decision := PlanSchedulesHeld([]WorkPawn{schedule("guide"), schedule("zed")}, domain.Unknown[ComfortObservation](), false, hold)
	sleeps := func(slots []string) int {
		n := 0
		for _, s := range slots {
			if s == ScheduleSleep {
				n++
			}
		}
		return n
	}
	if sleeps(decision.Schedules[0].Slots) != 0 || sleeps(decision.Schedules[1].Slots) == 0 {
		t.Fatalf("guide %v zed %v", decision.Schedules[0].Slots, decision.Schedules[1].Slots)
	}
}

func TestRitualPlansTakeEachPawnOnce(t *testing.T) {
	ideo := ritualFixture(func(i *Ideoligion) {
		i.Defs.Rituals["Sermon2"] = i.Defs.Rituals["Sermon"]
		i.Facts.Rituals = append(i.Facts.Rituals, HeldRitual{ID: "Precept_4", Def: "Ritual_Sermon2", Pattern: "Sermon2"})
	})
	got := plansAt(t, ideo, lecternSites(), 100*ritualDay, true)
	if len(got) != 1 {
		t.Fatalf("the second ritual has no moral guide left to lead it: %+v", got)
	}
}

func TestRitualsOwedRaisesMaintainRituals(t *testing.T) {
	f := stableRoutine()
	f.RitualsOwed = domain.Known(true)
	for _, g := range needs(t, f, RoutineLatches{}).Goals {
		if g.ID == MaintainRituals {
			return
		}
	}
	t.Fatal("a ritual ready to begin raised no MaintainRituals goal")
}

func TestRitualsOwedIsUnknownWhileItsInputsAre(t *testing.T) {
	if _, known := RitualsOwed(domain.Unknown[[]RitualPlan]()).Value(); known {
		t.Fatal("unknown plans owe nothing known")
	}
}
