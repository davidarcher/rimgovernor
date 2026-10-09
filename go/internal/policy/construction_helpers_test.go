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
		r.Candidates = append(r.Candidates, ReadyWork{Stage: "building:Wall", Work: LaborProfile{WorkConstruction}, State: ReadyRunnable, Parallelism: 1, Claims: []ReadyClaim{CellClaim(domain.Cell{X: int32(i)})}})
	}
	r.Candidates = append(r.Candidates, extra...)
	return r
}

// wallCensus is an observed quality-free wall blueprint, the fact that makes
// the Wall definition helper work.
func wallCensus() domain.Fact[CurrentConstruction] {
	b, _ := domain.NewBuilding("Wall", domain.Cell{X: 0, Z: 1}, domain.North, "WoodLog")
	return domain.Known(CurrentConstruction{Colony: true, Sites: []ConstructionSite{{Building: b, Stage: "blueprint", ID: "w", QualitySensitive: domain.Known(false), NativeFinishingSkill: domain.Known(0)}}})
}

// helpDemand reads demand over the wall census with prerequisite-free plans.
func helpDemand(report *ReadyWorkReport, world domain.GenerationSnapshot, tick domain.Tick, names []string, previous *ConstructionHelpRecord) ConstructionHelp {
	return ConstructionHelpDemand(report, world, tick, helpNames(names...), previous, wallCensus())
}

// helpNames are plan definitions with an observed zero prerequisite.
func helpNames(names ...string) []HelpDefinition {
	defs := []HelpDefinition{}
	for _, n := range names {
		defs = append(defs, HelpDefinition{Name: n, Skill: domain.Known(0)})
	}
	return defs
}

func planHelp(t *testing.T, pawns []WorkPawn, h ConstructionHelp, resting ...DiseaseRest) WorkDecision {
	t.Helper()
	d, err := PlanWork(pawns, nil, WorkDemand{Construction: true, Help: &h, Resting: resting})
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
	first := planHelp(t, pawns, helpDemand(wallReport(6), helpWorld, 100, []string{"Wall"}, nil))
	if first.Help.Reason != HelpNoSpare || len(first.Help.Helpers) != 0 || workValue(t, first, "a", WorkConstruction) != 0 {
		t.Fatalf("first review must only see spare capacity: %+v", first.Help)
	}
	second := planHelp(t, pawns, helpDemand(wallReport(6), helpWorld, 700, []string{"Wall"}, first.Help))
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
	one := planHelp(t, pawns, helpDemand(wallReport(1), helpWorld, 700, nil, &ConstructionHelpRecord{Tick: 100, Idle: []PawnID{"a", "b"}}))
	if one.Help.Unmet != 0 || len(one.Help.Helpers) != 0 {
		t.Fatalf("%+v", one.Help)
	}
	two := planHelp(t, pawns, helpDemand(wallReport(2), helpWorld, 700, nil, &ConstructionHelpRecord{Tick: 100, Idle: []PawnID{"a", "b"}}))
	if !reflect.DeepEqual(two.Help.Helpers, []PawnID{"a"}) {
		t.Fatalf("one unmet wall takes the better helper: %+v", two.Help)
	}
}

func TestConstructionHelpersRespectRestrictions(t *testing.T) {
	prev := &ConstructionHelpRecord{Tick: 100, Idle: []PawnID{"a", "b"}}
	help := func() ConstructionHelp {
		return helpDemand(wallReport(6), helpWorld, 700, []string{"Wall"}, prev)
	}
	// A player who switched Construction off does not keep a pawn from
	// helping: Governor owns every priority.
	pawns := helpTeam()
	setObservedWork(pawns[1], WorkConstruction, 0)
	d := planHelp(t, pawns, help())
	if !reflect.DeepEqual(d.Help.Helpers, []PawnID{"a", "b"}) || workValue(t, d, "a", WorkConstruction) != 4 {
		t.Fatalf("player edit: %+v", d.Help)
	}
	// Disabled work, incapable skill, resting pawn.
	pawns = helpTeam()
	work, _ := pawns[1].Work.Value()
	for i := range work {
		if work[i].Work == WorkConstruction {
			work[i].Disabled = true
		}
	}
	pawns[2].Skills = domain.Known([]WorkSkill{{Name: "Construction", Disabled: true}})
	if d := planHelp(t, pawns, help()); len(d.Help.Helpers) != 0 || d.Help.Reason != HelpNoSpare {
		t.Fatalf("disabled/incapable: %+v", d.Help)
	}
	pawns = helpTeam()
	if d := planHelp(t, pawns, help(), DiseaseRest{Pawn: "a", Conditions: []string{"Flu"}}, DiseaseRest{Pawn: "b", Conditions: []string{"Flu"}}); len(d.Help.Helpers) != 0 {
		t.Fatalf("resting: %+v", d.Help)
	}
	// An observed prerequisite above the helper's level excludes it.
	pawns = helpTeam()
	h := ConstructionHelpDemand(wallReport(6), helpWorld, 700, []HelpDefinition{{Name: "Wall", Skill: domain.Known(2)}}, prev, wallCensus())
	d2, err := PlanWork(pawns, nil, WorkDemand{Construction: true, Help: &h})
	if err != nil || !reflect.DeepEqual(d2.Help.Helpers, []PawnID{"a"}) {
		t.Fatalf("requirement: %+v %v", d2.Help, err)
	}
	// Unknown job is not spare capacity.
	pawns = helpTeam()
	pawns[1].Job = domain.Unknown[PawnJob]()
	if d := planHelp(t, pawns, help()); !reflect.DeepEqual(d.Help.Helpers, []PawnID{"b"}) {
		t.Fatalf("unknown job: %+v", d.Help)
	}
}

// A definition with unobserved quality or prerequisite withholds helpers under
// a reason naming that fact, and one appearing withdraws current helpers at
// once. The coarse limit: between reviews the helper's Construction priority
// covers any frame, which the record names.
func TestConstructionHelpersWithheldForRiskyOrUnknownWork(t *testing.T) {
	prev := &ConstructionHelpRecord{Tick: 100, Idle: []PawnID{"a", "b"}, Helpers: []PawnID{"a"}, DemandTick: 100}
	pawns := helpTeam()
	bed := ReadyWork{Stage: "building:Bed", Work: LaborProfile{WorkConstruction}, State: ReadyBlocked}
	d := planHelp(t, pawns, helpDemand(wallReport(6, bed), helpWorld, 700, []string{"Wall", "Bed"}, prev))
	if d.Help.Reason != HelpQualityUnknown || len(d.Help.Helpers) != 0 || workValue(t, d, "a", WorkConstruction) != 0 || !reflect.DeepEqual(d.Help.Withheld, []string{"Bed"}) {
		t.Fatalf("%+v", d.Help)
	}
	// A plan definition with no observed site has unknown quality.
	if h := helpDemand(wallReport(6), helpWorld, 700, []string{"Table2x2c"}, prev); h.Reason != HelpQualityUnknown || len(h.Withheld) != 1 {
		t.Fatal(h)
	}
	// An unobserved prerequisite names itself; the site's own prerequisite
	// stands in for an unread definition row.
	census := wallCensus()
	if got := ConstructionHelpDemand(wallReport(6), helpWorld, 700, []HelpDefinition{{Name: "Wall", Skill: domain.Unknown[int]()}}, prev, census); got.Reason != "" || got.Floor != 0 {
		t.Fatalf("site prerequisite should stand in: %+v", got)
	}
	sites, _ := census.Value()
	sites.Sites[0].NativeFinishingSkill = domain.Unknown[int]()
	if got := ConstructionHelpDemand(wallReport(6), helpWorld, 700, []HelpDefinition{{Name: "Wall", Skill: domain.Unknown[int]()}}, prev, census); got.Reason != HelpPrerequisiteUnknown || !reflect.DeepEqual(got.Withheld, []string{"Wall"}) {
		t.Fatalf("%+v", got)
	}
	// A def no list ever named (Door) gets helpers from observed facts alone.
	door, _ := domain.NewBuilding("Door", domain.Cell{X: 9, Z: 1}, domain.North, "WoodLog")
	sites.Sites[0] = ConstructionSite{Building: door, Stage: "frame", ID: "d", ResourcesComplete: domain.Known(true), QualitySensitive: domain.Known(false), NativeFinishingSkill: domain.Known(1)}
	got := ConstructionHelpDemand(nil, helpWorld, 700, nil, prev, census)
	if ready, _ := got.Ready.Value(); got.Reason != "" || got.Floor != 1 || ready != 1 {
		t.Fatalf("%+v", got)
	}
	// A quality site with no finishing minimum is unprotected; one with it is native-guarded.
	sites.Sites[0].QualitySensitive = domain.Known(true)
	if got := ConstructionHelpDemand(nil, helpWorld, 700, nil, prev, census); got.Reason != HelpQualityUnprotected {
		t.Fatalf("%+v", got)
	}
	sites.Sites[0].MinimumFinishingSkill = domain.Known(5)
	if got := ConstructionHelpDemand(nil, helpWorld, 700, nil, prev, census); got.Reason != "" || got.Floor != 0 {
		t.Fatalf("%+v", got)
	}
	// Another world's report with no census is unknown demand.
	if d := planHelp(t, pawns, ConstructionHelpDemand(wallReport(6), domain.GenerationSnapshot{Colony: "c", Map: helpMap, Load: "other"}, 700, nil, prev, domain.Unknown[CurrentConstruction]())); d.Help.Reason != HelpDemandUnknown || len(d.Help.Helpers) != 0 {
		t.Fatalf("%+v", d.Help)
	}
}

// Demand clearing keeps helpers through the hold, then restores the
// governor's ordinary priority, over any player edit.
func TestConstructionHelpersHoldThenRestore(t *testing.T) {
	pawns := helpTeam()
	prev := &ConstructionHelpRecord{Tick: 700, Idle: []PawnID{"a", "b"}, Helpers: []PawnID{"a", "b"}, DemandTick: 700}
	held := planHelp(t, pawns, helpDemand(wallReport(0), helpWorld, 1300, nil, prev))
	if held.Help.Reason != HelpHeld || !reflect.DeepEqual(held.Help.Helpers, []PawnID{"a", "b"}) || held.Help.DemandTick != 700 {
		t.Fatalf("%+v", held.Help)
	}
	// Stable across reviews: the same record plans the same priorities.
	again := planHelp(t, pawns, helpDemand(wallReport(0), helpWorld, 1900, nil, held.Help))
	if !reflect.DeepEqual(again.Assignments, held.Assignments) {
		t.Fatal("held helpers oscillated")
	}
	gone := planHelp(t, pawns, helpDemand(wallReport(0), helpWorld, 700+ConstructionHelpHoldTicks, nil, again.Help))
	if gone.Help.Reason != HelpNoDemand || len(gone.Help.Helpers) != 0 || workValue(t, gone, "a", WorkConstruction) != 0 {
		t.Fatalf("%+v", gone.Help)
	}
	// A player who set a helper's Construction loses it on restoration.
	edited := helpTeam()
	setObservedWork(edited[1], WorkConstruction, 2)
	kept := planHelp(t, edited, helpDemand(wallReport(0), helpWorld, 700+ConstructionHelpHoldTicks, nil, again.Help))
	if workValue(t, kept, "a", WorkConstruction) != 0 {
		t.Fatal("restoration kept a player edit")
	}
}

// setObservedWork stands in for a player edit in the Work tab.
func setObservedWork(p WorkPawn, work WorkType, priority int) {
	values, _ := p.Work.Value()
	for i := range values {
		if values[i].Work == work {
			values[i].Priority = priority
		}
	}
}
