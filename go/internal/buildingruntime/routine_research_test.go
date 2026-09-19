package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
