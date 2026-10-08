package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func TestQuestCapacityCensusPreservesOfferDurationAndWork(t *testing.T) {
	v := worldProgressionFixture()
	v.Maps[0].QuestWorkers = []*o.QuestWorker{{PawnId: proto.String("pawn-1"), HealthyAdult: proto.Bool(true), CanFight: proto.Bool(true), Rates: []*o.QuestWorkRate{{Stat: proto.String("ConstructionSpeed"), Rate: proto.Float64(1.25)}}}}
	m := &o.QuestMonument{MarkerId: proto.String("marker-1"), DefName: proto.String("MonumentMarker"), MapId: proto.Int32(0), Offered: proto.Bool(true), Packed: proto.Bool(false), Installed: proto.Bool(false), ClearSite: proto.Bool(true), Pieces: []*o.QuestMonumentPiece{{DefName: proto.String("Wall"), Offset: &c.Cell{X: proto.Int32(0), Z: proto.Int32(0)}, Rotation: proto.Int32(0), Footprint: []*c.Cell{{X: proto.Int32(0), Z: proto.Int32(0)}}, BuildOptions: []*o.QuestMonumentBuildOption{{Stuff: proto.String("BlocksGranite"), Work: proto.Float64(100), Costs: []*o.Quantity{{DefName: proto.String("BlocksGranite"), Units: proto.Int64(5)}}}}}}}
	v.Quests[0].Objectives = []*o.QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_MONUMENT.Enum(), DurationTicks: proto.Int64(60000), Monument: m}}
	out, err := worldProgressionSelected(v, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	objective := out.Quests[0].Objectives[0]
	if objective.Monument == nil || !objective.Monument.GetOffered() || objective.DurationTicks == nil || *objective.DurationTicks != 60000 || objective.DeadlineTicks != nil {
		t.Fatal(objective)
	}
	if out.Maps[0].QuestWorkers[0].Rates[0].GetRate() != 1.25 {
		t.Fatal(out.Maps[0])
	}
}

func TestQuestCapacityCensusRejectsMalformedRates(t *testing.T) {
	for name, rate := range map[string]*o.QuestWorkRate{"missing": {Stat: proto.String("ConstructionSpeed")}, "negative": {Stat: proto.String("ConstructionSpeed"), Rate: proto.Float64(-1)}, "nan": {Stat: proto.String("ConstructionSpeed"), Rate: proto.Float64(math.NaN())}} {
		t.Run(name, func(t *testing.T) {
			v := worldProgressionFixture()
			v.Maps[0].QuestWorkers = []*o.QuestWorker{{PawnId: proto.String("pawn-1"), Rates: []*o.QuestWorkRate{rate}}}
			if _, err := worldProgressionSelected(v, pbIdentity()); err == nil {
				t.Fatal("accepted invalid work rate")
			}
		})
	}
}
