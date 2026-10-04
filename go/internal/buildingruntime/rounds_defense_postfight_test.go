package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func postFightPawn(id string, side mp.CombatSide, x int32, downed bool, target string) *mp.CombatPawn {
	return &mp.CombatPawn{Id: proto.String(id), Side: side.Enum(), Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(0)}, Downed: proto.Bool(downed), TargetId: proto.String(target)}
}

// The downed raiders are the downed humanlike hostiles, and the one to
// finish an addict is its stripper: the colonist whose job targets it,
// else the nearest standing colonist (#1079).
func TestPostFightRaidersAndStripper(t *testing.T) {
	hostile, colonist := mp.CombatSide_COMBAT_SIDE_HOSTILE, mp.CombatSide_COMBAT_SIDE_COLONIST
	combat := bridge.Combat{
		Pawns: []*mp.CombatPawn{
			postFightPawn("r2", hostile, 10, true, ""),
			postFightPawn("r1", hostile, 20, true, ""),
			postFightPawn("r3", hostile, 30, false, ""),
			postFightPawn("wolf", hostile, 40, true, ""),
			postFightPawn("far", colonist, 0, false, ""),
			postFightPawn("near", colonist, 12, false, ""),
			postFightPawn("down", colonist, 10, true, ""),
		},
		Detail: bridge.PawnsFromMap(map[string]*n.PawnState{"wolf": {Humanlike: proto.Bool(false)}}),
	}
	raiders := downedRaiders(combat)
	if len(raiders) != 2 || raiders[0].GetId() != "r1" || raiders[1].GetId() != "r2" {
		t.Fatal(raiders)
	}
	if got := stripper(combat, raiders[1]); got.GetId() != "near" {
		t.Fatal("nearest standing colonist", got.GetId())
	}
	combat.Pawns[4].TargetId = proto.String("r2")
	if got := stripper(combat, raiders[1]); got.GetId() != "far" {
		t.Fatal("the stripper at work", got.GetId())
	}
	if w, known := apparelWorn(&n.PawnState{Equipment: &n.PawnEquipment{}}).Value(); !known || w {
		t.Fatal("a bare raider reads worn")
	}
	if _, known := apparelWorn(nil).Value(); known {
		t.Fatal("an unread raider reads known")
	}
}
