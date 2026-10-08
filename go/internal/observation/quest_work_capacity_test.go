package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestQuestWorkerCapacityRequiresCompleteHomeRoster(t *testing.T) {
	read := &bridge.WorldProgressionRead{Maps: []bridge.WorldMap{{ID: 0, Home: true, PawnIDs: []string{"builder"}, QuestWorkers: []*o.QuestWorker{{PawnId: proto.String("builder"), HealthyAdult: proto.Bool(true), CanFight: proto.Bool(false), Rates: []*o.QuestWorkRate{{Stat: proto.String("ConstructionSpeed"), Rate: proto.Float64(.5)}}}}}}}
	rates, departures := frameQuestWorkerCapacity(read, 0)
	workers, known := rates.Value()
	if !known || len(workers) != 1 || workers[0].Rates["ConstructionSpeed"] != .5 {
		t.Fatal(workers, known)
	}
	pawns, known := departures.Value()
	if !known || len(pawns) != 1 {
		t.Fatal(pawns, known)
	}
	if fight, known := pawns[0].CanFight.Value(); !known || fight {
		t.Fatal(pawns)
	}
	read.Maps[0].Home = false
	_, departures = frameQuestWorkerCapacity(read, 0)
	if _, known := departures.Value(); !known {
		t.Fatal("current remote-map actors became unknown")
	}
	read.Maps[0].PawnIDs = append(read.Maps[0].PawnIDs, "unobserved")
	rates, _ = frameQuestWorkerCapacity(read, 0)
	if _, known := rates.Value(); known {
		t.Fatal("partial worker roster became known capacity")
	}
}
