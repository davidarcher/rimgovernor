package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// Unarmed counts fighting-capable colonists only; a pacifist is not owed a
// weapon, and a row without its biography leaves the count unknown.
func TestRoutineArmedCountsUnarmedFighters(t *testing.T) {
	row := func(id string, armed bool, tags ...string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(armed)}, Biography: &o.PawnBiography{DisabledWorkTags: tags}}
	}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(3)}
	e := policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}
	for _, id := range []string{"a", "b", "c"} {
		e.Colonists = append(e.Colonists, policy.EmergencyPawn{ID: policy.PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false)})
	}
	p := &o.PawnSnapshot{Pawns: []*o.PawnState{row("a", true), row("b", false), row("c", false, "Violent")}}
	armed, unarmed := routineArmed(v, e, p)
	if a, k := armed.Value(); !k || a != 1 {
		t.Fatal(a, k)
	}
	if u, k := unarmed.Value(); !k || u != 1 {
		t.Fatal(u, k)
	}
	p.Pawns[1].Biography = nil
	if _, unarmed = routineArmed(v, e, p); func() bool { _, k := unarmed.Value(); return k }() {
		t.Fatal("missing biography gave a known unarmed count")
	}
}
