package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestPopulationCarriesTheCreepJoinerFacts: a colony prisoner's row
// says whether it has a creepjoiner tracker and its pawn kind; a failed
// tracker read leaves the fact unknown.
func TestPopulationCarriesTheCreepJoinerFacts(t *testing.T) {
	snapshot := populationReply(prisonerPerson("joiner", ""), prisonerPerson("plain", ""), prisonerPerson("unread", "")).GetObserved()
	pawns := populationPawns(snapshot)
	set := func(id string, edit func(*o.PawnState)) {
		row, _ := pawns.Row(&c.Ref{Id: proto.String(id)})
		edit(row)
	}
	set("joiner", func(r *o.PawnState) {
		r.KindDefName = proto.String("Colonist")
		r.Anomaly = &o.PawnAnomaly{Creepjoiner: &o.CreepJoinerState{DownsideTriggered: proto.Bool(false)}}
	})
	set("plain", func(r *o.PawnState) { r.Anomaly = &o.PawnAnomaly{} })
	set("unread", func(r *o.PawnState) {
		r.Anomaly = &o.PawnAnomaly{Issues: []*o.ReadIssue{{Field: proto.String("creepjoiner"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}
	})
	census, err := decodePopulation(snapshot, pawns, nil)
	if err != nil {
		t.Fatal(err)
	}
	rows, _ := census.Prisoners.Value()
	if len(rows) != 3 {
		t.Fatal(rows)
	}
	if v, known := rows[0].CreepJoiner.Value(); !known || !v || rows[0].Kind != "Colonist" {
		t.Fatalf("creepjoiner row %+v", rows[0])
	}
	if v, known := rows[1].CreepJoiner.Value(); !known || v {
		t.Fatalf("plain row %+v", rows[1])
	}
	if _, known := rows[2].CreepJoiner.Value(); known {
		t.Fatalf("unread row %+v", rows[2])
	}
}
