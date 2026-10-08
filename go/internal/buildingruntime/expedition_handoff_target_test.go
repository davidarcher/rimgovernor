package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func handoffPlan(t *testing.T, current domain.GenerationSnapshot, tile int32, method string) store.PlanState {
	t.Helper()
	departure, err := domain.NewCaravanDeparture([]domain.PawnID{"a", "b"}, nil, tile)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewCaravanDepartureAction("depart", departure)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("journey", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := domain.NewProgress(spec, action.ID())
	if err != nil {
		t.Fatal(err)
	}
	current.Plan = spec.ID()
	progress, err = progress.Prepare(current, 1)
	if err != nil {
		t.Fatal(err)
	}
	progress, err = progress.MarkDispatched(current, 1)
	if err != nil {
		t.Fatal(err)
	}
	progress, err = progress.RecordReceipt(progress.View().Attempt, domain.ReceiptAccepted)
	if err != nil {
		t.Fatal(err)
	}
	return store.PlanState{Retired: true, Method: domain.MethodID(method), Spec: spec, Progress: []domain.Progress{progress}}
}

func TestExpeditionHandoffRequiresPhysicalExactJournalDestination(t *testing.T) {
	current := controlScope()
	current.Native = 7
	plan := handoffPlan(t, current, 42, "quest-expedition-q-site-0")
	world := &bridge.WorldProgressionRead{Maps: []bridge.WorldMap{{ID: 77, Tile: 42, PawnIDs: []string{"a", "b"}}}, Sites: []*o.WorldSite{{Id: proto.String("site"), MapId: proto.Int32(77), Tile: proto.Int32(42), QuestIds: []string{"q"}}}}
	if target, ok := expeditionHandoffTarget(current, world, []store.PlanState{plan}); !ok || target.Map != 77 {
		t.Fatalf("physical site arrival not recognized: %+v %v", target, ok)
	}
	for _, change := range []string{"crew", "quest", "load", "source", "destination", "receipt"} {
		t.Run(change, func(t *testing.T) {
			copyWorld := *world
			copyWorld.Maps = append([]bridge.WorldMap(nil), world.Maps...)
			copyWorld.Sites = []*o.WorldSite{proto.Clone(world.Sites[0]).(*o.WorldSite)}
			copyPlan := plan
			scope := current
			switch change {
			case "crew":
				copyWorld.Maps[0].PawnIDs = []string{"a"}
			case "quest":
				copyWorld.Sites[0].QuestIds = []string{"other"}
			case "load":
				scope.Load = "other"
			case "source":
				scope.Map = 9
			case "destination":
				copyWorld.Maps[0].Tile = 43
			case "receipt":
				copyPlan.Progress = nil
			}
			if _, ok := expeditionHandoffTarget(scope, &copyWorld, []store.PlanState{copyPlan}); ok {
				t.Fatal("unproven scope handoff accepted")
			}
		})
	}
	current.Map = 77
	homePlan := handoffPlan(t, current, 1, "quest-site-q-site-return-site-0")
	world.Maps = []bridge.WorldMap{{ID: 0, Tile: 1, Home: true, PawnIDs: []string{"home", "a", "b"}}}
	if target, ok := expeditionHandoffTarget(current, world, []store.PlanState{homePlan}); !ok || target.Map != 0 {
		t.Fatal("return handoff lost retained journal")
	}
}
