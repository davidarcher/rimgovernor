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
func TestRoundsArmedCountsUnarmedFighters(t *testing.T) {
	row := func(id string, armed bool, tags ...string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(armed)}, Biography: &o.PawnBiography{DisabledWorkTags: tags}}
	}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(3)}
	e := policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}
	for _, id := range []string{"a", "b", "c"} {
		e.Colonists = append(e.Colonists, policy.EmergencyPawn{ID: policy.PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false)})
	}
	p := &o.PawnSnapshot{Pawns: []*o.PawnState{row("a", true), row("b", false), row("c", false, "Violent")}}
	armed, unarmed := roundsArmed(v, e, p, policy.CreepJoinerDownsides{})
	if a, k := armed.Value(); !k || a != 1 {
		t.Fatal(a, k)
	}
	if u, k := unarmed.Value(); !k || u != 1 {
		t.Fatal(u, k)
	}
	p.Pawns[1].Biography = nil
	if _, unarmed = roundsArmed(v, e, p, policy.CreepJoinerDownsides{}); func() bool { _, k := unarmed.Value(); return k }() {
		t.Fatal("missing biography gave a known unarmed count")
	}
}

// Defenders carry the wire's melee DPS scaled by health and ranged DPS;
// a missing stat leaves that fact unknown.
func TestRoundsDefendersReadObservedDPS(t *testing.T) {
	row := func(id string, ranged, melee, health *float64) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, Equipment: &o.PawnEquipment{RangedDps: ranged, MeleeDps: melee}, Health: &o.PawnHealth{SummaryFraction: health}, Biography: &o.PawnBiography{}}
	}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(2)}
	e := policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}
	for _, id := range []string{"a", "b"} {
		e.Colonists = append(e.Colonists, policy.EmergencyPawn{ID: policy.PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false)})
	}
	p := &o.PawnSnapshot{Pawns: []*o.PawnState{row("a", proto.Float64(10), proto.Float64(4), proto.Float64(0.5)), row("b", nil, proto.Float64(3), proto.Float64(1))}}
	got, known := roundsDefenders(v, e, p).Value()
	if !known || len(got) != 2 {
		t.Fatal(got, known)
	}
	if m, k := got[0].MeleePower.Value(); !k || m != 2 {
		t.Fatal(m, k)
	}
	if r, k := got[0].RangedDPS.Value(); !k || r != 10 {
		t.Fatal(r, k)
	}
	if _, k := got[1].RangedDPS.Value(); k {
		t.Fatal("missing ranged dps read as known")
	}
	if _, k := policy.DefenseCapacity(domain.Known(got), domain.Known([]policy.DefenseTurretFacts{})).Value(); k {
		t.Fatal("unknown ranged dps gave a known capacity")
	}
}
