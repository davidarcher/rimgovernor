package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type temperatureNative struct {
	*powerNative
	rooms     *o.ListRoomsReply
	roomReads int
	roomErr   error
	onRooms   func()
}

func TestTemperatureReadIsOptInAndBracketRejectsLateResults(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"disabled", "expired", "cancelled", "generation"} {
		t.Run(mode, func(t *testing.T) {
			p, db, n, _ := temperatureFixture(t, false)
			before, err := db.LoadRoutineReview(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			reads := n.roomReads
			switch mode {
			case "disabled":
				p.reviewer.methods = domain.Known([]policy.GoalID{})
			case "expired":
				n.onRooms = func() { p.reviewer.clock.(*testkit.ManualClock).Advance(time.Minute) }
			case "cancelled":
				n.onRooms = cancel
			case "generation":
				n.rooms.GetObserved().Context.NativeGeneration = proto.Uint64(n.rooms.GetObserved().Context.GetNativeGeneration() + 1)
			}
			_, err = p.reviewer.Step(ctx)
			if mode == "disabled" {
				if err != nil || n.roomReads != reads {
					t.Fatal(n.roomReads, err)
				}
				return
			}
			if err == nil {
				t.Fatal("late thermal observation was published")
			}
			after, err := db.LoadRoutineReview(context.Background())
			if err != nil || after.Revision != before.Revision {
				t.Fatal(after, err)
			}
		})
	}
}

func TestTemperatureNativeWorkBudgetRequiresCompletedCurrentDirection(t *testing.T) {
	t.Parallel()
	for _, definition := range []string{"Campfire", "PassiveCooler", "Wall"} {
		building, _ := domain.NewBuilding(definition, domain.Cell{X: 1, Z: 1}, domain.North, "")
		action, _ := domain.NewBuildingAction("thermal", building)
		spec, _ := domain.NewPlan("thermal-plan", 1, []domain.Action{action})
		progress, _ := domain.NewProgress(spec, action.ID())
		snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Native: 1, Plan: spec.ID(), Revision: 1}
		state := store.PlanState{Spec: spec, Progress: []domain.Progress{progress}}
		if temperatureNativeWorkTicks(state, snapshot, 100) != 0 {
			t.Fatal("pending construction granted time")
		}
		var err error
		progress, err = progress.Prepare(snapshot, 1)
		if err != nil {
			t.Fatal(err)
		}
		progress, err = progress.MarkDispatched(snapshot, 1)
		if err != nil {
			t.Fatal(err)
		}
		progress, err = progress.RecordReceipt(1, domain.ReceiptAccepted)
		if err != nil {
			t.Fatal(err)
		}
		state.Progress[0] = progress
		if temperatureNativeWorkTicks(state, snapshot, 100) != 0 {
			t.Fatal("receipt granted time")
		}
		progress, err = progress.Observe(domain.Observation{Action: action.ID(), Attempt: 1, Snapshot: snapshot, Tick: 100, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot)
		if err != nil {
			t.Fatal(err)
		}
		state.Progress[0] = progress
		for _, row := range []struct {
			tick domain.Tick
			want uint32
		}{{99, 0}, {100, 120}, {10099, 1}, {10100, 0}} {
			want := row.want
			if definition == "Wall" {
				want = 0
			}
			if got := temperatureNativeWorkTicks(state, snapshot, row.tick); got != want {
				t.Fatal(definition, row, got)
			}
		}
		// A reacquired direction keeps the budget (the heater was re-observed
		// under it); a reloaded world does not.
		snapshot.Native++
		if got := temperatureNativeWorkTicks(state, snapshot, 100); got != 120 && definition != "Wall" {
			t.Fatal("new direction lost heat-exchange budget", got)
		}
		snapshot.Load = "reload"
		if temperatureNativeWorkTicks(state, snapshot, 100) != 0 {
			t.Fatal("reloaded world inherited heat-exchange budget")
		}
	}
}

func (n *temperatureNative) ReadTemperatureRooms(context.Context, *c.Identity) (*o.ListRoomsReply, bridge.Result, error) {
	n.roomReads++
	if n.onRooms != nil {
		n.onRooms()
	}
	return n.rooms, bridge.Result{}, n.roomErr
}

func temperatureFixture(t *testing.T, hot bool) (*RoutineBuildingPlanner, *store.Store, *temperatureNative, store.ControlRequest) {
	t.Helper()
	base, db, power, request := powerFixture(t, false)
	n := &temperatureNative{powerNative: power}
	v := n.reply.GetObserved()
	count := func(n uint64) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	bed := &o.EntityRef{Id: proto.String("bed"), DefName: proto.String("SleepingSpot"), MapId: proto.Int32(0), Position: cell(0, 0)}
	temp := 5.0
	if hot {
		temp = 36
	}
	room := &o.RoomState{Id: proto.String("42"), ProperRoom: proto.Bool(true), Doorway: proto.Bool(false), Outdoors: proto.Bool(false), PsychologicallyOutdoors: proto.Bool(false), TouchesMapEdge: proto.Bool(false), OpenRoofCount: proto.Uint32(0), CellCount: proto.Uint32(4), TemperatureC: proto.Float64(temp), Center: cell(0, 0), Extents: &o.Rectangle{Minimum: cell(0, 0), Maximum: cell(1, 1)}, Cells: []*c.Cell{cell(0, 0), cell(0, 1), cell(1, 0), cell(1, 1)}, CellsCompleteness: count(4), Contents: []*o.Quantity{{DefName: proto.String("SleepingSpot"), Units: proto.Int64(1)}}, ContentsCompleteness: count(1), Beds: []*o.BuildingState{{Building: bed, Status: proto.String("built")}}}
	n.rooms = &o.ListRoomsReply{Outcome: &o.ListRoomsReply_Observed{Observed: &o.RoomsSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: count(1), Rooms: []*o.RoomState{room}}}}
	v.Upkeep = &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{Beds: []*o.UpkeepBed{{Bed: bed, Slots: proto.Uint32(1), Humanlike: proto.Bool(true), Medical: proto.Bool(false), Prisoners: proto.Bool(false), Roofed: proto.Bool(true), TemperatureC: proto.Float64(temp)}}}}}
	v.Upkeep.GetObserved().Completeness = count(1)
	v.Upkeep.GetObserved().Comfort = &o.ComfortSection{Outcome: &o.ComfortSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}
	// Safe reachable bed temperature can disappear while the actual room persists.
	v.SleepingTemperatureMinC, v.SleepingTemperatureMaxC = nil, nil
	for _, name := range []string{"Campfire", "PassiveCooler"} {
		v.Planning.GetObserved().Definitions = append(v.Planning.GetObserved().Definitions, &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String(name)}, Available: proto.Bool(true), ConstructionSkill: proto.Int32(4), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(1)}})
	}
	v.Planning.GetObserved().Completeness = count(uint64(len(v.Planning.GetObserved().Definitions)))
	base.reviewer.native = n
	base.reviewer.methods = domain.Known([]policy.GoalID{policy.EnsureTemperatureSafety})
	if _, err := base.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineTemperaturePlanner(base.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	return planner, db, n, request
}

func TestTemperatureSharedMethodPlacementAndManual(t *testing.T) {
	t.Parallel()
	for _, hot := range []bool{false, true} {
		t.Run(map[bool]string{false: "cold", true: "hot"}[hot], func(t *testing.T) {
			p, db, n, request := temperatureFixture(t, hot)
			result, err := p.Step(context.Background())
			if err != nil || result.Reason != BuildingMethodAdmitted {
				t.Fatal(result, err)
			}
			plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
			if err != nil || len(plan.Progress) != 1 || len(plan.Admissions) != 1 {
				t.Fatal(plan, err)
			}
			b, _ := plan.Progress[0].Action().Building()
			want := "Campfire"
			if hot {
				want = "PassiveCooler"
			}
			if b.Definition() != want || b.Cell().X > 1 || b.Cell().Z > 1 || plan.Progress[0].View().Attempt != 0 || result.Decision.Goal.Goal.Need != domain.NeedDeficit {
				t.Fatal(b, plan.Progress, result)
			}
			if next, err := p.Step(context.Background()); err != nil || next.Reason != BuildingMethodExistingWork || n.previews != 1 {
				t.Fatal(next, err)
			}
			request.Kind, request.RequestID = store.PauseControl, "manual-temperature"
			if _, err := p.reviewer.player.Pause(context.Background(), request); err != nil {
				t.Fatal(err)
			}
			plan, err = db.LoadPlan(context.Background(), plan.Spec.ID())
			if err != nil || plan.Progress[0].View().Stage != domain.Pending {
				t.Fatal(plan, err)
			}
			before := n.roomReads
			if next, err := p.Step(context.Background()); err != nil || next.Reason != BuildingMethodDisabled || n.roomReads != before {
				t.Fatal(next, err)
			}
		})
	}
}

func TestTemperatureUnknownExistingFacilityAndRecoveredRoom(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"unavailable", "existing", "recovered", "stale", "skill", "spill", "stock"} {
		t.Run(mode, func(t *testing.T) {
			p, db, n, _ := temperatureFixture(t, false)
			room := n.rooms.GetObserved().Rooms[0]
			switch mode {
			case "unavailable":
				n.roomErr = bridge.ErrUnavailable
			case "existing":
				room.Contents = append(room.Contents, &o.Quantity{DefName: proto.String("Campfire"), Units: proto.Int64(1)})
				room.ContentsCompleteness.Matched = proto.Uint64(2)
				room.ContentsCompleteness.Returned = proto.Uint64(2)
			case "recovered":
				room.TemperatureC = proto.Float64(18)
			case "stale":
				n.rooms.GetObserved().Context.Tick = proto.Int64(n.rooms.GetObserved().Context.GetTick() + int64(domain.PlanningTickTolerance) + 1)
			case "skill":
				n.pawnReply.GetObserved().Pawns[0].Biography.Skills[0].Level = proto.Int32(3)
			case "spill":
				original := n.onPreview
				n.onPreview = func(ctx context.Context, preview *bridge.BuildingPreview) {
					original(ctx, preview)
					footprint, _ := preview.Preview.Footprint.Value()
					preview.Preview.Footprint = domain.Known(append(footprint, domain.Cell{X: 2, Z: 2}))
				}
			case "stock":
				original := n.onPreview
				n.onPreview = func(ctx context.Context, preview *bridge.BuildingPreview) {
					original(ctx, preview)
					preview.Stock.Values[0].Available = domain.Known(int64(0))
				}
			}
			// Planners plan from the review's census, so the review must
			// observe the mutation before the planner steps (#75).
			if _, err := p.reviewer.Step(context.Background()); mode == "stale" {
				if err == nil {
					t.Fatal("stale room reviewed")
				}
				return
			} else if err != nil {
				t.Fatal(err)
			}
			result, err := p.Step(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			// Only a stock refusal lends the bounded stock wait; a spilled
			// footprint or a missing builder is not resolved by ticks (#66).
			wait := uint32(0)
			if mode == "stock" {
				wait = stockWaitTicks
			}
			if result.Decision.Admitted || result.NativeWorkTicks != wait {
				t.Fatal(result)
			}
			plans, err := db.LoadPlans(context.Background(), 256)
			if err != nil || len(plans) != 2 {
				t.Fatal(plans, err)
			}
			if mode == "existing" && result.Reason != RoutineBuildingReason(policy.TemperatureWait) {
				t.Fatal(result)
			}
			if mode == "unavailable" || mode == "recovered" {
				review, err := p.reviewer.Step(context.Background())
				if err != nil {
					t.Fatal(err)
				}
				want := domain.NeedUnknown
				if mode == "recovered" {
					want = domain.NeedRecovered
				}
				found := false
				for _, binding := range review.Review.Goals {
					if binding.Need == policy.EnsureTemperatureSafety {
						g, err := db.LoadGoal(context.Background(), binding.Goal)
						if err != nil || mode == "recovered" && g.Goal.Need != want || mode == "unavailable" && g.Goal.Need == domain.NeedRecovered {
							t.Fatal(g, err)
						}
						found = true
					}
				}
				if !found {
					t.Fatal("missing maintained thermal goal")
				}
			}
		})
	}
}
