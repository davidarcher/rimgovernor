package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var helpMap = domain.MapID(7)

var helpWorld = domain.GenerationSnapshot{Colony: "c", Map: helpMap, Load: "l"}

func helpTeam() []WorkPawn {
	builder := testWorkPawn("builder", true, false, []WorkSkill{{Name: "Construction", Level: 9}, {Name: "Cooking", Level: 6}})
	builder.Job = domain.Known(PawnJob{Def: "DoBill", Work: WorkCooking})
	a := testWorkPawn("a", true, false, []WorkSkill{{Name: "Construction", Level: 2}})
	a.Job = domain.Known(PawnJob{Def: "Wait_Wander"})
	b := testWorkPawn("b", true, false, []WorkSkill{{Name: "Construction", Level: 1}})
	b.Job = domain.Known(PawnJob{Def: "GotoWander"})
	return []WorkPawn{builder, a, b}
}

func wallReport(n int, extra ...ReadyWork) *ReadyWorkReport {
	r := &ReadyWorkReport{Colony: "c", Map: helpMap, Load: "l"}
	for i := 0; i < n; i++ {
		r.Candidates = append(r.Candidates, ReadyWork{Stage: "building:Wall", Work: LaborProfile{WorkConstruction}, State: ReadyRunnable, Parallelism: 1, Adapter: ReadyMigrated, Claims: []ReadyClaim{CellClaim(domain.Cell{X: int32(i)})}})
	}
	r.Candidates = append(r.Candidates, extra...)
	return r
}

func planHelp(t *testing.T, pawns []WorkPawn, overrides []WorkOverride, h ConstructionHelp, resting ...DiseaseRest) WorkDecision {
	t.Helper()
	d, err := PlanWork(pawns, nil, overrides, WorkDemand{Construction: true, Help: &h, Resting: resting})
	if err != nil {
		t.Fatal(err)
	}
	if d.Help == nil {
		t.Fatal("no help record")
	}
	return d
}

// An occupied skilled builder and two idle sub-floor pawns with six walls
// ready: after idling across two reviews both help at 4, the builder stays
// the owner at 1.
func TestConstructionHelpersAssistOccupiedBuilder(t *testing.T) {
	pawns := helpTeam()
	first := planHelp(t, pawns, nil, ConstructionHelpDemand(wallReport(6), helpWorld, 100, []string{"Wall"}, nil))
	if first.Help.Reason != HelpNoSpare || len(first.Help.Helpers) != 0 || workValue(t, first, "a", WorkConstruction) != 0 {
		t.Fatalf("first review must only see spare capacity: %+v", first.Help)
	}
	second := planHelp(t, pawns, nil, ConstructionHelpDemand(wallReport(6), helpWorld, 700, []string{"Wall"}, first.Help))
	if second.Help.Reason != HelpAssigned || !reflect.DeepEqual(second.Help.Helpers, []PawnID{"a", "b"}) || second.Help.Unmet != 6 {
		t.Fatalf("%+v", second.Help)
	}
	if workValue(t, second, "builder", WorkConstruction) != 1 || workValue(t, second, "a", WorkConstruction) != 4 || workValue(t, second, "b", WorkConstruction) != 4 {
		t.Fatal(second.Assignments)
	}
	// The policy preference is untouched for other work: a stays off Cooking.
	if workValue(t, second, "a", WorkCooking) != 0 || workValue(t, second, "a", WorkDoctor) != 0 {
		t.Fatal("helper relaxed an unrelated floor")
	}
	// Bounded by ready work: one wall, a free builder, no helper.
	pawns[0].Job = domain.Known(PawnJob{Def: "Wait_Wander"})
	one := planHelp(t, pawns, nil, ConstructionHelpDemand(wallReport(1), helpWorld, 700, nil, &ConstructionHelpRecord{Tick: 100, Idle: []PawnID{"a", "b"}}))
	if one.Help.Unmet != 0 || len(one.Help.Helpers) != 0 {
		t.Fatalf("%+v", one.Help)
	}
	two := planHelp(t, pawns, nil, ConstructionHelpDemand(wallReport(2), helpWorld, 700, nil, &ConstructionHelpRecord{Tick: 100, Idle: []PawnID{"a", "b"}}))
	if !reflect.DeepEqual(two.Help.Helpers, []PawnID{"a"}) {
		t.Fatalf("one unmet wall takes the better helper: %+v", two.Help)
	}
}

func TestConstructionHelpersRespectRestrictions(t *testing.T) {
	prev := &ConstructionHelpRecord{Tick: 100, Idle: []PawnID{"a", "b"}}
	help := func() ConstructionHelp {
		return ConstructionHelpDemand(wallReport(6), helpWorld, 700, []string{"Wall"}, prev)
	}
	// Player override, disabled work, incapable skill, resting pawn.
	pawns := helpTeam()
	d := planHelp(t, pawns, []WorkOverride{{Pawn: "a", Work: WorkConstruction, Priority: 0}}, help())
	if !reflect.DeepEqual(d.Help.Helpers, []PawnID{"b"}) || workValue(t, d, "a", WorkConstruction) != 0 {
		t.Fatalf("override: %+v", d.Help)
	}
	pawns = helpTeam()
	work, _ := pawns[1].Work.Value()
	for i := range work {
		if work[i].Work == WorkConstruction {
			work[i].Disabled = true
		}
	}
	pawns[2].Skills = domain.Known([]WorkSkill{{Name: "Construction", Disabled: true}})
	if d := planHelp(t, pawns, nil, help()); len(d.Help.Helpers) != 0 || d.Help.Reason != HelpNoSpare {
		t.Fatalf("disabled/incapable: %+v", d.Help)
	}
	pawns = helpTeam()
	if d := planHelp(t, pawns, nil, help(), DiseaseRest{Pawn: "a", Conditions: []string{"Flu"}}, DiseaseRest{Pawn: "b", Conditions: []string{"Flu"}}); len(d.Help.Helpers) != 0 {
		t.Fatalf("resting: %+v", d.Help)
	}
	// A native requirement minimum above the helper's level excludes it.
	pawns = helpTeam()
	h := help()
	d2, err := PlanWork(pawns, []WorkRequirement{{Work: WorkConstruction, Skill: "Construction", Minimum: 2}}, nil, WorkDemand{Construction: true, Help: &h})
	if err != nil || !reflect.DeepEqual(d2.Help.Helpers, []PawnID{"a"}) {
		t.Fatalf("requirement: %+v %v", d2.Help, err)
	}
	// Unknown job is not spare capacity.
	pawns = helpTeam()
	pawns[1].Job = domain.Unknown[PawnJob]()
	if d := planHelp(t, pawns, nil, help()); !reflect.DeepEqual(d.Help.Helpers, []PawnID{"b"}) {
		t.Fatalf("unknown job: %+v", d.Help)
	}
}

// Risky or unknown tasks withhold helpers, and a risky task appearing
// withdraws current helpers at once. The coarse limit: between reviews the
// helper's Construction priority covers any frame, which the record names.
func TestConstructionHelpersWithheldForRiskyOrUnknownWork(t *testing.T) {
	prev := &ConstructionHelpRecord{Tick: 100, Idle: []PawnID{"a", "b"}, Helpers: []PawnID{"a"}, DemandTick: 100}
	pawns := helpTeam()
	bed := ReadyWork{Stage: "building:Bed", Work: LaborProfile{WorkConstruction}, State: ReadyBlocked, Adapter: ReadyMigrated}
	d := planHelp(t, pawns, nil, ConstructionHelpDemand(wallReport(6, bed), helpWorld, 700, []string{"Wall", "Bed"}, prev))
	if d.Help.Reason != HelpRiskyTask || len(d.Help.Helpers) != 0 || workValue(t, d, "a", WorkConstruction) != 0 || !reflect.DeepEqual(d.Help.Risky, []string{"Bed", "building:Bed"}) {
		t.Fatalf("%+v", d.Help)
	}
	// A player blueprint's definition alone is enough.
	if h := ConstructionHelpDemand(wallReport(6), helpWorld, 700, []string{"Table2x2c"}, prev); len(h.Risky) != 1 {
		t.Fatal(h)
	}
	// A conservative-adapter candidate that may build is unknown work.
	cons := ReadyWork{Stage: "shelter", Work: LaborProfile{WorkConstruction, WorkHauling}, State: ReadyRunnable, Parallelism: 1, Adapter: ReadyConservative}
	if h := ConstructionHelpDemand(wallReport(6, cons), helpWorld, 700, nil, prev); len(h.Risky) != 1 {
		t.Fatal(h)
	}
	// Another world's report is unknown demand.
	if d := planHelp(t, pawns, nil, ConstructionHelpDemand(wallReport(6), domain.GenerationSnapshot{Colony: "c", Map: helpMap, Load: "other"}, 700, nil, prev)); d.Help.Reason != HelpDemandUnknown || len(d.Help.Helpers) != 0 {
		t.Fatalf("%+v", d.Help)
	}
}

// Demand clearing keeps helpers through the hold, then restores the
// governor's ordinary priority; a player override wins throughout.
func TestConstructionHelpersHoldThenRestore(t *testing.T) {
	pawns := helpTeam()
	prev := &ConstructionHelpRecord{Tick: 700, Idle: []PawnID{"a", "b"}, Helpers: []PawnID{"a", "b"}, DemandTick: 700}
	held := planHelp(t, pawns, nil, ConstructionHelpDemand(wallReport(0), helpWorld, 1300, nil, prev))
	if held.Help.Reason != HelpHeld || !reflect.DeepEqual(held.Help.Helpers, []PawnID{"a", "b"}) || held.Help.DemandTick != 700 {
		t.Fatalf("%+v", held.Help)
	}
	// Stable across reviews: the same record plans the same priorities.
	again := planHelp(t, pawns, nil, ConstructionHelpDemand(wallReport(0), helpWorld, 1900, nil, held.Help))
	if !reflect.DeepEqual(again.Assignments, held.Assignments) {
		t.Fatal("held helpers oscillated")
	}
	gone := planHelp(t, pawns, nil, ConstructionHelpDemand(wallReport(0), helpWorld, 700+ConstructionHelpHoldTicks, nil, again.Help))
	if gone.Help.Reason != HelpNoDemand || len(gone.Help.Helpers) != 0 || workValue(t, gone, "a", WorkConstruction) != 0 {
		t.Fatalf("%+v", gone.Help)
	}
	// A player who set a helper's Construction keeps it after restoration.
	kept := planHelp(t, pawns, []WorkOverride{{Pawn: "a", Work: WorkConstruction, Priority: 2}}, ConstructionHelpDemand(wallReport(0), helpWorld, 700+ConstructionHelpHoldTicks, nil, again.Help))
	if workValue(t, kept, "a", WorkConstruction) != 2 {
		t.Fatal("restoration clobbered a player edit")
	}
}
