package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type powerNative struct{ *sleepingNative }

func (n *powerNative) ReadEmergency(ctx context.Context, _ *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "builder", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}}}, bridge.Result{}, ctx.Err()
}

func powerFixture(t *testing.T, conduit bool) (*RoutineBuildingPlanner, *store.Store, *powerNative, store.ControlRequest) {
	t.Helper()
	base, db, _, request, sleeping := sleepingFixture(t)
	n := &powerNative{sleeping}
	v := n.reply.GetObserved()
	v.ColonistCount, v.WorkerCount = proto.Uint32(1), proto.Uint32(1)
	issues := v.Issues[:0]
	for _, issue := range v.Issues {
		if issue.GetField() != "environment" {
			issues = append(issues, issue)
		}
	}
	v.Issues = issues
	count := func(n uint64) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	row := func(id, def string, x int32, base float64) *o.DevelopmentPower {
		cell := &c.Cell{X: proto.Int32(x), Z: proto.Int32(2)}
		return &o.DevelopmentPower{BaseW: proto.Float64(base), Building: &o.BuildingState{Building: &o.EntityRef{Id: proto.String(id), DefName: proto.String(def), MapId: proto.Int32(0), Position: cell}, OccupiedCells: []*c.Cell{cell}, Service: &o.BuildingServiceState{Connected: proto.Bool(false), PowerOn: proto.Bool(false), PowerOutputW: proto.Float64(0), SwitchedOn: proto.Bool(true)}, Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}}}
	}
	development := &o.DevelopmentFacts{Power: []*o.DevelopmentPower{row("lamp", "StandingLamp", 1, -200)}, Completeness: count(1)}
	if conduit {
		development.Power = append(development.Power, row("generator", "WoodFiredGenerator", 4, 1000))
		development.Completeness = count(2)
	}
	v.Development = &o.DevelopmentSection{Outcome: &o.DevelopmentSection_Observed{Observed: development}}
	v.Planning.GetObserved().Definitions = nil
	for _, name := range []string{"HiddenConduit", "WoodFiredGenerator"} {
		v.Planning.GetObserved().Definitions = append(v.Planning.GetObserved().Definitions, &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String(name)}, Available: proto.Bool(true), ConstructionSkill: proto.Int32(4), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(1)}})
	}
	v.Planning.GetObserved().Completeness = count(2)
	pawn := policy.WorkPawn{ID: "builder", Available: domain.Known(true), Applies: domain.Known(true), Manual: domain.Known(true), Ranged: domain.Known(false)}
	var skills []policy.WorkSkill
	for _, name := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		skills = append(skills, policy.WorkSkill{Name: name, Level: 4, Passion: "None"})
	}
	pawn.Skills = domain.Known(skills)
	var work []policy.WorkPriority
	for _, name := range []policy.WorkType{"Construction", "Growing", "Cooking", "Doctor", "PlantCutting", "Hunting", "Firefighter"} {
		work = append(work, policy.WorkPriority{Work: name})
	}
	pawn.Work = domain.Known(work)
	assignment, err := policy.AssignWork([]policy.WorkPawn{pawn}, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	person := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("builder"), MapId: proto.Int32(0)}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{{Field: proto.String("pawn.snapshot"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_UNSUPPORTED.Enum()}}, {Field: proto.String("mental_state"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}}
	for _, skill := range skills {
		person.Biography.Skills = append(person.Biography.Skills, &o.Skill{Definition: &o.DefinitionRef{DefName: proto.String(skill.Name)}, Level: proto.Int32(int32(skill.Level)), Passion: proto.String(skill.Passion), Disabled: proto.Bool(false)})
	}
	for _, w := range assignment.Assignments[0].Priorities {
		person.Settings.Work = append(person.Settings.Work, &o.WorkSetting{DefName: proto.String(string(w.Work)), Priority: proto.Int32(int32(w.Priority)), Disabled: proto.Bool(w.Disabled)})
	}
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{person}, Completeness: count(1)}}}
	base.reviewer.native = n
	base.reviewer.methods = domain.Known([]policy.GoalID{policy.EnsureBasicPower})
	if _, err = base.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutinePowerPlanner(base.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	n.onPreview = func(_ context.Context, p *bridge.BuildingPreview) {
		b, _ := p.Preview.Action.Building()
		p.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
		p.Preview.Costs = domain.Known([]policy.Amount{{Resource: "Steel", Count: 1}})
		p.Stock.Values = []policy.Stock{{Resource: "Steel", Available: domain.Known(int64(100))}}
	}
	return planner, db, n, request
}

func TestRoutinePowerAdmitsSharedWorkAndManualCancels(t *testing.T) {
	t.Parallel()
	for _, conduit := range []bool{false, true} {
		t.Run(map[bool]string{false: "generation", true: "conduit"}[conduit], func(t *testing.T) {
			p, db, n, request := powerFixture(t, conduit)
			first, err := p.Step(context.Background())
			if err != nil || first.Reason != BuildingMethodAdmitted {
				review, _ := db.LoadRoutineReview(context.Background())
				for _, b := range review.Goals {
					if b.Need == policy.EnsureBasicPower {
						g, _ := db.LoadGoal(context.Background(), b.Goal)
						t.Logf("power goal %+v", g)
					}
				}
				t.Fatal(first, err)
			}
			plan, err := db.LoadPlan(context.Background(), first.Decision.Goal.Methods[0].Plan)
			want := 1
			if conduit {
				want = 3
			}
			if err != nil || len(plan.Progress) != want || len(plan.Admissions) != want || first.Decision.Goal.Goal.Need != domain.NeedDeficit {
				t.Fatal(plan, err)
			}
			for _, progress := range plan.Progress {
				if progress.View().Attempt != 0 {
					t.Fatal("compiler dispatched")
				}
			}
			if next, err := p.Step(context.Background()); err != nil || next.Reason != BuildingMethodExistingWork || n.previews != want {
				t.Fatal(next, err)
			}
			request.Kind, request.RequestID = store.PauseControl, "manual-power"
			if _, err = p.reviewer.player.Pause(context.Background(), request); err != nil {
				t.Fatal(err)
			}
			plan, err = db.LoadPlan(context.Background(), plan.Spec.ID())
			if err != nil {
				t.Fatal(err)
			}
			for _, progress := range plan.Progress {
				if progress.View().Stage != domain.Pending {
					t.Fatal(progress)
				}
			}
			if next, err := p.Step(context.Background()); err != nil || next.Reason != BuildingMethodDisabled {
				t.Fatal(next, err)
			}
		})
	}
}

func TestRoutinePowerRejectsUnsafeIncompleteAndUnaffordableRoutes(t *testing.T) {
	t.Parallel()
	for _, phase := range []string{"unsafe", "geometry", "unknown", "stock", "skill", "switched", "flare", "cancelled"} {
		t.Run(phase, func(t *testing.T) {
			p, db, n, _ := powerFixture(t, true)
			v := n.reply.GetObserved()
			switch phase {
			case "skill":
				n.pawnReply.GetObserved().Pawns[0].Biography.Skills[0].Level = proto.Int32(3)
			case "switched":
				v.Development.GetObserved().Power[0].Building.Service.SwitchedOn = proto.Bool(false)
			case "flare":
				v.Environment = []*o.EnvironmentCondition{{Id: proto.String("flare"), DefName: proto.String("SolarFlare")}}
			default:
				original := n.onPreview
				n.onPreview = func(ctx context.Context, preview *bridge.BuildingPreview) {
					original(ctx, preview)
					switch phase {
					case "unsafe":
						preview.Preview.SafeToPlace = domain.Known(false)
					case "geometry":
						preview.Preview.Footprint = domain.Known([]domain.Cell{{X: 4, Z: 4}})
					case "unknown":
						preview.Preview.SafeToPlace = domain.Unknown[bool]()
					case "stock":
						preview.Stock.Values[0].Available = domain.Known(int64(2))
					case "cancelled":
						p.reviewer.player.session.(*playerFakeSession).mu.Lock()
						p.reviewer.player.session.(*playerFakeSession).state.Snapshot.Native++
						p.reviewer.player.session.(*playerFakeSession).mu.Unlock()
					}
				}
			}
			result, err := p.Step(context.Background())
			if err == nil && result.Decision.Admitted {
				t.Fatal("invalid power work admitted", result)
			}
			plans, err := db.LoadPlans(context.Background(), 256)
			if err != nil || len(plans) != 2 {
				t.Fatal("partial power method", plans, err)
			}
		})
	}
}

func TestRoutinePowerMissingNativeComponentsPreventsGeneration(t *testing.T) {
	t.Parallel()
	p, db, n, _ := powerFixture(t, false)
	original := n.onPreview
	n.onPreview = func(ctx context.Context, preview *bridge.BuildingPreview) {
		original(ctx, preview)
		preview.Preview.Costs = domain.Known([]policy.Amount{{Resource: "Steel", Count: 100}, {Resource: "ComponentIndustrial", Count: 2}})
		preview.Stock.Values = append(preview.Stock.Values, policy.Stock{Resource: "ComponentIndustrial", Available: domain.Known(int64(0))})
	}
	result, err := p.Step(context.Background())
	// The stock refusal lends the bounded stock wait: the components may be
	// in a hauler's hands or on a bench (#66).
	if err != nil || result.Reason != BuildingMethodRefused || result.Decision.Admitted || result.NativeWorkTicks != stockWaitTicks {
		t.Fatal(result, err)
	}
	plans, err := db.LoadPlans(context.Background(), 256)
	if err != nil || len(plans) != 2 { // the guidance submission and the empty root plan
		t.Fatal("unfunded generation was journaled", plans, err)
	}
}
