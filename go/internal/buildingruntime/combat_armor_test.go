package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The frame's colonist armor fills SquadDefenderFacts.Armor; a
// row without it leaves the fact unknown.
func TestCombatViewFillsDefenderArmorFromFrame(t *testing.T) {
	var combat bridge.Combat
	rows := map[string]*n.PawnState{}
	for _, id := range []string{"a", "b"} {
		combat.Emergency.Facts.Colonists = append(combat.Emergency.Facts.Colonists, policy.EmergencyPawn{ID: policy.PawnID(id)})
		rows[id] = &n.PawnState{Pawn: &n.EntityRef{Id: proto.String(id)}}
	}
	combat.Pawns = []*mp.CombatPawn{{Id: proto.String("a"), Armor: proto.Float64(.65)}, {Id: proto.String("b")}}
	view := combatView(combat, combatInputs{rows: rows}, nil, domain.Fact[policy.CombatLayout]{})
	if len(view.Defenders) != 2 {
		t.Fatalf("%+v", view.Defenders)
	}
	if a, ok := view.Defenders[0].Armor.Value(); !ok || a != .65 {
		t.Fatalf("a armor %v %v", a, ok)
	}
	if _, ok := view.Defenders[1].Armor.Value(); ok {
		t.Fatalf("b armor known: %+v", view.Defenders[1].Armor)
	}
}
