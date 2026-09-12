package observation

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestAnimalCensusPreservesUnknownAndKnownFalse(t *testing.T) {
	u := &o.UpkeepFacts{Animals: []*o.AnimalFeed{{Pawn: &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("animal"), DefName: proto.String("Muffalo")}, AnimalState: &o.AnimalState{Contained: proto.Bool(false), Release: proto.Bool(false), Slaughter: proto.Bool(false)}}, RequiresPen: proto.Bool(true)}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	rows, known := colonyAnimals(v).Value()
	if !known || len(rows) != 1 {
		t.Fatal(rows, known)
	}
	if value, known := rows[0].Contained.Value(); !known || value {
		t.Fatal("false containment lost")
	}
	u.Animals[0].Pawn.AnimalState.Contained = nil
	rows, _ = colonyAnimals(v).Value()
	if _, known := rows[0].Contained.Value(); known {
		t.Fatal("absent containment became false")
	}
	u.Issues = []*o.ReadIssue{{Field: proto.String("animals")}}
	if _, known := colonyAnimals(v).Value(); known {
		t.Fatal("unavailable census became empty")
	}
	u.Issues = nil
	u.Animals = nil
	if rows, known := colonyAnimals(v).Value(); !known || len(rows) != 0 {
		t.Fatal(rows, known)
	}
	v.Upkeep = nil
	if _, known := colonyAnimals(v).Value(); known {
		t.Fatal("missing upkeep became empty")
	}
}
