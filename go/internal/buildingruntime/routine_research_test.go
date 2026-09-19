package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// researchNative is the routine census plus a research census the review
// measures the roadmap against and the planner re-reads before selecting.
type researchNative struct {
	*routineNative
	current  string
	finished []string
	// benchMissing locks every project on the bench the colony lacks.
	benchMissing bool
}

func (n *researchNative) ids() []string { return []string{"scholar-a", "scholar-b", "scholar-c"} }

func (n *researchNative) ReadEmergency(ctx context.Context, _ *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	facts := policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}
	for _, id := range n.ids() {
		facts.Colonists = append(facts.Colonists, policy.EmergencyPawn{ID: policy.PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)})
	}
	return bridge.EmergencyObservation{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Facts: facts}, bridge.Result{}, ctx.Err()
}

func (n *researchNative) ReadResearch(ctx context.Context, _ *c.Identity) (bridge.ResearchRead, bridge.Result, error) {
	known := func(names ...string) domain.Fact[[]policy.ResearchProjectID] {
		ids := make([]policy.ResearchProjectID, len(names))
		for i, name := range names {
			ids[i] = policy.ResearchProjectID(name)
		}
		return domain.Known(ids)
	}
	read := bridge.ResearchRead{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), SnapshotToken: "token", CurrentProject: n.current, Finished: n.finished, Projects: map[string]policy.ResearchProjectFacts{
		"Stonecutting": {Name: "Stonecutting", Hidden: domain.Known(false), Prerequisites: known(), HiddenPrerequisites: known()},
		"Electricity":  {Name: "Electricity", Hidden: domain.Known(false), Prerequisites: known(), HiddenPrerequisites: known()},
		"Batteries":    {Name: "Batteries", Hidden: domain.Known(false), Prerequisites: known("Electricity"), HiddenPrerequisites: known()},
	}}
	if n.benchMissing {
		for name, facts := range read.Projects {
			facts.RequiredBuilding, facts.LockReasons = "SimpleResearchBench", []string{policy.ResearchLockBench}
			read.Projects[name] = facts
		}
	}
	return read, bridge.Result{}, ctx.Err()
}

// With no operator target and no workshop need, the research planner walks
// the default ladder: it selects the first unfinished rung while the tab is
// idle, and lends the clock ticks while any project is current so the rung
// finishes on its own (#230).
func TestRoutineResearchWalksTheLadderAndLendsTicks(t *testing.T) {
	t.Parallel()
	reviewer, db, _, _, base := routineFixture(t)
	n := &researchNative{routineNative: base, finished: []string{"Stonecutting"}}
	v := base.reply.GetObserved()
	// Three research-capable colonists: a development slot is bounded by
	// observed workers, and the fixture's own player plan holds one.
	v.ColonistCount, v.WorkerCount = proto.Uint32(3), proto.Uint32(3)
	complete := &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(3), Returned: proto.Uint64(3), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	pawns := &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: complete}
	for _, id := range n.ids() {
		pawns.Pawns = append(pawns.Pawns, &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(0)}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{Skills: []*o.Skill{{Definition: &o.DefinitionRef{DefName: proto.String("Intellectual")}, Level: proto.Int32(6), Passion: proto.String("None"), Disabled: proto.Bool(false)}}}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true), Work: []*o.WorkSetting{{DefName: proto.String("Research"), Priority: proto.Int32(1), Disabled: proto.Bool(false)}}}, Issues: []*o.ReadIssue{{Field: proto.String("pawn.snapshot"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}, {Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}})
	}
	base.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: pawns}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{policy.EnsureResearch})
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineResearchPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted || result.Plan == "" {
		review, _ := db.LoadRoutineReview(context.Background())
		t.Fatal(result, err, review.Development.Rows)
	}
	plan, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil || len(plan.Spec.Actions()) != 1 {
		t.Fatal(plan, err)
	}
	if selected, ok := plan.Spec.Actions()[0].ResearchSelect(); !ok || selected.Project() != "Electricity" {
		t.Fatal("first unfinished rung after Stonecutting", plan.Spec.Actions()[0])
	}
	// The project is current: the goal recovers, the planner keeps the
	// clock moving until it finishes rather than proposing anything.
	n.current = "Electricity"
	if _, err = reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	result, err = planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodUsed || result.NativeWorkTicks != researchNativeWorkTicks {
		t.Fatal(result, err)
	}
	// An empty ladder with no target composes nothing.
	n.current = ""
	reviewer.policy.ResearchLadder = nil
	if _, err = reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	if result, err = planner.Step(context.Background()); err != nil || result.Reason != BuildingMethodDisabled {
		t.Fatal(result, err)
	}
}

// A rung locked only for lack of a research bench is a building need, never
// a selection native SelectResearch would refuse: with no building ladder
// composed the planner reports the bench hold by name (#254).
func TestRoutineResearchReportsTheBenchHoldInsteadOfSelecting(t *testing.T) {
	t.Parallel()
	reviewer, db, _, _, base := routineFixture(t)
	n := &researchNative{routineNative: base, finished: []string{"Stonecutting"}, benchMissing: true}
	v := base.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(3), proto.Uint32(3)
	complete := &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(3), Returned: proto.Uint64(3), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	pawns := &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: complete}
	for _, id := range n.ids() {
		pawns.Pawns = append(pawns.Pawns, &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(0)}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{Skills: []*o.Skill{{Definition: &o.DefinitionRef{DefName: proto.String("Intellectual")}, Level: proto.Int32(6), Passion: proto.String("None"), Disabled: proto.Bool(false)}}}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true), Work: []*o.WorkSetting{{DefName: proto.String("Research"), Priority: proto.Int32(1), Disabled: proto.Bool(false)}}}, Issues: []*o.ReadIssue{{Field: proto.String("pawn.snapshot"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}, {Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}})
	}
	base.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: pawns}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{policy.EnsureResearch})
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineResearchPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	if planner.building != nil {
		t.Fatal("a source without placement previews composes no ladder")
	}
	result, err := planner.Step(context.Background())
	if err != nil || result.Reason != BuildingResearchBench || result.Plan != "" {
		t.Fatal(result, err)
	}
	review, err := db.LoadRoutineReview(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	for _, binding := range review.Goals {
		if binding.Need != policy.EnsureResearch {
			continue
		}
		if goal, err := db.LoadGoal(context.Background(), binding.Goal); err != nil || len(goal.Methods) != 0 {
			t.Fatal("no selection may be committed while the bench is missing", goal, err)
		}
	}
	// A project the game let through without a bench (Stonecutting names
	// none) is not lent ticks: nobody can research it, the bench is owed.
	n.current = "Electricity"
	if _, err = reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	if result, err = planner.Step(context.Background()); err != nil || result.Reason != BuildingResearchBench || result.NativeWorkTicks != 0 {
		t.Fatal(result, err)
	}
	n.current = ""
	// The bench standing, the same rung is selected.
	n.benchMissing = false
	if _, err = reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	if result, err = planner.Step(context.Background()); err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
}

// The research bench rung maps the planning census onto the facility
// ladder: the simple bench, indoors, in a Laboratory-hosting room.
func TestResearchBenchSelectMapsOntoTheLadder(t *testing.T) {
	t.Parallel()
	ladder := &RoutineBuildingPlanner{goal: policy.EnsureResearch, definition: "Wall", shelter: true}
	definition := func(available domain.Fact[bool]) []observation.PlanningDefinition {
		return []observation.PlanningDefinition{{Name: policy.ResearchBenchDefinition, Available: available, NeedsPower: domain.Known(false), ConstructionSkill: domain.Known(int32(0)), Stuff: domain.Known("WoodLog")}}
	}
	for _, test := range []struct {
		name   string
		facts  observation.ColonyProjection
		reason RoutineBuildingReason
	}{
		{"undescribed", observation.ColonyProjection{}, BuildingMethodUnknown},
		{"unknown", observation.ColonyProjection{Definitions: definition(domain.Unknown[bool]())}, BuildingMethodUnknown},
		{"unavailable", observation.ColonyProjection{Definitions: definition(domain.Known(false))}, BuildingResearchBenchUnavailable},
		{"build", observation.ColonyProjection{Definitions: definition(domain.Known(true))}, ""},
	} {
		selected, reason, err := ladder.selectResearchBench(test.facts)
		if err != nil || reason != test.reason {
			t.Fatal(test.name, selected, reason, err)
		}
		if test.reason != "" {
			if selected != nil {
				t.Fatal(test.name, selected)
			}
			continue
		}
		if selected.definition != policy.ResearchBenchDefinition || selected.stuff != "WoodLog" || selected.environment != policy.PlacementIndoors || selected.facility == nil || selected.facility.Role != policy.RoomRoleLaboratory || !selected.facility.Hosts(policy.RoomRoleBarracks) {
			t.Fatal(test.name, selected)
		}
		if ladder.definition != "Wall" || ladder.facility != nil {
			t.Fatal("selection mutated reusable ladder", ladder)
		}
	}
	if !ladder.facilityLadder() {
		t.Fatal("the research bench walks the facility ladder")
	}
	facts := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(2))}}
	if missing, method, reason := ladder.selection(facts); missing != 32 || method != "laboratory-shell" || reason != "" {
		t.Fatal(missing, method, reason)
	}
	bench := &RoutineBuildingPlanner{goal: policy.EnsureResearch, definition: policy.ResearchBenchDefinition}
	if missing, method, reason := bench.selection(facts); missing != 1 || method != "laboratory-SimpleResearchBench" || reason != "" {
		t.Fatal(missing, method, reason)
	}
}
