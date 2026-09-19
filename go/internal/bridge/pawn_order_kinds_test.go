package bridge

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// Every undrafted order kind the native adapter implements passes the client
// contract with its fixed job def; the repair vertical (issue #2) was refused
// here as unsupported before it reached the native preview.
func TestPawnOrderCommandAcceptsEveryImplementedKind(t *testing.T) {
	for kind, jobDef := range map[o.PawnOrderKind]string{
		o.PawnOrderKind_PAWN_ORDER_KIND_TEND: "TendPatient", o.PawnOrderKind_PAWN_ORDER_KIND_RESCUE: "Rescue", o.PawnOrderKind_PAWN_ORDER_KIND_CAPTURE: "Capture",
		o.PawnOrderKind_PAWN_ORDER_KIND_HAUL: "HaulToCell", o.PawnOrderKind_PAWN_ORDER_KIND_EQUIP: "Equip", o.PawnOrderKind_PAWN_ORDER_KIND_CLEAN: "Clean", o.PawnOrderKind_PAWN_ORDER_KIND_REPAIR: "Repair", o.PawnOrderKind_PAWN_ORDER_KIND_OPEN_CASKET: "Open",
	} {
		command := &o.PawnTargetOrder{Pawn: &o.EntityPrecondition{EntityId: proto.String("pawn"), ExpectedSnapshotToken: proto.String("p")}, Target: &o.EntityPrecondition{EntityId: proto.String("thing"), ExpectedSnapshotToken: proto.String("t")}, Kind: kind.Enum(), RequireSafeStorage: proto.Bool(pawnOrderRequiresSafeStorage(kind))}
		if err := pawnOrderCommand(command); err != nil {
			t.Fatalf("%s: %v", kind, err)
		}
		if !pawnOrderJobDefAllowed(kind, jobDef) {
			t.Fatalf("%s: %s not allowed", kind, jobDef)
		}
	}
	unspecified := &o.PawnTargetOrder{Pawn: &o.EntityPrecondition{EntityId: proto.String("pawn"), ExpectedSnapshotToken: proto.String("p")}, Target: &o.EntityPrecondition{EntityId: proto.String("thing"), ExpectedSnapshotToken: proto.String("t")}, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_UNSPECIFIED.Enum(), RequireSafeStorage: proto.Bool(false)}
	if err := pawnOrderCommand(unspecified); err == nil {
		t.Fatal("unspecified kind accepted")
	}
}
