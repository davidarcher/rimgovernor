package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestRoundsArmedOwesNoWeaponToAHeldBackCreepJoiner (#1740): an unarmed
// creepjoiner whose downside has not shown is no fighter to arm, so it does
// not hold EnsureBasicDefense open; once the downside shows it counts again.
func TestRoundsArmedOwesNoWeaponToAHeldBackCreepJoiner(t *testing.T) {
	row := func(id string, joiner bool, triggered bool) *o.PawnState {
		r := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}}
		if joiner {
			r.Anomaly = &o.PawnAnomaly{Entity: proto.Bool(false), Creepjoiner: &o.CreepJoinerState{DownsideTriggered: proto.Bool(triggered)}}
			r.Health = &o.PawnHealth{HediffCompleteness: &o.Completeness{Filtered: proto.Uint64(0)}}
		}
		return r
	}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(2)}
	e := policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}
	for _, id := range []string{"joiner", "other"} {
		e.Colonists = append(e.Colonists, policy.EmergencyPawn{ID: policy.PawnID(id), Dead: domain.Known(false), Downed: domain.Known(false)})
	}
	p := &o.PawnSnapshot{Pawns: []*o.PawnState{row("joiner", true, false), row("other", false, false)}}
	if _, unarmed := roundsArmed(v, e, p, policy.CreepJoinerDownsides{}); func() bool { n, k := unarmed.Value(); return !k || n != 1 }() {
		t.Fatal("only the ordinary colonist is owed a weapon", unarmed)
	}
	p.Pawns[0] = row("joiner", true, true)
	if _, unarmed := roundsArmed(v, e, p, policy.CreepJoinerDownsides{}); func() bool { n, k := unarmed.Value(); return !k || n != 2 }() {
		t.Fatal("a creepjoiner whose downside fired is owed a weapon again", unarmed)
	}
}
