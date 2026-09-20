package observation

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestHomeExtentCompleteUnbatchedAndAbsent(t *testing.T) {
	row := &o.HomeCoverageTarget{Id: proto.String("wall"), MissingCells: proto.Uint32(0), ExtentGeometry: &o.HomeExtentGeometry{}}
	for x := int32(0); x < 700; x++ {
		row.ExtentGeometry.EnclosedInterior = append(row.ExtentGeometry.EnclosedInterior, &c.Cell{X: proto.Int32(x), Z: proto.Int32(1)})
	}
	row.ExtentGeometry.Corridor = []*c.Cell{{X: proto.Int32(700), Z: proto.Int32(1)}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{HomeCoverage: &o.HomeCoverageSection{Outcome: &o.HomeCoverageSection_Observed{Observed: &o.HomeCoverageFacts{Targets: []*o.HomeCoverageTarget{row}}}}}}}}
	home, known := colonyHomeCoverage(v).Value()
	if !known {
		t.Fatal("missing census")
	}
	g, known := home.Targets[0].ExtentGeometry.Value()
	if !known || len(g.EnclosedInterior) != 700 || len(g.Corridor) != 1 {
		t.Fatalf("batched geometry: %+v", g)
	}
	row.ExtentGeometry = nil
	home, _ = colonyHomeCoverage(v).Value()
	if _, known = home.Targets[0].ExtentGeometry.Value(); known {
		t.Fatal("legacy batch became complete geometry")
	}
	row.ExtentGeometry = &o.HomeExtentGeometry{}
	home, _ = colonyHomeCoverage(v).Value()
	if _, known = home.Targets[0].ExtentGeometry.Value(); !known {
		t.Fatal("known empty geometry lost")
	}
}
