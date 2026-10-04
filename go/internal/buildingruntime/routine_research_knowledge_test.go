package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	snap "github.com/davidarcher/RimGovernor/go/internal/snapshot"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// An empty Anomaly knowledge slot is a research need of its own (#1745): the
// planner fills it with the cheapest startable project of that category even
// while the ordinary slot is busy (the two slots are independent), and
// never from another category.
func TestRoutineResearchFillsAnEmptyKnowledgeSlot(t *testing.T) {
	t.Parallel()
	reviewer, db, _, _, base := routineFixture(t)
	reviewer.policy.Stage.Floor = policy.StageDevelopment
	n := &researchNative{routineNative: base, finished: []string{"Stonecutting"}, current: "Electricity", knowledge: []policy.KnowledgeSlot{{Category: "Advanced"}, {Category: "Basic"}}}
	v := base.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(3), proto.Uint32(3)
	complete := &o.Completeness{Filtered: proto.Uint64(0)}
	pawns := &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: complete}
	for _, id := range n.ids() {
		pawns.Pawns = append(pawns.Pawns, &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(0)}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{Skills: []*o.Skill{{DefName: proto.String("Intellectual"), Level: proto.Int32(6), Passion: o.Passion_PASSION_NONE.Enum(), Disabled: proto.Bool(false)}}}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true), Work: []*o.WorkSetting{{DefName: proto.String("Research"), Priority: proto.Int32(1), Disabled: proto.Bool(false)}, {DefName: proto.String("Construction"), Priority: proto.Int32(1), Disabled: proto.Bool(false)}}}, Issues: []*o.ReadIssue{{Field: proto.String("pawn.snapshot"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}, {Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}})
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
	if err != nil || result.Verdict != BuildingReasonAdmitted || result.Plan == "" {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), result.Plan)
	if err != nil || len(plan.Spec.Actions()) != 1 {
		t.Fatal(plan, err)
	}
	if selected, ok := plan.Spec.Actions()[0].ResearchSelect(); !ok || selected.Project() != "BioferriteExtraction" {
		t.Fatal("cheapest startable basic project", plan.Spec.Actions()[0])
	}
}

// A queue head that is a knowledge project is selected into its slot; one
// whose slot holds another project waits, and a locked head is unavailable.
func TestResearchKnowledgeNextFollowsTheQueueHead(t *testing.T) {
	t.Parallel()
	known := func(names ...string) domain.Fact[[]policy.ResearchProjectID] {
		ids := make([]policy.ResearchProjectID, len(names))
		for i, name := range names {
			ids[i] = policy.ResearchProjectID(name)
		}
		return domain.Known(ids)
	}
	read := bridge.ResearchRead{Knowledge: []policy.KnowledgeSlot{{Category: "Basic"}}, Projects: map[string]policy.ResearchProjectFacts{
		"BioferriteExtraction": {Name: "BioferriteExtraction", Hidden: domain.Known(false), KnowledgeCategory: "Basic", Cost: 5, Census: true, Prerequisites: known(), HiddenPrerequisites: known()},
		"BioferriteShaping":    {Name: "BioferriteShaping", Hidden: domain.Known(false), KnowledgeCategory: "Basic", Cost: 20, Census: true, Prerequisites: known("BioferriteExtraction"), HiddenPrerequisites: known()},
	}}
	in := snap.ResearchCall{Needs: []string{"BioferriteShaping"}, Read: read}
	if got, reason := researchKnowledgeNext(in); got != "BioferriteExtraction" || !reason.IsZero() {
		t.Fatal("the prerequisite is the head", got, reason)
	}
	in.Read.Knowledge = []policy.KnowledgeSlot{{Category: "Basic", Current: "BioferriteExtraction"}}
	if got, reason := researchKnowledgeNext(in); got != "" || !reason.Is(WaitMethodUsed) {
		t.Fatal("a held slot is never replaced", got, reason)
	}
	locked := in.Read.Projects["BioferriteExtraction"]
	locked.LockReasons = []string{"techprints"}
	in.Read.Projects = map[string]policy.ResearchProjectFacts{"BioferriteExtraction": locked}
	in.Read.Knowledge = []policy.KnowledgeSlot{{Category: "Basic"}}
	in.Needs = []string{"BioferriteExtraction"}
	if got, reason := researchKnowledgeNext(in); got != "" || !reason.Is(RefusalFieldUnavailable) {
		t.Fatal("a locked head is unavailable, not selected", got, reason)
	}
}
