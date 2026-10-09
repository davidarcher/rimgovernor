package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A stream frame carries only the things its re-read sections referenced, so
// the held pawn rows' primary weapons resolve through the hold's things table.
// Tick 1 of lab-base: a rifleman whose pawn row came from the hold
// while the frame's own things table held nothing read as an unknown weapon,
// and the hold refused "no eligible ranged defender among 6".
func TestCombatFrameResolvesHeldPrimaryWeapons(t *testing.T) {
	const pawnID, rifleID = "Thing_Human8817", "Thing_Gun_AssaultRifle8918"
	ctx := authorityTestContext(771)
	equipment := &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String(rifleID),
		Equipped: []*o.GearItem{{Thing: &c.Ref{Id: proto.String(rifleID)}, HitPoints: proto.Int32(100), MaxHitPoints: proto.Int32(100), ConditionFraction: proto.Float64(1)}}}
	pawn := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(pawnID)}, Equipment: equipment}
	rifle := &o.Thing{Thing: &o.EntityRef{Id: proto.String(rifleID), DefName: proto.String("Gun_AssaultRifle")}}
	held := &heldTables{pawns: NewPawns(pawn), things: NewThings(rifle)}
	frame := &o.BundleSnapshot{Context: ctx, Pawns: &o.PawnSnapshot{Context: ctx}, Things: &o.ThingsSnapshot{Context: ctx},
		Emergency: &o.StatusSnapshot{Colonists: []*c.Ref{{Id: proto.String(pawnID)}}}}

	cut := combatFrameHeld(frame, held)
	if len(cut.GetThings().GetThings()) != 1 {
		t.Fatalf("held rifle missing from the combat frame: %v", cut.GetThings())
	}
	things := NewThings(cut.GetThings().GetThings()...)
	weapon, known, err := sharedRecordedCatalog(t).PrimaryWeapon(equipment, things)
	if err != nil || !known || !weapon.Ranged {
		t.Fatalf("rifle %+v known=%v err=%v", weapon, known, err)
	}
}
