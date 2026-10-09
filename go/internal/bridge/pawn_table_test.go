package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func pawnTableFixture() *o.PawnSnapshot {
	return &o.PawnSnapshot{Context: pbContext(), Completeness: &o.Completeness{}, MeditateAssignmentAvailable: proto.Bool(true), Pawns: []*o.PawnState{
		{Pawn: &o.EntityRef{Id: proto.String("colonist")}, Colonist: proto.Bool(true), Needs: &o.PawnNeeds{Mood: proto.Float64(.5)}, Settings: &o.PawnSettings{Schedule: []*o.TimetableSlot{}}},
		{Pawn: &o.EntityRef{Id: proto.String("dog")}, Animal: proto.Bool(true), AnimalState: &o.AnimalState{PenId: proto.String("pen"), Contained: proto.Bool(true), Training: []*o.TrainingEntry{{DefName: proto.String("Obedience")}}}},
		{Pawn: &o.EntityRef{Id: proto.String("visitor")}},
	}}
}

// The pawn table holds every spawned pawn with the detail its kind
// carries, and refuses a malformed row of any kind.
func TestPawnTableValidatesEveryKind(t *testing.T) {
	pawns, err := PawnTable(pawnTableFixture(), pbIdentity())
	if err != nil || pawns.Len() != 3 {
		t.Fatal(pawns, err)
	}
	if row, ok := pawns.Row(&o.EntityRef{Id: proto.String("dog")}); !ok || row.GetAnimalState().GetPenId() != "pen" {
		t.Fatal(row, ok)
	}
	if _, ok := pawns.Row(&o.EntityRef{Id: proto.String("gone")}); ok {
		t.Fatal("unheld reference resolved")
	}
	for _, mutate := range []func(*o.PawnSnapshot){
		func(v *o.PawnSnapshot) { v.Pawns = append(v.Pawns, v.Pawns[0]) },
		func(v *o.PawnSnapshot) { v.Pawns[1].AnimalState.Contained = proto.Bool(false) },
		func(v *o.PawnSnapshot) {
			v.Pawns[1].AnimalState.Training = append(v.Pawns[1].AnimalState.Training, v.Pawns[1].AnimalState.Training[0])
		},
		func(v *o.PawnSnapshot) { v.Pawns[2].Pawn.Id = proto.String("") },
		func(v *o.PawnSnapshot) { v.Context.Identity.MapId = proto.Int32(4) },
	} {
		v := pawnTableFixture()
		mutate(v)
		if _, err := PawnTable(v, pbIdentity()); err == nil {
			t.Fatal("invalid pawn table accepted", v)
		}
	}
	if pawns, err := PawnTable(nil, pbIdentity()); err != nil || pawns.Len() != 0 {
		t.Fatal("a frame without a pawn table is an empty table", pawns, err)
	}
}

// The routine list read is the census colonists' table rows in id order;
// a census colonist the table lacks leaves it unserved.
func TestRoundsPawnsFromTable(t *testing.T) {
	census := EmergencyObservation{}
	census.Facts.Colonists = []policy.EmergencyPawn{{ID: "colonist"}}
	census.Facts.ColonistsComplete = domain.Known(true)
	out, ok := roundsPawns(pawnTableFixture(), census)
	if !ok || len(out.Pawns) != 1 || out.Pawns[0].Pawn.GetId() != "colonist" || !out.GetMeditateAssignmentAvailable() {
		t.Fatal(out, ok)
	}
	census.Facts.Colonists = append(census.Facts.Colonists, policy.EmergencyPawn{ID: "absent"})
	if _, ok := roundsPawns(pawnTableFixture(), census); ok {
		t.Fatal("a colonist missing from the table was served")
	}
}

// A population person the pawn table lacks leaves the person facts unknown
// until a later frame; the owned names stay known.
func TestPopulationUnresolvedPersonIsUnknown(t *testing.T) {
	v := populationReply(prisonerPerson("p", "")).GetObserved()
	v.OwnedNames = []*o.OwnedName{{PawnId: proto.String("p"), ShortName: proto.String("P"), ThingId: proto.Int32(1)}}
	census, err := decodePopulation(v, Pawns{}, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := census.Prisoners.Value(); known {
		t.Fatal("prisoners known without the pawn row")
	}
	if _, known := census.Custody.Value(); known {
		t.Fatal("custody known without the pawn row")
	}
	if names, known := census.Names.Value(); !known || len(names) != 1 {
		t.Fatal("owned names lost", names)
	}
	census, err = decodePopulation(v, populationPawns(v), nil)
	if rows, known := census.Prisoners.Value(); err != nil || !known || len(rows) != 1 {
		t.Fatal(rows, err)
	}
}
